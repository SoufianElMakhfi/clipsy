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
        Assert.Contains("width=1920:height=1080:resize_mode=scale_aspect", g);
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
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Window, 1, 0), [audio], "h264_nvenc", 1080, 60, 30, "seg%03d.ts");
        Assert.Contains("1:a", a);
        Assert.DoesNotContain("-filter_complex", a);
        Assert.Contains(@"\\.\pipe\x", a);
        Assert.Contains("h264_nvenc", a);
        Assert.DoesNotContain("-thread_queue_size", a); // bricht mit aktuellem ffmpeg ab
        Assert.Equal("19", a[a.IndexOf("-segment_wrap") + 1]); // 15 Segmente + 4 Reserve
        Assert.Equal("seg%03d.ts", a[^1]);
    }

    [Fact]
    public void Record_without_audio_has_no_audio_map()
    {
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Monitor, 1, 0), [], "libx264", 720, 30, 15, "o.ts");
        Assert.DoesNotContain("1:a", a);
        Assert.DoesNotContain("aac", a);
    }

    [Fact]
    public void Game_sound_and_mic_are_mixed_into_one_track()
    {
        var game = new AudioInput(@"\\.\pipe\g", "f32le", 48000, 2);
        var mic = new AudioInput(@"\\.\pipe\m", "f32le", 44100, 1);
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Window, 1, 0), [game, mic], "h264_amf", 1080, 60, 30, "o.ts");
        var graph = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:a]", graph);
        Assert.Contains("[2:a]", graph);
        Assert.Contains("amix=inputs=2", graph);
        Assert.Equal("[aout]", a[a.LastIndexOf("-map") + 1]);
        Assert.Contains("44100", a);
    }

    [Fact]
    public void Concat_joins_segments()
    {
        var a = FfmpegArgs.Concat(["seg001.ts", "seg002.ts"], @"C:\clips\a.mp4");
        Assert.Contains("concat:seg001.ts|seg002.ts", a);
        Assert.Equal(@"C:\clips\a.mp4", a[^1]);
    }

    [Theory]
    [InlineData(1080, 60, false, 16000)]
    [InlineData(1080, 60, true, 12000)]
    [InlineData(720, 30, false, 5200)]
    public void Bitrate_caps(int h, int fps, bool hevc, int expected) =>
        Assert.Equal(expected, FfmpegArgs.BitrateKbps(h, fps, hevc));

    [Fact]
    public void Amf_uses_quality_based_rate_control()
    {
        var a = FfmpegArgs.EncoderArgs("hevc_amf", 12000);
        Assert.Equal("qvbr", a[a.IndexOf("-rc") + 1]);
        Assert.Equal("12000k", a[a.IndexOf("-maxrate") + 1]);
    }

    [Fact]
    public void Nvenc_uses_constant_quality()
    {
        var a = FfmpegArgs.EncoderArgs("hevc_nvenc", 12000);
        Assert.Contains("-cq", a);
        Assert.Equal("0", a[a.IndexOf("-b:v") + 1]);
    }

    [Fact]
    public void Hevc_falls_back_to_h264_then_cpu()
    {
        var c = EncoderProbe.Candidates("hevc");
        Assert.Equal("hevc_nvenc", c[0]);
        Assert.True(c.ToList().IndexOf("h264_amf") > c.ToList().IndexOf("hevc_qsv"));
        Assert.Equal("libx264", c[^1]);
        Assert.DoesNotContain(EncoderProbe.Candidates("h264"), e => e.StartsWith("hevc"));
    }

    [Fact]
    public void Hevc_clips_get_hvc1_tag()
    {
        Assert.Contains("hvc1", FfmpegArgs.Concat(["a.ts"], "o.mp4", hevc: true));
        Assert.DoesNotContain("hvc1", FfmpegArgs.Concat(["a.ts"], "o.mp4", hevc: false));
    }
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

public class AudioDeviceTests
{
    static readonly AudioDeviceInfo speakers = new("spk", "Lautsprecher (Realtek)", false);
    static readonly AudioDeviceInfo headset = new("usb1", "USB Headset", true);
    static readonly AudioDeviceInfo usbMic = new("usb2", "USB Mikrofon", true);

    [Fact]
    public void Usb_has_priority_over_windows_default() =>
        Assert.Equal("usb1", AudioDevices.PickAuto([speakers, headset], "spk"));

    [Fact]
    public void Default_usb_wins_among_several_usb() =>
        Assert.Equal("usb2", AudioDevices.PickAuto([headset, usbMic, speakers], "usb2"));

    [Fact]
    public void Without_usb_windows_default_is_used() =>
        Assert.Equal("spk", AudioDevices.PickAuto([speakers], "spk"));

    [Fact]
    public void No_devices_gives_null() => Assert.Null(AudioDevices.PickAuto([], null));
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
        Assert.Equal("hevc", s.Codec);
        Assert.True(s.Autostart);
        Assert.Equal(AudioDevices.Auto, s.AudioOutput);
        Assert.Equal(AudioDevices.Off, s.Microphone); // Mikro nur auf Wunsch
    }

    [Theory]
    [InlineData(30, "30 Sekunden")]
    [InlineData(60, "1 Min")]
    [InlineData(90, "1,5 Min")]
    [InlineData(300, "5 Min")]
    public void Length_labels(int s, string expected) => Assert.Equal(expected, AppSettings.LengthLabel(s));

    [Fact]
    public void Normalize_fixes_invalid_values()
    {
        var s = new AppSettings { Height = 2160, Fps = 144, ClipSeconds = 9999, OutputFolder = "relativ", ClipHotkey = "x+y" }.Normalize();
        Assert.Equal(1080, s.Height);   // kein 4K
        Assert.Equal(60, s.Fps);
        Assert.Equal(300, s.ClipSeconds);
        Assert.True(Path.IsPathFullyQualified(s.OutputFolder));
        Assert.Equal("F10", s.ClipHotkey);
    }
}
