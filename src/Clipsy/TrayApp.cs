using System.Diagnostics;
using System.Media;

namespace Clipsy;

/// <summary>Läuft unsichtbar im Tray: Hotkeys, Status-Punkt, Spielerkennung, Watchdog.</summary>
internal sealed class TrayApp : ApplicationContext
{
    static readonly Color Green = Color.FromArgb(46, 204, 64);
    static readonly Color Red = Color.FromArgb(230, 50, 50);
    static readonly Color Orange = Color.FromArgb(255, 160, 0);

    AppSettings settings;
    readonly Recorder? recorder;
    readonly PostCompressor? compressor;
    readonly HotkeyManager hotkeys = new();
    readonly StatusOverlay overlay = new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<Color, Icon> icons = new();
    readonly ToolStripMenuItem clipItem = new("Clip speichern");
    readonly ToolStripMenuItem toggleItem = new("Aufnahme deaktivieren");
    SettingsForm? settingsForm;
    bool saving;

    public TrayApp()
    {
        settings = AppSettings.Load();
        Autostart.Apply(settings.Autostart);

        var menu = new ContextMenuStrip();
        clipItem.Click += (_, _) => SaveClip();
        toggleItem.Click += (_, _) => Toggle();
        menu.Items.Add(clipItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add("📂 Clip-Ordner öffnen", null, (_, _) => OpenFolder());
        menu.Items.Add("⚙ Einstellungen", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Quit());

        tray = new NotifyIcon { Text = "Clipsy", Visible = true, ContextMenuStrip = menu, Icon = IconFor(Orange) };
        tray.DoubleClick += (_, _) => OpenSettings();

        overlay.SetCorner(settings.OverlayCorner);
        if (settings.ShowOverlay) overlay.Show();

        var ff = FfmpegLocator.Find();
        if (ff is null)
        {
            Log.Write("ffmpeg.exe nicht gefunden");
            tray.ShowBalloonTip(10000, "Clipsy", "ffmpeg.exe fehlt – bitte das komplette Release-ZIP entpacken.", ToolTipIcon.Error);
        }
        else
        {
            recorder = new Recorder(ff, settings);
            recorder.SetTarget(TargetTracker.PrimaryMonitor());
            recorder.SetEnabled(true); // nach jedem Start (z. B. PC-Neustart) immer wieder AN
            compressor = new PostCompressor(ff);
            PostCompressor.CleanLeftovers(settings.OutputFolder);
        }

        RegisterHotkeys();
        timer.Tick += (_, _) => OnTick();
        timer.Start();
        UpdateVisuals();
    }

    void OnTick()
    {
        if (recorder is null) return;
        var game = TargetTracker.FindFullscreenForeground();
        if (recorder.Enabled)
        {
            if (game is not null)
                recorder.SetTarget(game);
            else if (recorder.Target is { IsWindow: true } t && !Native.IsWindow(t.Hwnd))
                recorder.SetTarget(TargetTracker.PrimaryMonitor()); // Spiel beendet
        }
        recorder.Tick();
        compressor?.Tick(gameRunning: game is not null);
        UpdateVisuals();
    }

    void RegisterHotkeys()
    {
        hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (Hotkey.TryParse(settings.ClipHotkey, out var clip) && !hotkeys.Register(clip, SaveClip))
            failed.Add(settings.ClipHotkey);
        if (Hotkey.TryParse(settings.ToggleHotkey, out var toggle) && !hotkeys.Register(toggle, Toggle))
            failed.Add(settings.ToggleHotkey);
        if (failed.Count > 0)
            tray.ShowBalloonTip(6000, "Clipsy", $"Taste schon belegt: {string.Join(", ", failed)} – bitte im ⚙ ändern.", ToolTipIcon.Warning);
        clipItem.Text = $"Clip speichern ({settings.ClipHotkey})";
    }

    async void SaveClip()
    {
        if (recorder is null || saving) return;
        if (!recorder.Enabled || recorder.State != RecorderState.Recording)
        {
            overlay.Flash(Red);
            return;
        }
        saving = true;
        overlay.Flash(Color.White);
        try
        {
            var path = await recorder.SaveClipAsync();
            if (path is not null)
            {
                if (settings.ClipSound) SystemSounds.Asterisk.Play();
                if (settings.PostCompress) compressor?.Enqueue(path);
                overlay.Flash(Color.DeepSkyBlue);
            }
            else
            {
                overlay.Flash(Red);
                tray.ShowBalloonTip(4000, "Clipsy", "Clip konnte nicht gespeichert werden (Details im Log).", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Clip-Fehler: {ex}");
        }
        finally { saving = false; }
    }

    void Toggle()
    {
        if (recorder is null) return;
        recorder.SetEnabled(!recorder.Enabled);
        recorder.Tick();
        UpdateVisuals();
    }

    (bool enabled, string text, Color color) Status()
    {
        if (recorder is null) return (false, "ffmpeg fehlt", Red);
        return recorder.State switch
        {
            RecorderState.Off => (false, "Deaktiviert", Red),
            RecorderState.Recording => (true, $"Aktiv – {recorder.Mode}", Green),
            RecorderState.Error => (true, "Fehler – starte neu …", Orange),
            _ => (true, "Startet …", Orange),
        };
    }

    void UpdateVisuals()
    {
        var (enabled, text, color) = Status();
        overlay.SetColor(color);
        tray.Icon = IconFor(color);
        var tip = $"Clipsy – {text}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        toggleItem.Text = enabled ? "Aufnahme deaktivieren" : "Aufnahme aktivieren";
    }

    Icon IconFor(Color c)
    {
        if (icons.TryGetValue(c, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            using var pen = new Pen(Color.FromArgb(30, 30, 30), 3);
            g.FillEllipse(b, 3, 3, 26, 26);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        icon = Icon.FromHandle(bmp.GetHicon());
        icons[c] = icon;
        return icon;
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.OutputFolder);
            Process.Start(new ProcessStartInfo { FileName = settings.OutputFolder, UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"Ordner öffnen fehlgeschlagen: {ex.Message}"); }
    }

    void OpenSettings()
    {
        if (settingsForm is not null) { settingsForm.Activate(); return; }

        hotkeys.UnregisterAll(); // sonst landet z. B. F10 nicht im Tasten-Feld
        settingsForm = new SettingsForm(settings, recorder?.Encoder ?? "wird ermittelt …", Status, Toggle, OpenFolder);
        settingsForm.Saved += s =>
        {
            settings = s;
            try { settings.Save(); } catch (Exception ex) { Log.Write($"Speichern fehlgeschlagen: {ex.Message}"); }
            Autostart.Apply(settings.Autostart);
            recorder?.ApplySettings(settings);
            overlay.SetCorner(settings.OverlayCorner);
            if (settings.ShowOverlay) overlay.Show(); else overlay.Hide();
        };
        settingsForm.FormClosed += (_, _) =>
        {
            settingsForm = null;
            RegisterHotkeys();
        };
        settingsForm.Show();
        settingsForm.Activate();
    }

    void Quit()
    {
        timer.Stop();
        hotkeys.Dispose();
        compressor?.Dispose();
        recorder?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        overlay.Close();
        ExitThread();
    }
}
