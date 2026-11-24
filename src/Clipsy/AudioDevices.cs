using NAudio.CoreAudioApi;

namespace Clipsy;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsUsb);

/// <summary>Audiogeräte auflisten und Einstellung ("auto", "default", "off" oder Geräte-ID) auflösen.</summary>
public static class AudioDevices
{
    public const string Auto = "auto";
    public const string Default = "default";
    public const string Off = "off";

    // PKEY_Device_EnumeratorName → "USB", "HDAUDIO", "BTHENUM" …
    static readonly PropertyKey enumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public static List<AudioDeviceInfo> List(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d) list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, IsUsb(d)));
            }
        }
        catch (Exception ex) { Log.Write($"Audiogeräte nicht lesbar: {ex.Message}"); }
        return list;
    }

    public static string? DefaultId(DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(flow, Role.Console)) return null;
            using var d = en.GetDefaultAudioEndpoint(flow, Role.Console);
            return d.ID;
        }
        catch { return null; }
    }

    /// <summary>Einstellung → konkrete Geräte-ID (null = kein Gerät / aus).</summary>
    public static string? Resolve(string? setting, DataFlow flow)
    {
        if (setting == Off) return null;
        var def = DefaultId(flow);
        if (setting == Default) return def;
        var list = List(flow);
        if (!string.IsNullOrEmpty(setting) && setting != Auto && list.Any(d => d.Id == setting)) return setting;
        return PickAuto(list, def); // "auto" oder gewähltes Gerät gerade nicht angeschlossen
    }

    /// <summary>USB hat Vorrang (bei mehreren: das Windows-Standardgerät, falls USB), sonst Windows-Standard.</summary>
    public static string? PickAuto(IReadOnlyList<AudioDeviceInfo> devices, string? defaultId)
    {
        var usb = devices.Where(d => d.IsUsb).ToList();
        if (usb.Count > 0) return usb.FirstOrDefault(d => d.Id == defaultId)?.Id ?? usb[0].Id;
        return defaultId ?? devices.FirstOrDefault()?.Id;
    }

    static bool IsUsb(MMDevice d)
    {
        try
        {
            var props = d.Properties;
            if (props.Contains(enumeratorName) && props[enumeratorName].Value is string s)
                return s.Equals("USB", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* Eigenschaft nicht verfügbar */ }
        return d.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase);
    }
}
