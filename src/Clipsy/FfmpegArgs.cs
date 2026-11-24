using System.Diagnostics;
using System.Globalization;

namespace Clipsy;

public enum CaptureMode { Window, Monitor, DesktopDuplication }

/// <param name="Handle">HWND (Window) bzw. HMONITOR (Monitor)</param>
/// <param name="OutputIndex">Bildschirm-Index für Desktop Duplication (Fallback)</param>
public sealed record CaptureSource(CaptureMode Mode, long Handle, int OutputIndex);

public sealed record AudioInput(string PipePath, string SampleFormat, int SampleRate, int Channels);

/// <summary>Baut alle ffmpeg-Argumentlisten. Rein funktional → testbar.</summary>
public static class FfmpegArgs
{
    public const int SegmentSeconds = 2;

    static string S(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static int BitrateKbps(int height, int fps) => (height >= 1080, fps >= 60) switch
    {
        (true, true) => 20000,
        (true, false) => 12000,
        (false, true) => 10000,
        _ => 6000,
    };

    /// <summary>Anzahl Ring-Buffer-Segmente: Clip-Länge + Reserve, damit nichts beim Speichern überschrieben wird.</summary>
    public static int SegmentSlots(int clipSeconds) => (int)Math.Ceiling(clipSeconds / (double)SegmentSeconds) + 4;

    public static string VideoGraph(CaptureSource src, int height, int fps)
    {
        int width = height >= 1080 ? 1920 : 1280;
        // Immer in eine feste Box (1920x1080 / 1280x720) skalieren, Seitenverhältnis bleibt; CFR für saubere Clips.
        var post = $"hwdownload,format=bgra,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear,format=nv12,fps={fps}";
        // Skalierung schon auf der GPU (spart CPU); dup_frames hält den Takt, wenn das Spiel kurz keine Frames liefert.
        var gfx = $"max_framerate={fps}:capture_cursor=1:display_border=0:dup_frames=1:width={width}:height={height}:resize_mode=scale_aspect";
        return src.Mode switch
        {
            // Windows.Graphics.Capture auf das Spielfenster: andere Fenster (Discord-Overlay, Clipsy-Punkt …) sind NICHT im Bild.
            CaptureMode.Window => $"gfxcapture=hwnd={S(src.Handle)}:{gfx},{post}",
            CaptureMode.Monitor => $"gfxcapture=hmonitor={S(src.Handle)}:{gfx},{post}",
            _ => $"ddagrab=output_idx={src.OutputIndex}:framerate={fps}:draw_mouse=1,{post}",
        };
    }

    public static List<string> EncoderArgs(string encoder, int kbps)
    {
        string b = $"{kbps}k", max = $"{kbps * 3 / 2}k", buf = $"{kbps * 2}k";
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_amf" => ["-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", b, "-maxrate", max],
            _ => ["-c:v", "libx264", "-preset", "superfast", "-b:v", b, "-maxrate", max, "-bufsize", buf],
        };
    }

    /// <param name="audio">0–2 Tonquellen (Spiel-Sound, Mikrofon); mehrere werden zu einer Spur gemischt.</param>
    public static List<string> Record(CaptureSource src, IReadOnlyList<AudioInput> audio, string encoder,
        int height, int fps, int clipSeconds, string outputPattern)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-y",
            // Kein -thread_queue_size: neuere ffmpeg-Versionen lehnen das an Eingängen ab (Fehler -22).
            "-f", "lavfi", "-i", VideoGraph(src, height, fps),
        };
        foreach (var au in audio)
            a.AddRange(["-f", au.SampleFormat, "-ar", S(au.SampleRate), "-ac", S(au.Channels), "-i", au.PipePath]);

        a.AddRange(["-map", "0:v"]);
        if (audio.Count == 1)
        {
            a.AddRange(["-map", "1:a"]);
        }
        else if (audio.Count > 1)
        {
            var graph = "";
            for (int i = 1; i <= audio.Count; i++)
                graph += $"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];";
            for (int i = 1; i <= audio.Count; i++) graph += $"[a{i}]";
            graph += $"amix=inputs={audio.Count}:duration=longest:normalize=0[aout]";
            a.AddRange(["-filter_complex", graph, "-map", "[aout]"]);
        }

        a.AddRange(EncoderArgs(encoder, BitrateKbps(height, fps)));
        a.AddRange(["-g", S(fps), "-bf", "0"]); // 1 Keyframe/Sekunde → sauber schneidbare Segmente
        if (audio.Count > 0) a.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"]);

        a.AddRange(["-max_muxing_queue_size", "4096",
            "-f", "segment", "-segment_time", S(SegmentSeconds), "-segment_wrap", S(SegmentSlots(clipSeconds)),
            "-segment_format", "mpegts", "-reset_timestamps", "0", outputPattern]);
        return a;
    }

    /// <summary>Segmente (Dateinamen relativ zum Arbeitsordner) ohne Neu-Encoding zu einer MP4 zusammenfügen.</summary>
    public static List<string> Concat(IEnumerable<string> segmentNames, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-i", "concat:" + string.Join("|", segmentNames),
        "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart",
        "-f", "mp4", outputPath,
    ];
}

public static class SegmentSelector
{
    /// <summary>Wählt die Segmente, die die letzten <paramref name="clipSeconds"/> Sekunden abdecken (chronologisch).</summary>
    public static List<string> Select(IEnumerable<(string Name, DateTime LastWriteUtc)> segments,
        DateTime nowUtc, int clipSeconds, int segmentSeconds)
    {
        var cutoff = nowUtc.AddSeconds(-(clipSeconds + segmentSeconds));
        return segments.Where(s => s.LastWriteUtc >= cutoff)
                       .OrderBy(s => s.LastWriteUtc)
                       .Select(s => s.Name)
                       .ToList();
    }
}

internal static class FfmpegLocator
{
    public static string? Find()
    {
        string[] local =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        ];
        foreach (var p in local) if (File.Exists(p)) return p;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* ungültiger PATH-Eintrag */ }
        }
        return null;
    }
}

internal static class EncoderProbe
{
    static readonly string[] candidates = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    /// <summary>Erster Hardware-Encoder, der wirklich funktioniert (NVIDIA → AMD → Intel), sonst CPU.</summary>
    public static string Detect(string ffmpeg)
    {
        foreach (var enc in candidates)
            if (Works(ffmpeg, enc)) return enc;
        return "libx264";
    }

    static bool Works(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=1280x720:r=30:d=0.5",
                                      "-vf", "format=nv12", "-c:v", encoder, "-f", "null", "-" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(15000)) { p.Kill(true); return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
