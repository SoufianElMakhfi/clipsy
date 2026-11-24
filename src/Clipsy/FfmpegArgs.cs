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

    public static bool IsHevc(string encoder) => encoder.StartsWith("hevc", StringComparison.Ordinal);
    public static bool IsAv1(string encoder) => encoder.StartsWith("av1", StringComparison.Ordinal);

    /// <summary>MPEG-TS kann kein AV1 → für AV1 Matroska im Live-Modus (auch halb geschriebene Segmente lesbar).</summary>
    public static string SegmentExtension(string encoder) => IsAv1(encoder) ? "mkv" : "ts";

    /// <summary>
    /// Obergrenze der Bitrate. Kodiert wird qualitätsbasiert (ruhige Szenen = wenig MB),
    /// die Grenze greift nur bei sehr unruhigen Szenen. HEVC braucht für gleiche Qualität ~40 % weniger, AV1 noch weniger.
    /// </summary>
    public static int BitrateKbps(int height, int fps, string encoder = "h264")
    {
        double kbps = IsAv1(encoder) ? 9000 : IsHevc(encoder) ? 12000 : 16000;   // 1080p60
        if (height < 1080) kbps *= 0.5;
        if (fps < 60) kbps *= 0.65;
        return (int)kbps;
    }

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

    /// <summary>
    /// Qualitätsbasiertes Encoding. Gemessen an echtem Material (VMAF, 30-s-Clip 1080p60):
    /// alt 20 Mbit/s H.264 = 71 MB / VMAF 95,2 · H.264 QVBR = 43 MB / 94,9 · HEVC QVBR = 22 MB / 94,2
    /// (VMAF-Unterschied &lt; 1 Punkt = mit bloßem Auge nicht sichtbar).
    /// </summary>
    public static List<string> EncoderArgs(string encoder, int maxKbps)
    {
        string max = $"{maxKbps}k", target = $"{maxKbps * 2 / 3}k", buf = $"{maxKbps * 2}k";
        return encoder switch
        {
            // AV1 (RTX 40/50, RX 7000/9000, Intel Arc) – auf Clipsys Test-PC nicht vorhanden; der Encoder-Test
            // beim Start prüft genau diese Optionen und fällt sonst automatisch auf HEVC zurück.
            "av1_nvenc" => ["-c:v", "av1_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "30", "-b:v", "0", "-maxrate", max, "-bufsize", buf],
            "av1_amf" => ["-c:v", "av1_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "qvbr",
                          "-qvbr_quality_level", "23", "-b:v", target, "-maxrate", max],
            "av1_qsv" => ["-c:v", "av1_qsv", "-preset", "faster", "-global_quality", "28", "-maxrate", max],
            "hevc_nvenc" => ["-c:v", "hevc_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "25", "-b:v", "0", "-maxrate", max, "-bufsize", buf],
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "22", "-b:v", "0", "-maxrate", max, "-bufsize", buf],
            "hevc_amf" or "h264_amf" => ["-c:v", encoder, "-usage", "transcoding", "-quality", "quality", "-rc", "qvbr",
                                         "-qvbr_quality_level", "23", "-b:v", target, "-maxrate", max],
            "hevc_qsv" => ["-c:v", "hevc_qsv", "-preset", "faster", "-global_quality", "25", "-maxrate", max],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "23", "-maxrate", max],
            _ => ["-c:v", "libx264", "-preset", "superfast", "-crf", "23", "-maxrate", max, "-bufsize", buf],
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

        a.AddRange(EncoderArgs(encoder, BitrateKbps(height, fps, encoder)));
        a.AddRange(["-g", S(fps), "-bf", "0"]); // 1 Keyframe/Sekunde → sauber schneidbare Segmente
        if (audio.Count > 0) a.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"]);

        a.AddRange(["-max_muxing_queue_size", "4096",
            "-f", "segment", "-segment_time", S(SegmentSeconds), "-segment_wrap", S(SegmentSlots(clipSeconds)),
            "-reset_timestamps", "0"]);
        if (IsAv1(encoder)) a.AddRange(["-segment_format", "matroska", "-segment_format_options", "live=1"]);
        else a.AddRange(["-segment_format", "mpegts"]);
        a.Add(outputPattern);
        return a;
    }

    /// <summary>MKV-Segmente (AV1) über eine Liste zusammenfügen – Liste: Zeilen "file 'segNNN.mkv'".</summary>
    public static List<string> ConcatList(string listFile, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "concat", "-safe", "0", "-i", listFile,
        "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart",
        "-f", "mp4", outputPath,
    ];

    /// <summary>Segmente (Dateinamen relativ zum Arbeitsordner) ohne Neu-Encoding zu einer MP4 zusammenfügen.</summary>
    public static List<string> Concat(IEnumerable<string> segmentNames, string outputPath, bool hevc = false)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", "concat:" + string.Join("|", segmentNames),
            "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart",
        };
        if (hevc) a.AddRange(["-tag:v", "hvc1"]); // damit auch iPhone/Mac/QuickTime HEVC abspielen
        a.AddRange(["-f", "mp4", outputPath]);
        return a;
    }

    /// <summary>
    /// Nachkomprimieren zu AV1 (SVT-AV1, Preset 8, CRF 30). Gemessen am HEVC-Clip:
    /// 21,9 MB → 8,8 MB bei VMAF 93,1 (vorher 94,2) – kein sichtbarer Unterschied. Ton wird nur kopiert.
    /// </summary>
    public static List<string> PostCompress(string input, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-i", input,
        "-map", "0", "-c:v", "libsvtav1", "-preset", "8", "-crf", "30", "-svtav1-params", "log-level=0",
        "-c:a", "copy", "-movflags", "+faststart", "-f", "mp4", output,
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
    static readonly string[] av1 = ["av1_nvenc", "av1_amf", "av1_qsv"];
    static readonly string[] hevc = ["hevc_nvenc", "hevc_amf", "hevc_qsv"];
    static readonly string[] h264 = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    /// <summary>
    /// Reihenfolge je Format: NVIDIA → AMD → Intel. "auto" = AV1, wenn die Grafikkarte es kann, sonst HEVC, sonst H.264.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string codec) => codec switch
    {
        "auto" => [.. av1, .. hevc, .. h264],
        "hevc" => [.. hevc, .. h264],
        _ => h264,
    };

    /// <summary>Erster Encoder, der auf diesem PC wirklich funktioniert; sonst CPU (libx264).</summary>
    public static string Detect(string ffmpeg, string codec)
    {
        foreach (var enc in Candidates(codec))
            if (Works(ffmpeg, enc)) return enc;
        return "libx264";
    }

    static bool Works(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
            // Mit exakt den Aufnahme-Einstellungen testen – so fällt z. B. eine nicht unterstützte Option sofort auf
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=1280x720:r=30:d=0.5", "-vf", "format=nv12" };
            args.AddRange(FfmpegArgs.EncoderArgs(encoder, 4000));
            args.AddRange(["-g", "30", "-bf", "0", "-f", "null", "-"]);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(15000)) { p.Kill(true); return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
