// AppConfig.cs
using System;
using System.IO;
using System.Windows.Forms;

namespace SCT_Updater
{
    public static class AppConfig
    {
        // --- App Constants ---
        public const string LOCAL_VERSIONS_FILE = "local_versions.json";
        public const string UPDATER_ID = "suite_updater";
        public const string UPDATE_STUB_FILENAME = "update_stub.bat";
        public const int MAX_PARALLEL_DOWNLOADS = 20;

        // --- Package modes (file manifest "package_mode") ---
        public const string PACKAGE_MODE_ZIP = "zip";
        public const string PACKAGE_MODE_APP = "app";

        // --- Python modules ("app" packages), relative to the application folder ---
        public const string APPS_DIR = "apps";
        public const string PYTHON_RUNTIME_DIR = "apps/python_runtime";
        public const string WHEELHOUSE_DIR = "apps/wheelhouse";
        public const string DEFAULT_WHEELS_URL = "runtime/wheels";

        // --- NEW: Config Constants ---
        public const string LOCAL_DEVICE_CONFIGS_PATH = "config/device_configs";
        public const string DEVICE_CONFIGS_ID = "device_configs";

        // --- Nextcloud Path ---
        private const string NC_FILES_PATH = "/SCT/Updater";
        private const string NC_DEVICE_CONFIGS_PATH_SUFFIX = "/device_configs";


        // --- Loaded from .env ---
        public static string NC_USER { get; private set; }
        public static string NC_PASSWORD { get; private set; }
        public static string NC_SERVER_URL { get; private set; }

        /// <summary>True when <c>NC_CHANNEL=dev</c> is set in .env; stable otherwise.</summary>
        public static bool IsDevChannel { get; private set; }

        // --- Derived properties ---
        public static string NC_WEBDAV_BASE_URL { get; private set; }
        public static string SUITE_MANIFEST_URL { get; private set; }
        public static string NC_DEVICE_CONFIGS_URL { get; private set; }

        /// <summary>
        /// Loads the configuration from the .env file located next to the executable.
        /// </summary>
        public static void LoadEnvConfiguration()
        {
            var envPath = Path.Combine(Application.StartupPath, ".env");
            if (!File.Exists(envPath)) throw new FileNotFoundException(".env file not found!");

            string user = null, password = null, serverUrl = null;
            bool devChannel = false;

            foreach (var line in File.ReadAllLines(envPath))
            {
                var parts = line.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var value = parts[1].Trim().Trim('"');
                switch (key)
                {
                    case "NC_USER_QA": user = value; break;
                    case "NC_PASSWORD_QA": password = value; break;
                    case "NC_SERVER_URL_QA": serverUrl = value; break;
                    case "NC_CHANNEL": devChannel = value.Equals("dev", StringComparison.OrdinalIgnoreCase); break;
                }
            }

            ApplyConfiguration(user, password, serverUrl, devChannel);
        }

        /// <summary>
        /// Applies credentials and the update channel, and recalculates all derived URLs.
        /// </summary>
        /// <param name="user">Nextcloud user name.</param>
        /// <param name="password">Nextcloud app password.</param>
        /// <param name="serverUrl">Nextcloud server base URL.</param>
        /// <param name="devChannel">True to use the dev channel of the suite manifest.</param>
        public static void ApplyConfiguration(string user, string password, string serverUrl, bool devChannel)
        {
            NC_USER = user;
            NC_PASSWORD = password;
            NC_SERVER_URL = serverUrl;
            IsDevChannel = devChannel;

            if (string.IsNullOrEmpty(NC_USER) || string.IsNullOrEmpty(NC_PASSWORD) || string.IsNullOrEmpty(NC_SERVER_URL))
            {
                throw new Exception("Failed to load NC_USER, NC_PASSWORD, or NC_SERVER_URL from .env.");
            }

            // Set derived properties
            NC_WEBDAV_BASE_URL = $"{NC_SERVER_URL}/remote.php/dav/files/{NC_USER}{NC_FILES_PATH}";
            SUITE_MANIFEST_URL = $"{NC_WEBDAV_BASE_URL}/suite_manifest.json";
            NC_DEVICE_CONFIGS_URL = $"{NC_WEBDAV_BASE_URL}{NC_DEVICE_CONFIGS_PATH_SUFFIX}";
        }
    }
}