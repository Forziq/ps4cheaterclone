using PS4Mem.Core;
using PS4Mem.Core.Logging;

namespace PS4Mem.Core.Tests;

public class DebugLogTests
{
    [Fact]
    public void Disabled_log_writes_nothing_and_does_not_throw()
    {
        var log = DebugLog.Disabled;
        Assert.False(log.IsEnabled);
        Assert.Null(log.FilePath);
        log.Info("proto", "hello");
        log.Wire("proto", sent: true, new byte[] { 1, 2, 3 });
        log.Error("proto", "boom", new InvalidOperationException());
    }

    [Fact]
    public void Lines_have_level_and_category()
    {
        var sw = new StringWriter();
        var log = new DebugLog(sw);

        log.Info("scan", "first scan started");
        log.Warn("maps", "skipped map with end <= start");

        var text = sw.ToString();
        Assert.Contains("[INFO ] [scan] first scan started", text);
        Assert.Contains("[WARN ] [maps] skipped map with end <= start", text);
    }

    [Fact]
    public void Wire_dump_shows_direction_length_hex_and_ascii()
    {
        var sw = new StringWriter();
        var log = new DebugLog(sw);

        // CMD_VERSION header: magic 0xFFAABBCC, cmd 0xBD000001, datalen 0 (little-endian).
        byte[] header = [0xCC, 0xBB, 0xAA, 0xFF, 0x01, 0x00, 0x00, 0xBD, 0x00, 0x00, 0x00, 0x00];
        log.Wire("proto", sent: true, header);
        log.Wire("proto", sent: false, "1.3"u8);

        var text = sw.ToString();
        Assert.Contains("[WIRE ] [proto] >> 12 bytes", text);
        Assert.Contains("0000  CC BB AA FF 01 00 00 BD  00 00 00 00", text);
        Assert.Contains("<< 3 bytes", text);
        Assert.Contains("1.3", text);
    }

    [Fact]
    public void Wire_dump_is_truncated_but_reports_full_length()
    {
        var sw = new StringWriter();
        var log = new DebugLog(sw, wireDumpBytes: 16);

        log.Wire("proto", sent: false, new byte[1000]);

        var text = sw.ToString();
        Assert.Contains("<< 1000 bytes (first 16 shown)", text);
        Assert.DoesNotContain("0010  ", text); // only one dump line
    }

    [Fact]
    public void HexDump_splits_into_16_byte_lines()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var lines = DebugLog.HexDump(bytes).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("    0000  00 01 02", lines[0]);
        Assert.StartsWith("    0010  10 11 12 13", lines[1]);
    }

    [Fact]
    public void Create_returns_disabled_when_not_requested()
    {
        // Assumes PS4MEM_DEBUG is not set in the test environment.
        if (Environment.GetEnvironmentVariable(DebugLog.EnvironmentVariable) is not null) return;

        using var log = DebugLog.Create(PS4MemSettings.Default);
        Assert.False(log.IsEnabled);
    }

    [Fact]
    public void Create_writes_a_timestamped_file_with_a_header_when_enabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps4mem-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = PS4MemSettings.Default with { DebugLogging = true, LogDirectory = dir };
            string path;
            using (var log = DebugLog.Create(settings))
            {
                Assert.True(log.IsEnabled);
                path = log.FilePath!;
                log.Info("test", "written to file");
            }

            Assert.Matches(@"ps4mem-\d{8}-\d{6}\.log$", path);
            var text = File.ReadAllText(path);
            Assert.Contains("PS4 Mem debug log started", text);
            Assert.Contains("console 192.168.0.99:744", text);
            Assert.Contains("[test] written to file", text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Writing_after_dispose_does_not_throw()
    {
        var log = new DebugLog(new StringWriter());
        log.Dispose();
        log.Info("test", "late line");
    }
}
