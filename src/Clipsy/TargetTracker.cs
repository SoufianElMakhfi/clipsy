using System.Diagnostics;
using System.Text;

namespace Clipsy;

/// <summary>Was aufgenommen wird: ein Spielfenster (Hwnd ≠ 0) oder ein ganzer Monitor.</summary>
public sealed record CaptureTarget(IntPtr Hwnd, IntPtr Monitor, string Name, int OutputIndex)
{
    public bool IsWindow => Hwnd != IntPtr.Zero;
}

/// <summary>Erkennt Spiele: Vordergrundfenster, das seinen ganzen Monitor bedeckt (Vollbild / randloses Fenster).</summary>
internal static class TargetTracker
{
    static readonly HashSet<string> ignoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    static readonly int ownPid = Environment.ProcessId;

    public static CaptureTarget? FindFullscreenForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return null;

        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (ignoredClasses.Contains(cls.ToString())) return null;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return null;

        if (!Native.GetWindowRect(hwnd, out var r)) return null;
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return null;

        var m = mi.rcMonitor;
        bool fullscreen = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        if (!fullscreen) return null;

        return new CaptureTarget(hwnd, mon, ProcessName(pid), OutputIndex(m));
    }

    public static CaptureTarget PrimaryMonitor()
    {
        var mon = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        int idx = Native.GetMonitorInfo(mon, ref mi) ? OutputIndex(mi.rcMonitor) : 0;
        return new CaptureTarget(IntPtr.Zero, mon, "Desktop", idx);
    }

    static int OutputIndex(Native.RECT m)
    {
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            if (b.Left == m.Left && b.Top == m.Top && b.Right == m.Right && b.Bottom == m.Bottom) return i;
        }
        return 0;
    }

    static string ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "Spiel"; }
    }
}
