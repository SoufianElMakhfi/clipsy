using Clipsy;

namespace Clipsy.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("F10", "F10")]
    [InlineData("ctrl+shift+f10", "Ctrl+Shift+F10")]
    [InlineData("Strg+Alt+K", "Ctrl+Alt+K")]
    public void Parses_and_normalizes(string input, string expected)
    {
        Assert.True(Hotkey.TryParse(input, out var hk));
        Assert.Equal(expected, hk.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("F10+F11")]
    [InlineData("Quatsch")]
    public void Rejects_invalid(string input) => Assert.False(Hotkey.TryParse(input, out _));

    [Fact]
    public void FromKeyData_ignores_pure_modifiers()
    {
        Assert.Null(Hotkey.FromKeyData(Keys.ControlKey | Keys.Control));
        Assert.Equal("Alt+F9", Hotkey.FromKeyData(Keys.F9 | Keys.Alt)!.Value.ToString());
    }
}

public class FfmpegArgsTests
{
    [Fact]
    public void Window_capture_uses_gfxcapture_on_hwnd()
    {
        var g = FfmpegArgs.VideoGraph(new CaptureSource(CaptureMode.Window, 12345, 0), 1080, 60);
        Assert.StartsWith("gfxcapture=hwnd=12345:", g);
        Assert.Contains("scale=1920:1080", g);
        Assert.EndsWith("fps=60", g);
    }

    [Fact]
    public void Quality_720p_uses_1280_box()
    {
        var g = FfmpegArgs.VideoGraph(new CaptureSource(CaptureMode.DesktopDuplication, 0, 1), 720, 30);
        Assert.StartsWith("ddagrab=output_idx=1:", g);
        Assert.Contains("scale=1280:720", g);
    }

    [Fact]
    public void Record_includes_audio_pipe_and_ringbuffer()
    {
        var audio = new AudioInput(@"\\.\pipe\x", "f32le", 48000, 2);
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Window, 1, 0), audio, "h264_nvenc", 1080, 60, 30, "seg%03d.ts");
        Assert.Contains(@"\\.\pipe\x", a);
        Assert.Contains("h264_nvenc", a);
        Assert.Equal("19", a[a.IndexOf("-segment_wrap") + 1]); // 15 Segmente + 4 Reserve
        Assert.Equal("seg%03d.ts", a[^1]);
    }

    [Fact]
    public void Record_without_audio_has_no_audio_map()
    {
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Monitor, 1, 0), null, "libx264", 720, 30, 15, "o.ts");
        Assert.DoesNotContain("1:a", a);
        Assert.DoesNotContain("aac", a);
    }

    [Fact]
    public void Concat_joins_segments()
    {
        var a = FfmpegArgs.Concat(["seg001.ts", "seg002.ts"], @"C:\clips\a.mp4");
        Assert.Contains("concat:seg001.ts|seg002.ts", a);
        Assert.Equal(@"C:\clips\a.mp4", a[^1]);
    }

    [Theory]
    [InlineData(1080, 60, 20000)]
    [InlineData(720, 30, 6000)]
    public void Bitrates(int h, int fps, int expected) => Assert.Equal(expected, FfmpegArgs.BitrateKbps(h, fps));
}

public class SegmentSelectorTests
{
    [Fact]
    public void Selects_only_recent_segments_in_order()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            ("seg003.ts", now.AddSeconds(-1)),
            ("seg001.ts", now.AddSeconds(-5)),
            ("seg009.ts", now.AddSeconds(-300)), // alter Ringpuffer-Rest
            ("seg002.ts", now.AddSeconds(-3)),
        };
        var sel = SegmentSelector.Select(segs, now, 4, 2);
        Assert.Equal(["seg001.ts", "seg002.ts", "seg003.ts"], sel);
    }
}

public class ByteFifoTests
{
    [Fact]
    public void Wraps_and_drops_oldest_on_overflow()
    {
        var f = new ByteFifo(4);
        f.Write([1, 2, 3], 0, 3);
        f.Write([4, 5, 6], 0, 3); // 1 und 2 fliegen raus
        var buf = new byte[4];
        Assert.Equal(4, f.Read(buf, 0, 4));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, buf);
        Assert.Equal(0, f.Count);
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_are_1080p60_F10()
    {
        var s = new AppSettings().Normalize();
        Assert.Equal(1080, s.Height);
        Assert.Equal(60, s.Fps);
        Assert.Equal("F10", s.ClipHotkey);
        Assert.True(s.Autostart);
    }

    [Fact]
    public void Normalize_fixes_invalid_values()
    {
        var s = new AppSettings { Height = 2160, Fps = 144, ClipSeconds = 9999, OutputFolder = "relativ", ClipHotkey = "x+y" }.Normalize();
        Assert.Equal(1080, s.Height);   // kein 4K
        Assert.Equal(60, s.Fps);
        Assert.Equal(120, s.ClipSeconds);
        Assert.True(Path.IsPathFullyQualified(s.OutputFolder));
        Assert.Equal("F10", s.ClipHotkey);
    }
}
