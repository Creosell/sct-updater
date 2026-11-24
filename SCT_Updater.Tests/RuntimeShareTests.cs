// RuntimeShareTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Xunit;

namespace SCT_Updater.Tests
{
    /// <summary>
    /// Covers the "runtime-share" package mode: files are fetched from the shared store by hash.
    /// </summary>
    [Collection("AppConfig")]
    public class RuntimeShareTests : IDisposable
    {
        private readonly string _folder = $"rs_test_{Guid.NewGuid():N}";
        private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"sct_tests_{Guid.NewGuid():N}.json");

        private string LocalPath(string relative) => Path.Combine(Application.StartupPath, _folder, relative);

        public void Dispose()
        {
            string dir = Path.Combine(Application.StartupPath, _folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(_stateFile)) File.Delete(_stateFile);
        }

        private static string HashOf(string content) => Utility.GetBytesHash(Encoding.UTF8.GetBytes(content));

        private FileManifest Manifest(params (string path, string content)[] files) => new FileManifest
        {
            ProductId = "demo",
            Version = "1.0.0",
            BaseUrl = "versions/demo/1.0.0",
            PackageMode = AppConfig.PACKAGE_MODE_RUNTIME_SHARE,
            Files = files.Select(f => new ManifestFile { Path = $"{_folder}/{f.path}", Hash = HashOf(f.content) }).ToList()
        };

        private (FakeServer server, UpdateService service) Arrange()
        {
            FakeServer.Configure(false);
            var server = new FakeServer();
            return (server, new UpdateService(server.CreateClient(), new LocalStateService(_stateFile)));
        }

        private static Task Run(UpdateService service, FileManifest manifest, Dictionary<string, string> oldFiles = null) =>
            service.StartModuleUpdate_Files(manifest, oldFiles ?? new Dictionary<string, string>(),
                new Progress<string>(), new Progress<int>());

        [Fact]
        public void BuildManifestFileUrl_RuntimeShare_UsesStoreByHash()
        {
            FakeServer.Configure(false);
            var client = new FakeServer().CreateClient();
            var manifest = Manifest(("a.dll", "A"));

            string url = client.BuildManifestFileUrl(manifest, manifest.Files[0]);

            string hash = HashOf("A");
            Assert.Equal($"{AppConfig.NC_WEBDAV_BASE_URL}/versions/python_runtime/{hash.Substring(0, 2)}/{hash}", url);
        }

        [Fact]
        public void BuildManifestFileUrl_CustomStoreUrl_IsRespected()
        {
            FakeServer.Configure(false);
            var client = new FakeServer().CreateClient();
            var manifest = Manifest(("a.dll", "A"));
            manifest.StoreUrl = "versions/other_store";

            string url = client.BuildManifestFileUrl(manifest, manifest.Files[0]);

            Assert.Contains("/versions/other_store/", url);
        }

        [Theory]
        [InlineData("zip")]
        [InlineData("files")]
        public void BuildManifestFileUrl_LegacyModes_UseVersionFolder(string mode)
        {
            FakeServer.Configure(false);
            var client = new FakeServer().CreateClient();
            var manifest = Manifest(("a.dll", "A"));
            manifest.PackageMode = mode;

            string url = client.BuildManifestFileUrl(manifest, manifest.Files[0]);

            Assert.Equal($"{AppConfig.NC_WEBDAV_BASE_URL}/versions/demo/1.0.0/{manifest.Files[0].Path}", url);
        }

        [Fact]
        public void Deserialize_RuntimeShareManifest_ReadsModeAndStore()
        {
            const string json = @"{ ""product_id"": ""p"", ""version"": ""1"", ""base_url"": ""versions/p/1"",
                ""package_mode"": ""runtime-share"", ""store_url"": ""versions/python_runtime"", ""files"": [] }";

            var manifest = JsonConvert.DeserializeObject<FileManifest>(json);

