using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Events;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.AppBase
{
    /// <summary>
    /// Class BaseConfigurationManager
    /// </summary>
    public abstract class BaseConfigurationManager : IConfigurationManager
    {
        /// <summary>
        /// Gets the type of the configuration.
        /// </summary>
        /// <value>The type of the configuration.</value>
        protected abstract Type ConfigurationType { get; }

        /// <summary>
        /// Occurs when [configuration updated].
        /// </summary>
        public event EventHandler<EventArgs> ConfigurationUpdated;

        /// <summary>
        /// Occurs when [configuration updating].
        /// </summary>
        public event EventHandler<ConfigurationUpdateEventArgs> NamedConfigurationUpdating;

        /// <summary>
        /// Occurs when [named configuration updated].
        /// </summary>
        public event EventHandler<ConfigurationUpdateEventArgs> NamedConfigurationUpdated;

        /// <summary>
        /// Gets the logger.
        /// </summary>
        /// <value>The logger.</value>
        protected ILogger Logger { get; private set; }
        /// <summary>
        /// Gets the XML serializer.
        /// </summary>
        /// <value>The XML serializer.</value>
        protected IXmlSerializer XmlSerializer { get; private set; }

        /// <summary>
        /// Gets or sets the application paths.
        /// </summary>
        /// <value>The application paths.</value>
        public IApplicationPaths CommonApplicationPaths { get; private set; }
        public readonly IFileSystem FileSystem;

        /// <summary>
        /// The _configuration loaded
        /// </summary>
        private bool _configurationLoaded;
        /// <summary>
        /// The _configuration sync lock
        /// </summary>
        private object _configurationSyncLock = new object();
        /// <summary>
        /// The _configuration
        /// </summary>
        private BaseApplicationConfiguration _configuration;
        /// <summary>
        /// Gets the system configuration
        /// </summary>
        /// <value>The configuration.</value>
        public BaseApplicationConfiguration CommonConfiguration
        {
            get
            {
                // Lazy load
                LazyInitializer.EnsureInitialized(ref _configuration, ref _configurationLoaded, ref _configurationSyncLock, () => (BaseApplicationConfiguration)ConfigurationHelper.GetXmlConfiguration(ConfigurationType, CommonApplicationPaths.SystemConfigurationFilePath, XmlSerializer, FileSystem));
                return _configuration;
            }
            protected set
            {
                _configuration = value;

                _configurationLoaded = value != null;
            }
        }

        private ConfigurationStore[] _configurationStores = { };
        private IConfigurationFactory[] _configurationFactories = { };

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseConfigurationManager" /> class.
        /// </summary>
        /// <param name="applicationPaths">The application paths.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="xmlSerializer">The XML serializer.</param>
        /// <param name="fileSystem">The file system</param>
        protected BaseConfigurationManager(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory, IXmlSerializer xmlSerializer, IFileSystem fileSystem)
        {
            CommonApplicationPaths = applicationPaths;
            XmlSerializer = xmlSerializer;
            FileSystem = fileSystem;
            Logger = loggerFactory.CreateLogger(GetType().Name);

            UpdateCachePath();
        }

        public virtual void AddParts(IEnumerable<IConfigurationFactory> factories)
        {
            _configurationFactories = factories.ToArray();

            _configurationStores = _configurationFactories
                .SelectMany(i => i.GetConfigurations())
                .ToArray();
        }

        /// <summary>
        /// Saves the configuration.
        /// </summary>
        public void SaveConfiguration()
        {
            Logger.LogInformation("Saving system configuration");
            var path = CommonApplicationPaths.SystemConfigurationFilePath;

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            lock (_configurationSyncLock)
            {
                XmlSerializer.SerializeToFile(CommonConfiguration, path);
            }

            OnConfigurationUpdated();
        }

        /// <summary>
        /// Called when [configuration updated].
        /// </summary>
        protected virtual void OnConfigurationUpdated()
        {
            UpdateCachePath();

            EventHelper.QueueEventIfNotNull(ConfigurationUpdated, this, EventArgs.Empty, Logger);
        }

        /// <summary>
        /// Replaces the configuration.
        /// </summary>
        /// <param name="newConfiguration">The new configuration.</param>
        /// <exception cref="ArgumentNullException">newConfiguration</exception>
        public virtual void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration)
        {
            if (newConfiguration == null)
            {
                throw new ArgumentNullException(nameof(newConfiguration));
            }

            ValidateCachePath(newConfiguration);

            CommonConfiguration = newConfiguration;
            SaveConfiguration();
        }

        /// <summary>
        /// Updates the items by name path.
        /// </summary>
        private void UpdateCachePath()
        {
            string cachePath;
            // If the configuration file has no entry (i.e. not set in UI)
            if (string.IsNullOrWhiteSpace(CommonConfiguration.CachePath))
            {
                // If the current live configuration has no entry (i.e. not set on CLI/envvars, during startup)
                if (string.IsNullOrWhiteSpace(((BaseApplicationPaths)CommonApplicationPaths).CachePath))
                {
                    // Set cachePath to a default value under ProgramDataPath
                    cachePath = (((BaseApplicationPaths)CommonApplicationPaths).ProgramDataPath + "/cache");
                }
                else
                {
                    // Set cachePath to the existing live value; will require restart if UI value is removed (but not replaced)
                    // TODO: Figure out how to re-grab this from the CLI/envvars while running
                    cachePath = ((BaseApplicationPaths)CommonApplicationPaths).CachePath;
                }
            }
            else
            {
                // Set cachePath to the new UI-set value
                cachePath = CommonConfiguration.CachePath;
            }

            Logger.LogInformation("Setting cache path to " + cachePath);
            ((BaseApplicationPaths)CommonApplicationPaths).CachePath = cachePath;
        }

        /// <summary>
        /// Replaces the cache path.
        /// </summary>
        /// <param name="newConfig">The new configuration.</param>
        /// <exception cref="DirectoryNotFoundException"></exception>
        private void ValidateCachePath(BaseApplicationConfiguration newConfig)
        {
            var newPath = newConfig.CachePath;

            if (!string.IsNullOrWhiteSpace(newPath)
                && !string.Equals(CommonConfiguration.CachePath ?? string.Empty, newPath))
            {
                // Validate
                if (!Directory.Exists(newPath))
                {
                    throw new FileNotFoundException(string.Format("{0} does not exist.", newPath));
                }

                EnsureWriteAccess(newPath);
            }
        }

        protected void EnsureWriteAccess(string path)
        {
            var file = Path.Combine(path, Guid.NewGuid().ToString());
            File.WriteAllText(file, string.Empty);
            FileSystem.DeleteFile(file);
        }

        private readonly ConcurrentDictionary<string, object> _configurations = new ConcurrentDictionary<string, object>();

        private string GetConfigurationFile(string key)
        {
            return Path.Combine(CommonApplicationPaths.ConfigurationDirectoryPath, key.ToLowerInvariant() + ".xml");
        }

        public object GetConfiguration(string key)
        {
            return _configurations.GetOrAdd(key, k =>
            {
                var file = GetConfigurationFile(key);

                var configurationInfo = _configurationStores
                    .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

                if (configurationInfo == null)
                {
                    throw new ResourceNotFoundException("Configuration with key " + key + " not found.");
                }

                var configurationType = configurationInfo.ConfigurationType;

                lock (_configurationSyncLock)
                {
                    return LoadConfiguration(file, configurationType);
                }
            });
        }

        private object LoadConfiguration(string path, Type configurationType)
        {
            if (!File.Exists(path))
            {
                return Activator.CreateInstance(configurationType);
            }

            try
            {
                return XmlSerializer.DeserializeFromFile(configurationType, path);
            }
            catch (IOException)
            {
                return Activator.CreateInstance(configurationType);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading configuration file: {path}", path);

                return Activator.CreateInstance(configurationType);
            }
        }

        public void SaveConfiguration(string key, object configuration)
        {
            var configurationStore = GetConfigurationStore(key);
            var configurationType = configurationStore.ConfigurationType;

            if (configuration.GetType() != configurationType)
            {
                throw new ArgumentException("Expected configuration type is " + configurationType.Name);
            }

            var validatingStore = configurationStore as IValidatingConfiguration;
            if (validatingStore != null)
            {
                var currentConfiguration = GetConfiguration(key);

                validatingStore.Validate(currentConfiguration, configuration);
            }

            NamedConfigurationUpdating?.Invoke(this, new ConfigurationUpdateEventArgs
            {
                Key = key,
                NewConfiguration = configuration
            });

            _configurations.AddOrUpdate(key, configuration, (k, v) => configuration);

            var path = GetConfigurationFile(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            lock (_configurationSyncLock)
            {
                XmlSerializer.SerializeToFile(configuration, path);
            }

            OnNamedConfigurationUpdated(key, configuration);
        }

        protected virtual void OnNamedConfigurationUpdated(string key, object configuration)
        {
            NamedConfigurationUpdated?.Invoke(this, new ConfigurationUpdateEventArgs
            {
                Key = key,
                NewConfiguration = configuration
            });
        }

        public Type GetConfigurationType(string key)
        {
            return GetConfigurationStore(key)
                .ConfigurationType;
        }

        private ConfigurationStore GetConfigurationStore(string key)
        {
            return _configurationStores
                .First(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
