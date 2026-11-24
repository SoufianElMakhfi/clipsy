using System.Diagnostics;
using System.Media;

namespace Clipsy;

/// <summary>Läuft unsichtbar im Tray: Hotkeys, Status-Logo, Clip-Meldungen, Spielerkennung, Watchdog.</summary>
internal sealed class TrayApp : ApplicationContext
{
    AppSettings settings;
    readonly Recorder? recorder;
    readonly PostCompressor? compressor;
    readonly HotkeyManager hotkeys = new();
    readonly StatusOverlay overlay = new();
    readonly ClipToast toast = new();
    readonly EventWaitHandle showSettings = new(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<Color, Icon> icons = new();
    readonly ToolStripMenuItem clipItem = new("Clip speichern");
    readonly ToolStripMenuItem toggleItem = new("Pausieren");
    SettingsForm? settingsForm;
    bool saving;
    bool codecHintShown;
    string? balloonStoreCodec;

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

        tray = new NotifyIcon { Text = "Clipsy", Visible = true, ContextMenuStrip = menu, Icon = IconFor(Brand.Orange) };
        tray.DoubleClick += (_, _) => OpenSettings();
        tray.BalloonTipClicked += (_, _) => { if (balloonStoreCodec != null) CodecSupport.OpenStore(balloonStoreCodec); };
        tray.BalloonTipClosed += (_, _) => balloonStoreCodec = null;

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
            _ = CodecSupport.CheckAsync(); // Video-Erweiterungen schon mal im Hintergrund prüfen
        }

        // Zweiter Start der EXE → Zahnrad öffnen
        var ui = SynchronizationContext.Current;
        ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => ui?.Post(_ => OpenSettings(), null),
                                               null, Timeout.Infinite, executeOnlyOnce: false);

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

    void Notify(string title, string subtitle, Color accent)
    {
        if (settings.ShowToast) toast.ShowMessage(title, subtitle, accent, settings.ToastCorner);
    }

    async void SaveClip()
    {
        if (recorder is null || saving) return;
        if (!recorder.Enabled)
        {
            overlay.Flash(Brand.Red);
            Notify("Clipsy ist pausiert", string.IsNullOrEmpty(settings.ToggleHotkey)
                ? "Über das Tray-Menü wieder aktivieren" : $"Mit {settings.ToggleHotkey} wieder aktivieren", Brand.Red);
            return;
        }
        if (recorder.State != RecorderState.Recording)
        {
            overlay.Flash(Brand.Orange);
            Notify("Noch kein Clip möglich", "Aufnahme startet gerade – gleich nochmal versuchen", Brand.Orange);
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
                var mb = new FileInfo(path).Length / 1048576.0;
                Notify("Clip gespeichert", $"{AppSettings.LengthLabel(settings.ClipSeconds)} · {mb:0.0} MB · {Path.GetFileName(path)}", Brand.Green);
                overlay.Flash(Brand.Cyan);
                // Schon als AV1 aufgenommen (RTX 40/50 usw.) → Nachkomprimieren bringt nichts
                if (settings.PostCompress && !FfmpegArgs.IsAv1(recorder.Encoder ?? "")) compressor?.Enqueue(path);
                _ = HintMissingCodecAsync();
            }
            else
            {
                overlay.Flash(Brand.Red);
                Notify("Clip fehlgeschlagen", "Details stehen im Log – Aufnahme läuft weiter", Brand.Red);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Clip-Fehler: {ex}");
        }
        finally { saving = false; }
    }

    /// <summary>Einmal pro Start: Hinweis, falls Windows die Clips ohne Erweiterung nicht abspielen kann.</summary>
    async Task HintMissingCodecAsync()
    {
        if (codecHintShown) return;
        var missing = CodecSupport.Missing(await CodecSupport.CheckAsync(), recorder?.Encoder, settings.PostCompress);
        if (missing is null) return;
        codecHintShown = true;
        balloonStoreCodec = missing;
        var price = missing == "AV1" ? "kostenlos" : "0,99 €";
        tray.ShowBalloonTip(10000, $"{missing}-Erweiterung fehlt",
            $"Die Windows-Medienwiedergabe braucht die {missing}-Erweiterung ({price}), um deine Clips abzuspielen. " +
            "Browser, Discord und VLC gehen auch ohne. Hier klicken, um sie im Microsoft Store zu öffnen.", ToolTipIcon.Info);
    }

    void Toggle()
    {
        if (recorder is null) return;
        recorder.SetEnabled(!recorder.Enabled);
        recorder.Tick();
        UpdateVisuals();
        if (recorder.Enabled) Notify("Clipsy aktiv", $"{settings.ClipHotkey} speichert die letzten {AppSettings.LengthLabel(settings.ClipSeconds)}", Brand.Green);
        else Notify("Clipsy pausiert", "Nach einem Neustart ist Clipsy automatisch wieder an", Brand.Red);
    }

    (bool enabled, string text, Color color) Status()
    {
        if (recorder is null) return (false, "ffmpeg fehlt", Brand.Red);
        return recorder.State switch
        {
            RecorderState.Off => (false, "Pausiert", Brand.Red),
            RecorderState.Recording => (true, $"Aktiv – {recorder.Mode}", Brand.Green),
            RecorderState.Error => (true, "Fehler – starte neu …", Brand.Orange),
            _ => (true, "Startet …", Brand.Orange),
        };
    }

    void UpdateVisuals()
    {
        var (enabled, text, color) = Status();
        overlay.SetColor(color);
        tray.Icon = IconFor(color);
        var tip = $"Clipsy – {text}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        toggleItem.Text = enabled ? "⏸ Pausieren" : "▶ Aktivieren";
    }

    Icon IconFor(Color c)
    {
        if (!icons.TryGetValue(c, out var icon))
            icons[c] = icon = Brand.StatusIcon(c);
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
        settingsForm = new SettingsForm(settings, () => recorder?.Encoder, Status, Toggle, OpenFolder);
        settingsForm.Saved += s =>
        {
            settings = s;
            try { settings.Save(); } catch (Exception ex) { Log.Write($"Speichern fehlgeschlagen: {ex.Message}"); }
            Autostart.Apply(settings.Autostart);
            recorder?.ApplySettings(settings);
            overlay.SetCorner(settings.OverlayCorner);
            if (settings.ShowOverlay) overlay.Show(); else overlay.Hide();
            codecHintShown = false;
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
        toast.Close();
        overlay.Close();
        ExitThread();
    }
}
