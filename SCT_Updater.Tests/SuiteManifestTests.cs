// SuiteManifestTests.cs
using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace SCT_Updater.Tests
{
    public class SuiteManifestTests
    {
        private const string FullManifest = @"{
          ""products"": [ { ""id"": ""a"", ""name"": ""A"", ""latest_version"": ""1.0.0"", } ],
          ""dev"":      [ { ""id"": ""a"", ""name"": ""A"", ""latest_version"": ""1.0.1"" } ]
        }";

        [Fact]
        public void Deserialize_ToleratesTrailingCommas()
        {
            var manifest = JsonConvert.DeserializeObject<SuiteManifest>(FullManifest);

            Assert.Equal("1.0.0", manifest.Products.Single().LatestVersion);
            Assert.Equal("1.0.1", manifest.Dev.Single().LatestVersion);
        }

        [Fact]
        public void Deserialize_LegacyManifestWithDescriptionAndWithoutDev_Works()
        {
            const string legacy = @"{ ""products"": [ { ""id"": ""a"", ""name"": ""A"", ""latest_version"": ""1.0.0"", ""description"": ""x"" } ] }";

            var manifest = JsonConvert.DeserializeObject<SuiteManifest>(legacy);

            Assert.Null(manifest.Dev);
            Assert.Single(manifest.Products);
        }

        [Fact]
        public void ResolveProducts_Stable_ReturnsProducts()
        {
            var manifest = JsonConvert.DeserializeObject<SuiteManifest>(FullManifest);

            Assert.Equal("1.0.0", manifest.ResolveProducts(devChannel: false).Single().LatestVersion);
        }

        [Fact]
        public void ResolveProducts_Dev_ReturnsDevList()
        {
            var manifest = JsonConvert.DeserializeObject<SuiteManifest>(FullManifest);

            Assert.Equal("1.0.1", manifest.ResolveProducts(devChannel: true).Single().LatestVersion);
        }

        [Theory]
        [InlineData(@"{ ""products"": [ { ""id"": ""a"", ""latest_version"": ""1.0.0"" } ] }")]
        [InlineData(@"{ ""products"": [ { ""id"": ""a"", ""latest_version"": ""1.0.0"" } ], ""dev"": [] }")]
        public void ResolveProducts_DevMissingOrEmpty_FallsBackToStable(string json)
        {
            var manifest = JsonConvert.DeserializeObject<SuiteManifest>(json);

            Assert.Equal("1.0.0", manifest.ResolveProducts(devChannel: true).Single().LatestVersion);
        }

        [Fact]
        public void ResolveProducts_NoProducts_ReturnsEmptyList()
        {
            var manifest = new SuiteManifest();

            Assert.Empty(manifest.ResolveProducts(devChannel: false));
        }
    }
}
