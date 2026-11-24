// AppPackageTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Xunit;

namespace SCT_Updater.Tests
{
    /// <summary>
    /// Covers the "app" package mode: module code, wheels unpacked next to it and the shared embeddable Python.
    /// </summary>
    [Collection("AppConfig")]
    public class AppPackageTests : IDisposable
    {
        private const string Id = "app_demo";
        private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"sct_tests_{Guid.NewGuid():N}.json");
        private static readonly string[] OwnFolders = { "apps", "tools", "config", "lib_nextcloud_cli" };

        public AppPackageTests() => Clean();

        public void Dispose()
        {
            Clean();
            if (File.Exists(_stateFile)) File.Delete(_stateFile);
        }

        private static void Clean()
        {
            foreach (string folder in OwnFolders)
            {
                string path = Path.Combine(Application.StartupPath, folder);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }

        private static string Path_(string relative) => Path.Combine(Application.StartupPath, relative);

        private static byte[] Zip(params (string path, string content)[] entries)
        {
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var (path, content) in entries)
                    {
                        using (var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false))) writer.Write(content);
                    }
                }
                return stream.ToArray();
            }
        }

        private static (string file, byte[] bytes) Wheel(string name, params (string path, string content)[] entries) =>
            ($"{name}-1.0-py3-none-any.whl", Zip(entries));

        /// <summary>Publishes Python, wheels, code and the manifest of a module version and returns the manifest.</summary>
        private static FileManifest Publish(FakeServer server, string id, string version,
            (string path, string content)[] code, (string file, byte[] bytes)[] wheels, byte[] pythonZip = null, string pythonHash = null)
        {
            byte[] python = pythonZip ?? Zip(("python.exe", "exe"), ("python313._pth", "python313.zip\n."));
            string pythonFile = "python-3.13.5-embed-amd64.zip";
            server.PutAt($"runtime/python/{pythonFile}", python);
            server.PutAt("runtime/python_3.13.json", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new PythonCoreInfo
            {
                Version = "3.13.5", File = pythonFile, Sha256 = pythonHash ?? Utility.GetBytesHash(python), Size = python.Length
            })));

            foreach (var wheel in wheels) server.PutAt($"runtime/wheels/{wheel.file}", wheel.bytes);
            foreach (var file in code) server.PutAt($"versions/{id}/{version}/{file.path}", Encoding.UTF8.GetBytes(file.content));

            var manifest = new FileManifest
            {
                ProductId = id, Version = version, BaseUrl = $"versions/{id}/{version}", PackageMode = AppConfig.PACKAGE_MODE_APP,
                Python = "3.13", WheelsUrl = "runtime/wheels",
                Files = code.Select(c => new ManifestFile { Path = c.path, Hash = Utility.GetBytesHash(Encoding.UTF8.GetBytes(c.content)) }).ToList(),
                Wheels = wheels.Select(w => new ManifestWheel { File = w.file, Sha256 = Utility.GetBytesHash(w.bytes), Size = w.bytes.Length }).ToList()
            };
            server.PutFileManifest(id, version, JsonConvert.SerializeObject(manifest));
            return manifest;
        }

        private (FakeServer server, UpdateService service, LocalStateService state) Arrange()
        {
            FakeServer.Configure(false);
            var server = new FakeServer();
            var state = new LocalStateService(_stateFile);
            return (server, new UpdateService(server.CreateClient(), state), state);
        }

        private static Task Install(UpdateService service, string id, string version) =>
            service.RunAppUpdateAsync(new Product { Id = id, LatestVersion = version }, new Progress<string>(), new Progress<int>());

        private static readonly (string, string)[] Code =
        {
            ($"apps/{Id}/__main__.py", "print('v1')"), ($"apps/{Id}/src/calc.py", "x = 1"), ("config/res.txt", "shared")
        };

        [Fact]
        public void Manifest_WrittenByTheReleaseTool_IsReadByTheUpdater()
        {
            const string json = @"{
              ""product_id"": ""nextcloud_cli"", ""version"": ""1.3.0"", ""base_url"": ""versions/nextcloud_cli/1.3.0"",
              ""package_mode"": ""app"", ""python"": ""3.13"", ""wheels_url"": ""runtime/wheels"",
              ""files"": [ { ""path"": ""apps/nextcloud_cli/__main__.py"", ""hash"": ""ab"" } ],
              ""wheels"": [ { ""file"": ""loguru-0.7.3-py3-none-any.whl"", ""sha256"": ""cd"", ""size"": 61595 } ] }";

            var manifest = JsonConvert.DeserializeObject<FileManifest>(json);

            Assert.True(manifest.IsApp);
            Assert.Equal("3.13", manifest.Python);
            Assert.Equal("runtime/wheels", manifest.WheelsUrl);
            Assert.Equal("loguru-0.7.3-py3-none-any.whl", Assert.Single(manifest.Wheels).File);
        }

        [Fact]
        public async Task FreshInstall_PlacesPythonCodeWheelsAndSharedFiles()
        {
            var (server, service, _) = Arrange();
            Publish(server, Id, "1.0.0", Code, new[] { Wheel("pkgone", ("pkgone/__init__.py", "one")), Wheel("pkgtwo", ("pkgtwo/__init__.py", "two")) });

            await Install(service, Id, "1.0.0");

            Assert.True(File.Exists(Path_("apps/python_runtime/python.exe")));
            Assert.True(File.Exists(Path_("apps/python_runtime/.sct_python.json")));
            Assert.Equal("print('v1')", File.ReadAllText(Path_($"apps/{Id}/__main__.py")));
            Assert.Equal("shared", File.ReadAllText(Path_("config/res.txt")));
            Assert.Equal("one", File.ReadAllText(Path_($"apps/{Id}/pkgone/__init__.py")));
            Assert.Equal("two", File.ReadAllText(Path_($"apps/{Id}/pkgtwo/__init__.py")));
            Assert.Equal(2, Directory.GetFiles(Path_("tools/wheelhouse"), "*.whl").Length);
        }

        [Fact]
        public async Task SecondRun_DownloadsNeitherPythonNorWheels()
        {
            var (server, service, state) = Arrange();
            Publish(server, Id, "1.0.0", Code, new[] { Wheel("pkgone", ("pkgone/__init__.py", "one")) });
            await Install(service, Id, "1.0.0");
            state.SaveLocalVersion(Id, "1.0.0");
            server.Requests.Clear();

            await Install(service, Id, "1.0.0");

            Assert.DoesNotContain(server.Requests, r => r.Contains("/runtime/wheels/") || r.Contains("/runtime/python/"));
        }

        [Fact]
        public async Task WheelChange_ReplacesOnlyWhatChangedAndKeepsTheRest()
        {
            var (server, service, state) = Arrange();
            var one = Wheel("pkgone", ("pkgone/__init__.py", "one"));
            Publish(server, Id, "1.0.0", Code, new[] { one, Wheel("pkgtwo", ("pkgtwo/__init__.py", "two")) });
            await Install(service, Id, "1.0.0");
            state.SaveLocalVersion(Id, "1.0.0");

            Publish(server, Id, "1.1.0", Code, new[] { one, Wheel("pkgthree", ("pkgthree/__init__.py", "three")) });
            server.Requests.Clear();
            await Install(service, Id, "1.1.0");

            Assert.True(File.Exists(Path_($"apps/{Id}/pkgone/__init__.py")));
            Assert.True(File.Exists(Path_($"apps/{Id}/pkgthree/__init__.py")));
            Assert.False(Directory.Exists(Path_($"apps/{Id}/pkgtwo")));
            Assert.DoesNotContain(server.Requests, r => r.EndsWith(one.file));
            Assert.Contains(server.Requests, r => r.EndsWith("pkgthree-1.0-py3-none-any.whl"));
        }

        [Fact]
        public async Task CodeUpdate_DeletesObsoleteFilesAndKeepsInstalledWheels()
        {
            var (server, service, state) = Arrange();
            var wheel = Wheel("pkgone", ("pkgone/__init__.py", "one"));
            Publish(server, Id, "1.0.0", Code, new[] { wheel });
            await Install(service, Id, "1.0.0");
            state.SaveLocalVersion(Id, "1.0.0");

            Publish(server, Id, "1.0.1", new[] { ($"apps/{Id}/__main__.py", "print('v2')"), ("config/res.txt", "shared") }, new[] { wheel });
            await Install(service, Id, "1.0.1");

            Assert.Equal("print('v2')", File.ReadAllText(Path_($"apps/{Id}/__main__.py")));
            Assert.False(File.Exists(Path_($"apps/{Id}/src/calc.py")));
            Assert.True(File.Exists(Path_($"apps/{Id}/pkgone/__init__.py")));
        }

        [Fact]
        public async Task TamperedWheel_IsRejectedAndNothingIsUnpacked()
        {
            var (server, service, _) = Arrange();
            var wheel = Wheel("pkgone", ("pkgone/__init__.py", "one"));
            var manifest = Publish(server, Id, "1.0.0", Code, new[] { wheel });
            server.PutAt($"runtime/wheels/{wheel.file}", Zip(("pkgone/__init__.py", "evil")));

            await Assert.ThrowsAsync<InvalidDataException>(() => Install(service, Id, "1.0.0"));

            Assert.False(File.Exists(Path_($"apps/{Id}/pkgone/__init__.py")));
            Assert.False(File.Exists(Path_($"apps/{Id}/.sct_wheels.json")));
        }

        [Fact]
        public async Task TamperedPythonArchive_IsRejected()
        {
            var (server, service, _) = Arrange();
            Publish(server, Id, "1.0.0", Code, new (string, byte[])[0], pythonHash: new string('a', 64));

            await Assert.ThrowsAsync<InvalidDataException>(() => Install(service, Id, "1.0.0"));

            Assert.False(Directory.Exists(Path_("apps/python_runtime")));
        }

        [Fact]
        public async Task PythonAlreadyInstalled_IsNotDownloadedForAnotherModule()
        {
            var (server, service, _) = Arrange();
            Publish(server, Id, "1.0.0", Code, new (string, byte[])[0]);
            await Install(service, Id, "1.0.0");
            string other = "other_mod";
            Publish(server, other, "1.0.0", new[] { ($"apps/{other}/__main__.py", "x") }, new (string, byte[])[0]);
            server.Requests.Clear();

            await Install(service, other, "1.0.0");

            Assert.DoesNotContain(server.Requests, r => r.Contains("/runtime/python/"));
        }

        [Fact]
        public void Delete_RemovesOnlyTheModuleAndItsSharedFiles()
        {
            var (_, service, _) = Arrange();
            foreach (string file in new[] { $"apps/{Id}/__main__.py", "apps/other/__main__.py", "apps/python_runtime/python.exe", "config/res.txt", "config/keep.txt" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_(file)));
                File.WriteAllText(Path_(file), "x");
            }
            var manifest = new FileManifest
            {
                ProductId = Id, PackageMode = AppConfig.PACKAGE_MODE_APP,
                Files = new List<ManifestFile> { new ManifestFile { Path = $"apps/{Id}/__main__.py" }, new ManifestFile { Path = "config/res.txt" } }
            };

            service.DeleteModuleFilesFromManifest(manifest);

            Assert.False(Directory.Exists(Path_($"apps/{Id}")));
            Assert.False(File.Exists(Path_("config/res.txt")));
            Assert.True(File.Exists(Path_("apps/other/__main__.py")));
            Assert.True(File.Exists(Path_("apps/python_runtime/python.exe")));
            Assert.True(File.Exists(Path_("config/keep.txt")));
        }

        [Fact]
        public async Task Delete_OfALockedFile_AsksTheHandlerAndContinuesAfterRelease()
        {
            var (_, service, _) = Arrange();
            string locked = Path_($"apps/{Id}/__main__.py");
            Directory.CreateDirectory(Path.GetDirectoryName(locked));
            var holder = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            int asked = 0;
            service.FileInUseHandler = (id, attempt) =>
            {
                asked++;
                holder.Dispose();
                return Task.FromResult(FileInUseChoice.Retry);
            };
            var manifest = new FileManifest
            {
                ProductId = Id, PackageMode = AppConfig.PACKAGE_MODE_APP,
                Files = new List<ManifestFile> { new ManifestFile { Path = $"apps/{Id}/__main__.py" } }
            };

            await service.DeleteModuleFilesFromManifestAsync(manifest);

            Assert.Equal(1, asked);
            Assert.False(Directory.Exists(Path_($"apps/{Id}")));
        }

        [Fact]
        public async Task MigrationFromZip_RemovesTheOldLayout()
        {
            var (server, service, state) = Arrange();
            const string module = "nextcloud_cli";
            state.SaveLocalVersion(module, "1.2.0");
            Directory.CreateDirectory(Path_("lib_nextcloud_cli"));
            File.WriteAllText(Path_("lib_nextcloud_cli/old.dll"), "old");
            server.PutFileManifest(module, "1.2.0", $@"{{ ""product_id"": ""{module}"", ""version"": ""1.2.0"", ""base_url"": ""versions/{module}/1.2.0"",
                ""package_mode"": ""zip"", ""files"": [ {{ ""path"": ""nextcloud_cli_1.2.0.zip"", ""hash"": ""00"" }} ] }}");
            Publish(server, module, "1.3.0", new[] { ($"apps/{module}/__main__.py", "print('cli')") }, new (string, byte[])[0]);

            await Install(service, module, "1.3.0");

            Assert.False(Directory.Exists(Path_("lib_nextcloud_cli")));
            Assert.Equal("print('cli')", File.ReadAllText(Path_($"apps/{module}/__main__.py")));
        }

        [Fact]
        public async Task Reinstall_RebuildsTheModuleFromTheWheelCache()
        {
            var (server, service, state) = Arrange();
            Publish(server, Id, "1.0.0", Code, new[] { Wheel("pkgone", ("pkgone/__init__.py", "one")) });
            await Install(service, Id, "1.0.0");
            state.SaveLocalVersion(Id, "1.0.0");
            File.WriteAllText(Path_($"apps/{Id}/stray.txt"), "junk");
            server.Requests.Clear();

            await service.ReinstallModuleAsync(new Product { Id = Id, Name = Id, LatestVersion = "1.0.0" }, new Progress<string>(), new Progress<int>());

            Assert.False(File.Exists(Path_($"apps/{Id}/stray.txt")));
            Assert.Equal("one", File.ReadAllText(Path_($"apps/{Id}/pkgone/__init__.py")));
            Assert.DoesNotContain(server.Requests, r => r.Contains("/runtime/wheels/"));
        }
    }
}
