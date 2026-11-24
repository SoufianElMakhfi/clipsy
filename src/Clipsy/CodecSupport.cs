using System.Diagnostics;

namespace Clipsy;

/// <summary>
/// Prüft, ob die Windows-Video-Erweiterungen installiert sind, die "Medienwiedergabe"/"Filme &amp; TV"/"Fotos"
/// zum Abspielen von AV1 bzw. HEVC brauchen. (Browser, Discord und VLC brauchen sie nicht.)
/// </summary>
internal static class CodecSupport
{
    public sealed record Status(bool Av1, bool Hevc);

    public const string Av1StoreUrl = "ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V";   // kostenlos
    public const string HevcStoreUrl = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";  // 0,99 €

    static Task<Status?>? check;

    /// <summary>Einmal pro Programmstart im Hintergrund; null = unbekannt (dann kein Hinweis).</summary>
    public static Task<Status?> CheckAsync() => check ??= Task.Run(Check);

    static Status? Check()
    {
        try
        {
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(ps)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Get-AppxPackage | Where-Object { $_.Name -match 'AV1VideoExtension|HEVCVideoExtension' } | ForEach-Object Name" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20000) || p.ExitCode != 0) return null;
            return new Status(output.Contains("AV1VideoExtension", StringComparison.OrdinalIgnoreCase),
                              output.Contains("HEVCVideoExtension", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Write($"Codec-Prüfung nicht möglich: {ex.Message}");
            return null;
        }
    }

    /// <summary>Welche Erweiterung fehlt für die Clips, die gerade entstehen? null = nichts.</summary>
    public static string? Missing(Status? s, string? encoder, bool postCompress)
    {
        if (s is null || encoder is null) return null;
        bool av1 = postCompress || FfmpegArgs.IsAv1(encoder);
        if (av1) return s.Av1 ? null : "AV1";
        if (FfmpegArgs.IsHevc(encoder)) return s.Hevc ? null : "HEVC";
        return null;
    }

    public static string StoreUrl(string codec) => codec == "AV1" ? Av1StoreUrl : HevcStoreUrl;

    public static void OpenStore(string codec)
    {
        try { Process.Start(new ProcessStartInfo(StoreUrl(codec)) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Store nicht öffnbar: {ex.Message}"); }
    }
}
