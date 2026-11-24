// ManifestClasses.cs
using Newtonsoft.Json;
using System.Collections.Generic;

namespace SCT_Updater
{
    public class SuiteManifest
    {
        [JsonProperty("products")]
        public List<Product> Products { get; set; }

        [JsonProperty("dev")]
        public List<Product> Dev { get; set; }

        /// <summary>
        /// Returns the product list for the requested channel. The dev channel uses the
        /// <c>dev</c> list when it is present and non-empty, otherwise the stable <c>products</c> list.
        /// </summary>
        /// <param name="devChannel">True to resolve the dev channel; false for stable.</param>
        /// <returns>The product list of the resolved channel; never null.</returns>
        public List<Product> ResolveProducts(bool devChannel)
        {
            if (devChannel && Dev != null && Dev.Count > 0) return Dev;
            return Products ?? new List<Product>();
        }
    }

    public class Product
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("latest_version")]
        public string LatestVersion { get; set; }

        // Local-only fields
        public string InstalledVersion { get; set; } = "Not Installed";
        public bool IsUpdateAvailable { get; set; } = false;

        /// <summary>True when the installed version matches the server one but the package manifest hash differs.</summary>
        public bool IsRebuilt { get; set; } = false;
    }

    public class LocalVersions
    {
        [JsonProperty("installed_products")]
        public List<InstalledProduct> InstalledProducts { get; set; } = new List<InstalledProduct>();
    }

    public class InstalledProduct
    {
        [JsonProperty("id")]
        public string Id { get; set; }
        [JsonProperty("version")]
        public string Version { get; set; }
        [JsonProperty("manifest_hash", NullValueHandling = NullValueHandling.Ignore)]
        public string ManifestHash { get; set; } // SHA256 of the file manifest that was installed
    }

    public class FileManifest
    {
        [JsonProperty("product_id")]
        public string ProductId { get; set; }
        [JsonProperty("version")]
        public string Version { get; set; }
        [JsonProperty("base_url")]
        public string BaseUrl { get; set; }
        [JsonProperty("package_mode")]
        public string PackageMode { get; set; } // "zip", "files" or "app"
        [JsonProperty("files")]
        public List<ManifestFile> Files { get; set; }

        [JsonProperty("python", NullValueHandling = NullValueHandling.Ignore)]
        public string Python { get; set; } // Python minor version of an "app" package, e.g. "3.13"
        [JsonProperty("wheels_url", NullValueHandling = NullValueHandling.Ignore)]
        public string WheelsUrl { get; set; } // Wheel store of an "app" package, relative to the Updater root
        [JsonProperty("wheels", NullValueHandling = NullValueHandling.Ignore)]
        public List<ManifestWheel> Wheels { get; set; }

        /// <summary>True for a Python module installed as code plus wheels under apps/&lt;module&gt;.</summary>
        [JsonIgnore]
        public bool IsApp => PackageMode == AppConfig.PACKAGE_MODE_APP;
    }

    public class ManifestWheel
    {
        [JsonProperty("file")]
        public string File { get; set; }
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
        [JsonProperty("size")]
        public long Size { get; set; }
    }

    /// <summary>The embeddable Python archive published for a minor version.</summary>
    public class PythonCoreInfo
    {
        [JsonProperty("version")]
        public string Version { get; set; }
        [JsonProperty("file")]
        public string File { get; set; }
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
        [JsonProperty("size")]
        public long Size { get; set; }
    }

    public class ManifestFile
    {
        [JsonProperty("path")]
        public string Path { get; set; }
        [JsonProperty("hash")]
        public string Hash { get; set; } // SHA256
    }

    // A helper class to return results from CheckForUpdatesAsync
    public class UpdateCheckResult
    {
        public List<Product> ProductsToShow { get; set; }
        public Product SelfUpdateProduct { get; set; }
    }
}