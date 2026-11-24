// SelfUpdateTests.cs
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Updater.Tests
{
    /// <summary>
    /// Covers the self-update loop guard, the restart report and the log rotation.
    /// </summary>
    public class SelfUpdateTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"sct_selfupd_{Guid.NewGuid():N}");

        public SelfUpdateTests() => Directory.CreateDirectory(_folder);

        public void Dispose() => Directory.Delete(_folder, true);

        [Fact]
        public void HasFailedBefore_RecentAttemptFromTheSameVersion_IsAFailure()
        {
            SelfUpdateGuard.Record("1.2.0", "1.1.0", _folder);

            Assert.True(SelfUpdateGuard.HasFailedBefore("1.2.0", "1.1.0", _folder));
        }

        [Fact]
        public void HasFailedBefore_WithoutMarkerOrForOtherVersions_IsNotAFailureAndClearsTheMarker()
        {
            Assert.False(SelfUpdateGuard.HasFailedBefore("1.2.0", "1.1.0", _folder));

            SelfUpdateGuard.Record("1.2.0", "1.1.0", _folder);
            Assert.False(SelfUpdateGuard.HasFailedBefore("1.2.0", "1.3.0", _folder));
            Assert.False(File.Exists(Path.Combine(_folder, SelfUpdateGuard.MarkerFile)));
        }

        [Fact]
        public void HasFailedBefore_ExpiredAttempt_IsIgnored()
        {
            SelfUpdateGuard.Record("1.2.0", "1.1.0", _folder);

            Assert.False(SelfUpdateGuard.HasFailedBefore("1.2.0", "1.1.0", _folder, DateTime.Now.AddHours(2)));
        }

        [Fact]
        public void HasFailedBefore_BrokenMarker_IsIgnored()
        {
            File.WriteAllText(Path.Combine(_folder, SelfUpdateGuard.MarkerFile), "{ not json");

            Assert.False(SelfUpdateGuard.HasFailedBefore("1.2.0", "1.1.0", _folder));
        }

        [Fact]
        public void ConfirmCompleted_RunningTheTargetVersion_IsTrueOnceAndClearsTheMarker()
        {
            SelfUpdateGuard.Record("1.2.0", "1.1.0", _folder);

            Assert.True(SelfUpdateGuard.ConfirmCompleted("1.1.0", _folder));
            Assert.False(SelfUpdateGuard.ConfirmCompleted("1.1.0", _folder));
        }

        [Fact]
        public void ImportRestartReport_ReadsTheExitCodeAndTheUnicodeLogAndDeletesBoth()
        {
            File.WriteAllText(Path.Combine(_folder, SelfUpdateGuard.ExitCodeFile), "16 \r\n");
            File.WriteAllText(Path.Combine(_folder, SelfUpdateGuard.RobocopyLogFile), "ERROR 5 (0x00000005) 拒绝访问\r\n", Encoding.Unicode);

            string summary = SelfUpdateGuard.ImportRestartReport(_folder);

            Assert.Contains("exit code 16", summary);
            Assert.Contains("not replaced", summary);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void ImportRestartReport_WithoutFiles_ReturnsNull()
        {
            Assert.Null(SelfUpdateGuard.ImportRestartReport(_folder));
        }

        [Fact]
        public void Rotate_KeepsANumberedHistoryAndDropsTheOldest()
        {
            string log = Path.Combine(_folder, "sct_updater.log");
            for (int run = 1; run <= 4; run++)
            {
                File.WriteAllText(log, $"run {run}");
                LogRotation.Rotate(log, 2);
            }

            Assert.False(File.Exists(log));
            Assert.Equal("run 4", File.ReadAllText(Path.Combine(_folder, "sct_updater.1.log")));
            Assert.Equal("run 3", File.ReadAllText(Path.Combine(_folder, "sct_updater.2.log")));
            Assert.False(File.Exists(Path.Combine(_folder, "sct_updater.3.log")));
        }
    }
}
