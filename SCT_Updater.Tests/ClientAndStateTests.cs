// ClientAndStateTests.cs
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SCT_Updater.Tests
{
    [Collection("AppConfig")]
    public class ClientAndStateTests : IDisposable
    {
        private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"sct_tests_{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(_stateFile)) File.Delete(_stateFile);
        }

        [Fact]
        public async Task FetchFileManifest_HashesRawBytes()
        {
            FakeServer.Configure(false);
            var server = new FakeServer();
            const string json = "{ \"product_id\": \"p\", \"version\": \"1.0.0\", \"package_mode\": \"zip\", \"files\": [] }";
            string expected = server.PutFileManifest("p", "1.0.0", json);

            var (manifest, hash) = await server.CreateClient().FetchFileManifestAsync("p", "1.0.0");

            Assert.Equal(expected, hash);
            Assert.Equal("zip", manifest.PackageMode);
        }

        [Fact]
        public async Task FetchFileManifest_SameContentGivesSameHash_DifferentContentDiffers()
        {
            FakeServer.Configure(false);
            var server = new FakeServer();
            var client = server.CreateClient();

            server.PutFileManifest("p", "1.0.0", "{ \"files\": [ { \"path\": \"a\", \"hash\": \"1\" } ] }");
            var first = await client.FetchFileManifestAsync("p", "1.0.0");
            var again = await client.FetchFileManifestAsync("p", "1.0.0");

            server.PutFileManifest("p", "1.0.0", "{ \"files\": [ { \"path\": \"a\", \"hash\": \"2\" } ] }");
            var rebuilt = await client.FetchFileManifestAsync("p", "1.0.0");

            Assert.Equal(first.Hash, again.Hash);
            Assert.NotEqual(first.Hash, rebuilt.Hash);
        }

        [Fact]
        public async Task FetchFileManifest_ParsesUtf8Bom()
        {
            FakeServer.Configure(false);
            var server = new FakeServer();
            server.PutFileManifest("p", "1.0.0", "﻿{ \"package_mode\": \"files\", \"files\": [] }");

            var (manifest, _) = await server.CreateClient().FetchFileManifestAsync("p", "1.0.0");

            Assert.Equal("files", manifest.PackageMode);
        }

        [Fact]
        public async Task FetchFileManifest_Missing_Throws()
        {
            FakeServer.Configure(false);
            var client = new FakeServer().CreateClient();

            await Assert.ThrowsAnyAsync<Exception>(() => client.FetchFileManifestAsync("p", "9.9.9"));
        }

        [Fact]
        public void GetBytesHash_MatchesKnownSha256()
        {
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                Utility.GetBytesHash(Encoding.ASCII.GetBytes("abc")));
        }

        [Fact]
        public void LocalState_RoundTripsManifestHash()
        {
            var state = new LocalStateService(_stateFile);

            state.SaveLocalVersion("p", "1.0.0", "h1");
            state.SaveLocalVersion("p", "1.0.1", "h2");

            var installed = new LocalStateService(_stateFile).LoadLocalVersions().InstalledProducts.Single();
            Assert.Equal("1.0.1", installed.Version);
            Assert.Equal("h2", installed.ManifestHash);
        }

        [Fact]
        public void BuildRestartArguments_PassesFoldersWithoutTrailingSeparators()
        {
            string args = Utility.BuildRestartArguments(@"C:\SCT\", @"C:\Temp\SCT_Updater_New\", "Updater.exe");

            Assert.Contains(@"robocopy ""C:\Temp\SCT_Updater_New"" ""C:\SCT"" /E /IS /MOVE", args);
            Assert.Contains(@"rmdir /s /q ""C:\Temp\SCT_Updater_New""", args);
            Assert.EndsWith(@"start """" ""C:\SCT\Updater.exe""", args);
        }

        [Fact]
        public void LocalState_LegacyFileWithoutHash_LoadsWithNullHash()
        {
            File.WriteAllText(_stateFile, "{ \"installed_products\": [ { \"id\": \"p\", \"version\": \"1.0.0\" } ] }");

            var installed = new LocalStateService(_stateFile).LoadLocalVersions().InstalledProducts.Single();

            Assert.Null(installed.ManifestHash);
        }

        [Fact]
        public void LocalState_NotInstalled_RemovesProduct()
        {
            var state = new LocalStateService(_stateFile);
            state.SaveLocalVersion("p", "1.0.0", "h1");

            state.SaveLocalVersion("p", "Not Installed");

            Assert.Empty(state.LoadLocalVersions().InstalledProducts);
        }
    }
}
