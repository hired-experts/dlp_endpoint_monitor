using System.Text.Json;
using DlpEndpointMonitor.Core;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// AtomicFileWriter.Save is the fix for a real production bug: UsbDeviceList/ClipboardRuleList/
/// DisabledDevices each independently did an atomic temp-file-then-rename save, but the actual
/// disk I/O ran OUTSIDE any lock - only the in-memory snapshot was protected. Two threads saving
/// around the same time (e.g. two devices blocked concurrently, each on its own Task.Run) could
/// collide on the shared ".tmp" path, observed in production as
/// "disabled_devices_save: ...file...being used by another process" (12x in one session's event
/// history). These tests prove the shared helper's lock actually serializes concurrent saves.
/// </summary>
public class AtomicFileWriterTests
{
    static void WithTempDir(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { test(dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // T-AFW-01: the base case - a single save creates the directory (if missing) and writes the
    // exact given content to the target path (not left sitting in the .tmp file).
    [Fact]
    public void Save_CreatesDirectoryAndWritesExactContentToTargetPath()
    {
        WithTempDir(dir =>
        {
            string subDir = Path.Combine(dir, "nested");
            string path = Path.Combine(subDir, "state.json");
            var lockObj = new object();

            AtomicFileWriter.Save(lockObj, subDir, path, "{\"value\":1}");

            Assert.True(Directory.Exists(subDir));
            Assert.True(File.Exists(path));
            Assert.Equal("{\"value\":1}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp")); // renamed away, not left behind
        });
    }

    // T-AFW-02: this is the actual regression test - many threads racing to save to the SAME
    // path with the SAME lock object must never throw (the original bug: concurrent
    // File.WriteAllText/File.Move on the shared ".tmp" path threw "being used by another
    // process"), and the file left behind must be valid, complete JSON from exactly one of the
    // writers - never truncated/interleaved content from two writers stepping on each other.
    [Fact]
    public void Save_ManyConcurrentCallsWithSharedLock_NeverThrowsAndLeavesValidCompleteContent()
    {
        WithTempDir(dir =>
        {
            string path = Path.Combine(dir, "state.json");
            var lockObj = new object();

            var tasks = Enumerable.Range(0, 25).Select(i => Task.Run(() =>
                AtomicFileWriter.Save(lockObj, dir, path, $"{{\"value\":{i}}}")
            )).ToArray();

            var ex = Record.Exception(() => Task.WaitAll(tasks));
            Assert.Null(ex);

            Assert.True(File.Exists(path));
            // Valid, parseable JSON - proves no interleaved/truncated write ever landed, whichever
            // of the 25 concurrent writers happened to finish last.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.TryGetProperty("value", out _));
        });
    }

    // T-AFW-03: two DIFFERENT lock objects (simulating two different classes/files, e.g.
    // DeviceWhitelist saving while ClipboardBlacklist saves) must not block each other - the lock
    // is per-caller, not a single global lock serializing every save in the process.
    [Fact]
    public void Save_ConcurrentCallsWithDifferentLocksAndDifferentPaths_BothSucceed()
    {
        WithTempDir(dir =>
        {
            string pathA = Path.Combine(dir, "a.json");
            string pathB = Path.Combine(dir, "b.json");

            var tasks = new[]
            {
                Task.Run(() => AtomicFileWriter.Save(new object(), dir, pathA, "{\"who\":\"a\"}")),
                Task.Run(() => AtomicFileWriter.Save(new object(), dir, pathB, "{\"who\":\"b\"}")),
            };

            var ex = Record.Exception(() => Task.WaitAll(tasks));
            Assert.Null(ex);
            Assert.Equal("{\"who\":\"a\"}", File.ReadAllText(pathA));
            Assert.Equal("{\"who\":\"b\"}", File.ReadAllText(pathB));
        });
    }
}
