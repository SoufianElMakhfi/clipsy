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
    [InlineData(1080, 60, "h264_amf", 16000)]
    [InlineData(1080, 60, "hevc_amf", 12000)]
    [InlineData(1080, 60, "av1_nvenc", 9000)]
    [InlineData(720, 30, "libx264", 5200)]
    public void Bitrate_caps(int h, int fps, string enc, int expected) =>
        Assert.Equal(expected, FfmpegArgs.BitrateKbps(h, fps, enc));

    [Fact]
    public void Auto_prefers_av1_then_hevc_then_h264()
    {
        var c = EncoderProbe.Candidates("auto").ToList();
        Assert.Equal("av1_nvenc", c[0]);
        Assert.True(c.IndexOf("av1_qsv") < c.IndexOf("hevc_nvenc"));
        Assert.True(c.IndexOf("hevc_qsv") < c.IndexOf("h264_nvenc"));
        Assert.Equal("libx264", c[^1]);
        Assert.DoesNotContain(EncoderProbe.Candidates("hevc"), e => e.StartsWith("av1"));
    }

    [Fact]
    public void Av1_records_into_live_mkv_segments()
    {
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Window, 1, 0), [], "av1_nvenc", 1080, 60, 30, "seg%03d.mkv");
        Assert.Equal("matroska", a[a.IndexOf("-segment_format") + 1]);
        Assert.Equal("live=1", a[a.IndexOf("-segment_format_options") + 1]);
        Assert.Equal("mkv", FfmpegArgs.SegmentExtension("av1_amf"));
        Assert.Equal("ts", FfmpegArgs.SegmentExtension("hevc_nvenc"));
    }

    [Fact]
    public void Hevc_records_into_mpegts_segments()
    {
        var a = FfmpegArgs.Record(new CaptureSource(CaptureMode.Window, 1, 0), [], "hevc_nvenc", 1080, 60, 30, "seg%03d.ts");
        Assert.Equal("mpegts", a[a.IndexOf("-segment_format") + 1]);
        Assert.DoesNotContain("-segment_format_options", a);
    }

    [Fact]
    public void Av1_clip_uses_concat_list()
    {
        var a = FfmpegArgs.ConcatList("list.txt", "o.mp4");
        Assert.Equal("concat", a[a.IndexOf("-f") + 1]);
        Assert.Equal("list.txt", a[a.IndexOf("-i") + 1]);
    }

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
    public void PostCompress_uses_svtav1_and_copies_audio()
    {
        var a = FfmpegArgs.PostCompress(@"C:\c\clip.mp4", @"C:\c\~clipsy-clip.mp4");
        Assert.Equal("libsvtav1", a[a.IndexOf("-c:v") + 1]);
        Assert.Equal("30", a[a.IndexOf("-crf") + 1]);
        Assert.Equal("copy", a[a.IndexOf("-c:a") + 1]);
        Assert.Equal(@"C:\c\~clipsy-clip.mp4", a[^1]);
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

public class CodecSupportTests
{
    static readonly CodecSupport.Status none = new(false, false), all = new(true, true);

    [Fact]
    public void Av1_recording_needs_av1_extension() =>
        Assert.Equal("AV1", CodecSupport.Missing(none, "av1_nvenc", postCompress: false));

    [Fact]
    public void Hevc_recording_needs_hevc_extension() =>
        Assert.Equal("HEVC", CodecSupport.Missing(none, "hevc_amf", postCompress: false));

    [Fact]
    public void Post_compress_turns_everything_into_av1() =>
        Assert.Equal("AV1", CodecSupport.Missing(new(false, true), "hevc_amf", postCompress: true));

    [Fact]
    public void H264_needs_nothing() => Assert.Null(CodecSupport.Missing(none, "h264_nvenc", postCompress: false));

    [Fact]
    public void Unknown_status_gives_no_hint() => Assert.Null(CodecSupport.Missing(null, "av1_nvenc", false));

    [Fact]
    public void Installed_gives_no_hint() => Assert.Null(CodecSupport.Missing(all, "av1_amf", true));
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
        Assert.Equal("auto", s.Codec);
        Assert.False(s.PostCompress); // nur auf Wunsch
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
