namespace Clipsy;

/// <summary>Tastenkombination wie "F10" oder "Ctrl+Shift+F10".</summary>
public readonly record struct Hotkey(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    static readonly HashSet<Keys> ModifierKeys =
    [
        Keys.None, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.ShiftKey, Keys.LShiftKey,
        Keys.RShiftKey, Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin,
    ];

    public uint Modifiers =>
        (Ctrl ? Native.MOD_CONTROL : 0) | (Alt ? Native.MOD_ALT : 0) |
        (Shift ? Native.MOD_SHIFT : 0) | (Win ? Native.MOD_WIN : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "strg" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift" or "umschalt": shift = true; break;
                case "win": win = true; break;
                default:
                    if (key != null || !Enum.TryParse(raw, true, out Keys k) || ModifierKeys.Contains(k)) return false;
                    if ((k & ~Keys.KeyCode) != 0) return false;
                    key = k;
                    break;
            }
        }
        if (key == null) return false;
        hotkey = new Hotkey(key.Value, ctrl, alt, shift, win);
        return true;
    }

    /// <summary>Aus WinForms-KeyData (z. B. Keys.Control | Keys.F10). Null bei reiner Modifier-Taste.</summary>
    public static Hotkey? FromKeyData(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (ModifierKeys.Contains(key)) return null;
        return new Hotkey(key, keyData.HasFlag(Keys.Control), keyData.HasFlag(Keys.Alt), keyData.HasFlag(Keys.Shift), false);
    }
}

/// <summary>Globale Hotkeys über RegisterHotKey (funktioniert auch, wenn ein Spiel im Vordergrund ist).</summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    public HotkeyManager() => CreateHandle(new CreateParams());

    public bool Register(Hotkey hk, Action action)
    {
        int id = nextId++;
        if (!Native.RegisterHotKey(Handle, id, hk.Modifiers | Native.MOD_NOREPEAT, (uint)hk.Key)) return false;
        actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys) Native.UnregisterHotKey(Handle, id);
        actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && actions.TryGetValue((int)m.WParam, out var action))
        {
            action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
