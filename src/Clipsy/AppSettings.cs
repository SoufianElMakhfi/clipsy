using System.Text.Json;
using Microsoft.Win32;

namespace Clipsy;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class AppSettings
{
    public string ClipHotkey { get; set; } = "F10";
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+F10";
    public int Height { get; set; } = 1080;          // 720 oder 1080
    public int Fps { get; set; } = 60;               // 30 oder 60
    public string Codec { get; set; } = "hevc";      // "hevc" (≈70 % kleiner) oder "h264" (maximal kompatibel)
    public int ClipSeconds { get; set; } = 30;
    public string OutputFolder { get; set; } = DefaultFolder;
    public string AudioOutput { get; set; } = AudioDevices.Auto;   // Spiel-Sound: off / auto / default / Geräte-ID
    public string Microphone { get; set; } = AudioDevices.Off;     // Mikrofon: standardmäßig aus (Datenschutz)
    public bool ShowOverlay { get; set; } = true;
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopLeft;
    public bool ClipSound { get; set; } = true;
    public bool Autostart { get; set; } = true;

    public static readonly int[] ClipLengths = [15, 30, 45, 60, 90, 120, 180, 300];
    public const int MaxClipSeconds = 300;

    public static string LengthLabel(int seconds) =>
        seconds < 60 ? $"{seconds} Sekunden"
        : seconds % 60 == 0 ? $"{seconds / 60} Min"
        : $"{seconds / 60},{seconds % 60 * 10 / 60} Min";

    static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clipsy");

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipsy", "settings.json");

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Ungültige Werte (z. B. von Hand editierte Datei) auf sichere Standards zurücksetzen.</summary>
    public AppSettings Normalize()
    {
        if (Height is not (720 or 1080)) Height = 1080;
        if (Fps is not (30 or 60)) Fps = 60;
        if (Codec is not ("hevc" or "h264")) Codec = "hevc";
        ClipSeconds = Math.Clamp(ClipSeconds, 5, MaxClipSeconds);
        if (string.IsNullOrWhiteSpace(AudioOutput) || AudioOutput.Length > 300) AudioOutput = AudioDevices.Auto;
        if (string.IsNullOrWhiteSpace(Microphone) || Microphone.Length > 300) Microphone = AudioDevices.Off;
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Path.IsPathFullyQualified(OutputFolder)) OutputFolder = DefaultFolder;
        if (!Hotkey.TryParse(ClipHotkey, out _)) ClipHotkey = "F10";
        if (!string.IsNullOrEmpty(ToggleHotkey) && !Hotkey.TryParse(ToggleHotkey, out _)) ToggleHotkey = "";
        if (!Enum.IsDefined(OverlayCorner)) OverlayCorner = OverlayCorner.TopLeft;
        return this;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()).Normalize();
        }
        catch (Exception ex) { Log.Write($"Einstellungen unlesbar, nutze Standard: {ex.Message}"); }
        return new AppSettings().Normalize();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, json));
        File.Move(tmp, FilePath, true);
    }
}

internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Clipsy";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(Name, $"\"{exe}\" --autostart");
            else
                key.DeleteValue(Name, false);
        }
        catch (Exception ex) { Log.Write($"Autostart konnte nicht gesetzt werden: {ex.Message}"); }
    }
}

internal static class Log
{
    static readonly object gate = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, "clipsy.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging darf nie crashen */ }
    }
}
