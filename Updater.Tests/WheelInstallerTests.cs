// WheelInstallerTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Updater.Tests
{
    public class WheelInstallerTests : IDisposable
    {
        private readonly string _work = Path.Combine(Path.GetTempPath(), $"sct_wheel_{Guid.NewGuid():N}");

        public WheelInstallerTests() => Directory.CreateDirectory(_work);

        public void Dispose() => Directory.Delete(_work, true);

        private string BuildWheel(string name, params (string path, string content)[] entries)
        {
            string wheel = Path.Combine(_work, name);
            using (var zip = ZipFile.Open(wheel, ZipArchiveMode.Create))
            {
                foreach (var (path, content) in entries)
                {
                    var entry = zip.CreateEntry(path);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content);
                }
            }
            return wheel;
        }

        private string Site => Path.Combine(_work, "site-packages");

        [Fact]
        public void Extract_PackageAndDistInfo_AreWrittenAtTheRoot()
        {
            string wheel = BuildWheel("pkg-1.0-py3-none-any.whl",
                ("pkg/__init__.py", "x = 1"), ("pkg/sub/data.txt", "data"),
                ("pkg-1.0.dist-info/METADATA", "Name: pkg"), ("pkg-1.0.dist-info/RECORD", ""));

            int written = WheelInstaller.Extract(wheel, Site);

            Assert.Equal(4, written);
            Assert.Equal("x = 1", File.ReadAllText(Path.Combine(Site, "pkg", "__init__.py")));
            Assert.Equal("data", File.ReadAllText(Path.Combine(Site, "pkg", "sub", "data.txt")));
            Assert.True(File.Exists(Path.Combine(Site, "pkg-1.0.dist-info", "METADATA")));
        }

        [Fact]
        public void Extract_DataFolder_OnlyPurelibAndPlatlibAreInstalled()
        {
            string wheel = BuildWheel("pkg-1.0-py3-none-any.whl",
                ("pkg-1.0.data/purelib/extra.py", "pure"), ("pkg-1.0.data/platlib/native.txt", "plat"),
                ("pkg-1.0.data/scripts/tool.exe", "skip"), ("pkg-1.0.data/headers/h.h", "skip"), ("pkg-1.0.data/data/share.txt", "skip"));

            WheelInstaller.Extract(wheel, Site);

            Assert.Equal("pure", File.ReadAllText(Path.Combine(Site, "extra.py")));
            Assert.Equal("plat", File.ReadAllText(Path.Combine(Site, "native.txt")));
            Assert.Equal(2, Directory.GetFiles(Site, "*", SearchOption.AllDirectories).Length);
        }

        [Fact]
        public void Extract_PthFile_IsCopiedButNotExecuted()
        {
            string wheel = BuildWheel("pkg-1.0-py3-none-any.whl", ("pkg.pth", "import os"));

            WheelInstaller.Extract(wheel, Site);

            Assert.Equal("import os", File.ReadAllText(Path.Combine(Site, "pkg.pth")));
        }

        [Theory]
        [InlineData("../outside.txt")]
        [InlineData("pkg/../../outside.txt")]
        public void Extract_PathEscapingTheTarget_Throws(string entryPath)
        {
            string wheel = BuildWheel("evil-1.0-py3-none-any.whl", (entryPath, "x"));

            Assert.Throws<InvalidDataException>(() => WheelInstaller.Extract(wheel, Site));
            Assert.False(File.Exists(Path.Combine(_work, "outside.txt")));
        }

        [Fact]
        public void Extract_ExistingFile_IsOverwritten()
        {
            string wheel = BuildWheel("pkg-2.0-py3-none-any.whl", ("pkg/__init__.py", "new"));
            Directory.CreateDirectory(Path.Combine(Site, "pkg"));
            File.WriteAllText(Path.Combine(Site, "pkg", "__init__.py"), "old");

            WheelInstaller.Extract(wheel, Site);

            Assert.Equal("new", File.ReadAllText(Path.Combine(Site, "pkg", "__init__.py")));
        }

        /// <summary>
        /// Optional check against real wheels: set SCT_TEST_WHEELHOUSE to a folder of .whl files and
        /// SCT_TEST_UV_SITE to the site-packages that uv installed from the same wheels.
        /// Every file uv installed from the wheels must be byte-identical (uv bookkeeping files are ignored).
        /// </summary>
        [Fact]
        public void Extract_RealWheels_MatchTheUvInstallation()
        {
            string wheelhouse = Environment.GetEnvironmentVariable("SCT_TEST_WHEELHOUSE");
            string uvSite = Environment.GetEnvironmentVariable("SCT_TEST_UV_SITE");
            if (string.IsNullOrEmpty(wheelhouse) || string.IsNullOrEmpty(uvSite)) return;

            foreach (string wheel in Directory.GetFiles(wheelhouse, "*.whl")) WheelInstaller.Extract(wheel, Site);

            var mine = Hashes(Site);
            var uv = Hashes(uvSite);
            var ignored = new[] { "/INSTALLER", "/REQUESTED", "/uv_cache.json", "/RECORD", "/direct_url.json" };
            bool Bookkeeping(string p) => p == ".lock" || p.StartsWith("bin/") || ignored.Any(p.EndsWith);

            Assert.Empty(mine.Keys.Where(k => !Bookkeeping(k) && !uv.ContainsKey(k)));
            Assert.Empty(uv.Keys.Where(k => !Bookkeeping(k) && !k.EndsWith(".pth") && !mine.ContainsKey(k)));
            Assert.Empty(mine.Where(p => !Bookkeeping(p.Key) && uv.TryGetValue(p.Key, out string h) && h != p.Value).Select(p => p.Key));
        }

        private static Dictionary<string, string> Hashes(string root)
        {
            string full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            using (var sha = SHA256.Create())
            {
                return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetFullPath(p))
                    .Where(p => !p.Contains("__pycache__"))
                    .ToDictionary(p => p.Substring(full.Length).Replace('\\', '/'),
                        p => BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(p))));
            }
        }
    }
}
