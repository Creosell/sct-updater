// UpdateCheckTests.cs
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SCT_Updater.Tests
{
    /// <summary>
    /// Runs <see cref="UpdateService.CheckForUpdatesAsync"/> against an in-memory server.
    /// AppConfig is static, so the tests must not run in parallel.
    /// </summary>
    [Collection("AppConfig")]
    public class UpdateCheckTests : IDisposable
    {
        private const string CurrentUpdaterVersion = "1.1.0";
        private const string Tool = "screen_checker";

        private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"sct_tests_{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(_stateFile)) File.Delete(_stateFile);
        }

        private static string Suite(string stableVersion, string devVersion = null, string updaterDev = null)
        {
            string dev = devVersion == null && updaterDev == null
                ? string.Empty
                : $@", ""dev"": [
                    {{ ""id"": ""suite_updater"", ""name"": ""Updater"", ""latest_version"": ""{updaterDev ?? CurrentUpdaterVersion}"" }},
                    {{ ""id"": ""{Tool}"", ""name"": ""Screen Checker"", ""latest_version"": ""{devVersion ?? stableVersion}"" }} ]";

            return $@"{{ ""products"": [
                {{ ""id"": ""suite_updater"", ""name"": ""Updater"", ""latest_version"": ""{CurrentUpdaterVersion}"" }},
                {{ ""id"": ""{Tool}"", ""name"": ""Screen Checker"", ""latest_version"": ""{stableVersion}"" }} ]{dev} }}";
        }

        private static string Files(string version, string zipHash) =>
            $@"{{ ""product_id"": ""{Tool}"", ""version"": ""{version}"", ""package_mode"": ""zip"", ""files"": [ {{ ""path"": ""p.zip"", ""hash"": ""{zipHash}"" }} ] }}";

        private (FakeServer server, UpdateService service, LocalStateService state) Arrange(bool dev, string suite)
        {
            FakeServer.Configure(dev);
            var server = new FakeServer();
            server.PutSuiteManifest(suite);
            var state = new LocalStateService(_stateFile);
            return (server, new UpdateService(server.CreateClient(), state), state);
        }

        private static Product ToolOf(UpdateCheckResult result) => result.ProductsToShow.Single(p => p.Id == Tool);

        [Fact]
        public async Task SameVersionSameHash_NothingToDo()
        {
            var (server, service, state) = Arrange(false, Suite("1.4.1"));
            string hash = server.PutFileManifest(Tool, "1.4.1", Files("1.4.1", "aa"));
            state.SaveLocalVersion(Tool, "1.4.1", hash);

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.False(product.IsUpdateAvailable);
            Assert.False(product.IsRebuilt);
        }

        [Fact]
        public async Task SameVersionDifferentHash_MarkedRebuilt()
        {
            var (server, service, state) = Arrange(false, Suite("1.4.1"));
            server.PutFileManifest(Tool, "1.4.1", Files("1.4.1", "bb"));
            state.SaveLocalVersion(Tool, "1.4.1", Utility.GetBytesHash(new byte[] { 1 }));

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.True(product.IsUpdateAvailable);
            Assert.True(product.IsRebuilt);
        }

        [Fact]
        public async Task SameVersionNoLocalHash_NotFlaggedAndManifestNotFetched()
        {
            var (server, service, state) = Arrange(false, Suite("1.4.1"));
            server.PutFileManifest(Tool, "1.4.1", Files("1.4.1", "aa"));
            state.SaveLocalVersion(Tool, "1.4.1");

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.False(product.IsUpdateAvailable);
            Assert.DoesNotContain(server.Requests, r => r.Contains("versions/"));
        }

        [Fact]
        public async Task SameVersionManifestUnavailable_NotFlagged()
        {
            var (_, service, state) = Arrange(false, Suite("1.4.1"));
            state.SaveLocalVersion(Tool, "1.4.1", "deadbeef");

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.False(product.IsUpdateAvailable);
            Assert.False(product.IsRebuilt);
        }

        [Fact]
        public async Task DifferentVersion_RegularUpdateNotRebuilt()
        {
            var (_, service, state) = Arrange(false, Suite("1.4.2"));
            state.SaveLocalVersion(Tool, "1.4.1", "deadbeef");

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.True(product.IsUpdateAvailable);
            Assert.False(product.IsRebuilt);
        }

        [Fact]
        public async Task NotInstalled_OffersInstall()
        {
            var (_, service, _) = Arrange(false, Suite("1.4.1"));

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.Equal("Not Installed", product.InstalledVersion);
            Assert.True(product.IsUpdateAvailable);
        }

        [Fact]
        public async Task StableChannel_IgnoresDevVersions()
        {
            var (_, service, state) = Arrange(false, Suite("1.4.1", devVersion: "1.5.2"));
            state.SaveLocalVersion(Tool, "1.4.1");

            var result = await service.CheckForUpdatesAsync(CurrentUpdaterVersion);

            Assert.Null(result.SelfUpdateProduct);
            Assert.Equal("1.4.1", ToolOf(result).LatestVersion);
            Assert.False(ToolOf(result).IsUpdateAvailable);
        }

        [Fact]
        public async Task DevChannel_PicksUpDevVersion()
        {
            var (_, service, state) = Arrange(true, Suite("1.4.1", devVersion: "1.5.2"));
            state.SaveLocalVersion(Tool, "1.4.1");

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.Equal("1.5.2", product.LatestVersion);
            Assert.True(product.IsUpdateAvailable);
        }

        [Fact]
        public async Task NewUpdaterVersionOnDev_TriggersSelfUpdateOnDevOnly()
        {
            string suite = Suite("1.4.1", updaterDev: "1.1.1");

            var dev = Arrange(true, suite);
            var devResult = await dev.service.CheckForUpdatesAsync(CurrentUpdaterVersion);

            var stable = Arrange(false, suite);
            var stableResult = await stable.service.CheckForUpdatesAsync(CurrentUpdaterVersion);

            Assert.Equal("1.1.1", devResult.SelfUpdateProduct?.LatestVersion);
            Assert.Null(stableResult.SelfUpdateProduct);
        }

        [Fact]
        public async Task SwitchingDevToStable_OffersDowngrade()
        {
            var (_, service, state) = Arrange(false, Suite("1.4.1", devVersion: "1.5.2"));
            state.SaveLocalVersion(Tool, "1.5.2");

            var product = ToolOf(await service.CheckForUpdatesAsync(CurrentUpdaterVersion));

            Assert.Equal("1.4.1", product.LatestVersion);
            Assert.True(product.IsUpdateAvailable);
            Assert.False(product.IsRebuilt);
        }
    }
}