            Assert.True(manifest.IsRuntimeShare);
            Assert.Equal("versions/python_runtime", manifest.StoreUrl);
        }

        [Fact]
        public async Task Install_DownloadsFilesFromStore()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("lib/a.dll", "AAA"), ("b.exe", "BBB"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("AAA"));
            server.PutStoreBlob(manifest.Files[1].Hash, Encoding.UTF8.GetBytes("BBB"));

            await Run(service, manifest);

            Assert.Equal("AAA", File.ReadAllText(LocalPath("lib/a.dll")));
            Assert.Equal("BBB", File.ReadAllText(LocalPath("b.exe")));
            Assert.DoesNotContain(server.Requests, r => r.Contains("versions/demo/"));
        }

        [Fact]
        public async Task Install_SameContentTwice_UsesOneBlob()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("x.dll", "SAME"), ("y.dll", "SAME"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("SAME"));

            await Run(service, manifest);

            Assert.Equal("SAME", File.ReadAllText(LocalPath("x.dll")));
            Assert.Equal("SAME", File.ReadAllText(LocalPath("y.dll")));
        }

        [Fact]
        public async Task Install_CorruptBlob_ThrowsAndWritesNothing()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "EXPECTED"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("TAMPERED"));

            await Assert.ThrowsAsync<InvalidDataException>(() => Run(service, manifest));

            Assert.False(File.Exists(LocalPath("a.dll")));
        }

        [Fact]
        public async Task Install_MissingBlob_Throws()
        {
            var (_, service) = Arrange();
            var manifest = Manifest(("a.dll", "A"));

            await Assert.ThrowsAnyAsync<Exception>(() => Run(service, manifest));
        }

        [Fact]
        public async Task Update_UnchangedFile_IsNotDownloadedAgain()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("keep.dll", "KEEP"), ("new.dll", "NEW"));
            server.PutStoreBlob(manifest.Files[1].Hash, Encoding.UTF8.GetBytes("NEW"));
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("keep.dll")));
            File.WriteAllText(LocalPath("keep.dll"), "KEEP");
            var oldFiles = new Dictionary<string, string> { { manifest.Files[0].Path, manifest.Files[0].Hash } };

            await Run(service, manifest, oldFiles);

            Assert.DoesNotContain(server.Requests, r => r.Contains(manifest.Files[0].Hash.Substring(0, 2) + "/" + manifest.Files[0].Hash));
            Assert.Equal("NEW", File.ReadAllText(LocalPath("new.dll")));
        }

        [Fact]
        public async Task Update_ChangedFile_IsDownloaded()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "V2"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("V2"));
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("a.dll")));
            File.WriteAllText(LocalPath("a.dll"), "V1");
            var oldFiles = new Dictionary<string, string> { { manifest.Files[0].Path, HashOf("V1") } };

            await Run(service, manifest, oldFiles);

            Assert.Equal("V2", File.ReadAllText(LocalPath("a.dll")));
        }

        [Fact]
        public async Task Install_LocalFileWithCorrectHash_IsNotDownloadedEvenWithoutOldManifest()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "SAME"));
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("a.dll")));
            File.WriteAllText(LocalPath("a.dll"), "SAME");

            await Run(service, manifest);

            Assert.DoesNotContain(server.Requests, r => r.Contains("python_runtime"));
        }

        [Fact]
        public async Task Install_FileOfAnotherProduct_IsCopiedWithoutDownload()
        {
            var (server, service) = Arrange();
            string otherPath = $"{_folder}/other/a.dll";
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("other/a.dll")));
            File.WriteAllText(LocalPath("other/a.dll"), "SHARED");
            new LocalStateService(_stateFile).SaveLocalVersion("other", "1.0.0", "h", new Dictionary<string, string> { { otherPath, HashOf("SHARED") } });
            var manifest = Manifest(("mine/a.dll", "SHARED"), ("mine/b.dll", "UNIQUE"));
            server.PutStoreBlob(manifest.Files[1].Hash, Encoding.UTF8.GetBytes("UNIQUE"));

            await Run(service, manifest);

            Assert.Equal("SHARED", File.ReadAllText(LocalPath("mine/a.dll")));
            Assert.Equal("UNIQUE", File.ReadAllText(LocalPath("mine/b.dll")));
            string sharedHash = manifest.Files[0].Hash;
            Assert.DoesNotContain(server.Requests, r => r.EndsWith(sharedHash));
        }

        [Fact]
        public async Task Install_StaleIndexEntry_FallsBackToDownload()
        {
            var (server, service) = Arrange();
            string otherPath = $"{_folder}/other/a.dll";
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("other/a.dll")));
            File.WriteAllText(LocalPath("other/a.dll"), "MODIFIED LOCALLY");
            new LocalStateService(_stateFile).SaveLocalVersion("other", "1.0.0", "h", new Dictionary<string, string> { { otherPath, HashOf("SHARED") } });
            var manifest = Manifest(("mine/a.dll", "SHARED"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("SHARED"));

            await Run(service, manifest);

            Assert.Equal("SHARED", File.ReadAllText(LocalPath("mine/a.dll")));
            Assert.Contains(server.Requests, r => r.EndsWith(manifest.Files[0].Hash));
        }

        [Fact]
        public void LocalFileIndex_ExcludesOwnFilesAndIsLowercase()
        {
            var state = new LocalStateService(_stateFile);
            state.SaveLocalVersion("a", "1", "h", new Dictionary<string, string> { { "lib_a/x.dll", "ABCDEF" } });
            state.SaveLocalVersion("b", "1", "h", new Dictionary<string, string> { { "lib_b/x.dll", "123456" } });
            state.SaveLocalVersion("c", "1", "h");

            var index = state.GetLocalFileIndex("a");

            Assert.Equal("lib_b/x.dll", Assert.Single(index).Value);
            Assert.Equal("123456", index.Keys.Single());
        }

        /// <summary>Locks the target file and returns the install operation, which the test starts once the handler is set.</summary>
        private (Func<Task> update, FileStream locker) StartLockedInstall(UpdateService service, FileManifest manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("a.dll")));
            var locker = new FileStream(LocalPath("a.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            return (() => Run(service, manifest), locker);
        }

        [Fact]
        public async Task LockedFile_HandlerRetry_ContinuesAfterRelease()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "NEW"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("NEW"));
            var (update, locker) = StartLockedInstall(service, manifest);
            int calls = 0;
            service.FileInUseHandler = (productId, attempt) =>
            {
                calls++;
                locker.Dispose();
                return Task.FromResult(FileInUseChoice.Retry);
            };

            await update();

            Assert.True(calls >= 1);
            Assert.Equal("NEW", File.ReadAllText(LocalPath("a.dll")));
        }

        [Fact]
        public async Task LockedFile_HandlerCancel_ThrowsOperationCanceled()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "NEW"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("NEW"));
            var (update, locker) = StartLockedInstall(service, manifest);
            service.FileInUseHandler = (productId, attempt) => Task.FromResult(FileInUseChoice.Cancel);

            using (locker)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(update);
            }
        }

        [Fact]
        public async Task LockedFile_NoHandler_ThrowsOriginalIoError()
        {
            var (server, service) = Arrange();
            var manifest = Manifest(("a.dll", "NEW"));
            server.PutStoreBlob(manifest.Files[0].Hash, Encoding.UTF8.GetBytes("NEW"));
            var (update, locker) = StartLockedInstall(service, manifest);

            using (locker)
            {
                await Assert.ThrowsAsync<IOException>(update);
            }
        }

        [Fact]
        public async Task Migration_FromZip_RemovesOldLayoutAndInstallsFromStore()
        {
            var (server, service) = Arrange();
            var state = new LocalStateService(_stateFile);
            state.SaveLocalVersion("demo", "1.0.0");
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath("stale/old.dll")));
            File.WriteAllText(LocalPath("stale/old.dll"), "OLD");

            string zipManifest = $@"{{ ""product_id"": ""demo"", ""version"": ""1.0.0"", ""base_url"": ""versions/demo/1.0.0"",
                ""package_mode"": ""zip"", ""files"": [ {{ ""path"": ""demo_1.0.0.zip"", ""hash"": ""00"" }} ] }}";
            server.PutFileManifest("demo", "1.0.0", zipManifest);

            var next = Manifest(("lib/new.dll", "NEW"));
            next.Version = "2.0.0";
            server.PutFileManifest("demo", "2.0.0", JsonConvert.SerializeObject(next));
            server.PutStoreBlob(next.Files[0].Hash, Encoding.UTF8.GetBytes("NEW"));

            await service.RunDeltaUpdateAsync(new Product { Id = "demo", LatestVersion = "2.0.0" }, new Progress<string>(), new Progress<int>());

            Assert.False(File.Exists(LocalPath("stale/old.dll")));
            Assert.Equal("NEW", File.ReadAllText(LocalPath("lib/new.dll")));
        }
    }
}
