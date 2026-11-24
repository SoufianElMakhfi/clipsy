namespace Clipsy;

/// <summary>Das Zahnrad: Status, Ein/Aus, Ordner und alle Einstellungen – in Gruppen, hell/dunkel wie Windows.</summary>
internal sealed class SettingsForm : Form
{
    public event Action<AppSettings>? Saved;

    readonly AppSettings original;
    readonly Func<(bool enabled, string text, Color color)> status;
    readonly Func<string?> currentEncoder;
    CodecSupport.Status? codecs;

    readonly Label statusLabel = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 2, 0, 0) };
    readonly Button toggleButton = HeaderButton("");
    readonly ComboBox quality = Combo("720p", "1080p (Standard)");
    readonly ComboBox fps = Combo("30 FPS", "60 FPS (Standard)");
    readonly ComboBox codec = Combo(
        "Automatisch – AV1 wenn möglich, sonst HEVC (empfohlen)",
        "H.265 / HEVC",
        "H.264 – läuft überall, größere Dateien");
    readonly ComboBox length = Combo(AppSettings.ClipLengths.Select(AppSettings.LengthLabel).ToArray());
    readonly LinkLabel codecHint = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 0, 3, 6) };
    readonly ComboBox outputDevice = Combo();
    readonly ComboBox micDevice = Combo();
    readonly HotkeyBox clipKey = new() { Width = 200 };
    readonly HotkeyBox toggleKey = new() { Width = 200, AllowNone = true };
    readonly TextBox folder = new() { Width = 300 };
    readonly CheckBox postCompress = Check("Clips nachkomprimieren (AV1, ca. 60 % kleiner)");
    readonly CheckBox overlay = Check("Status-Logo anzeigen");
    readonly ComboBox corner = Combo("Oben links", "Oben rechts", "Unten links", "Unten rechts");
    readonly CheckBox toast = Check("Meldung „Clip gespeichert“ einblenden");
    readonly ComboBox toastCorner = Combo("Oben rechts", "Unten rechts (über der Taskleiste)");
    readonly CheckBox sound = Check("Ton beim Speichern");
    readonly CheckBox autostart = Check("Mit Windows starten (empfohlen)");
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    string? missingCodec;

    public SettingsForm(AppSettings current, Func<string?> currentEncoder,
                        Func<(bool enabled, string text, Color color)> status, Action toggle, Action openFolder)
    {
        original = current;
        this.status = status;
        this.currentEncoder = currentEncoder;

        Text = "Clipsy – Einstellungen";
        Icon = Brand.AppIcon;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0) };

        // ---------- Kopfbereich ----------
        var header = new TableLayoutPanel
        {
            ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, BackColor = Brand.Navy,
            Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 12, 0) };
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Brand.Navy };
        titles.Controls.Add(new Label { Text = "Clipsy", AutoSize = true, ForeColor = Brand.Cyan, Font = new Font("Segoe UI", 16f, FontStyle.Bold), Margin = new Padding(0) });
        titles.Controls.Add(statusLabel);
        var openButton = HeaderButton("📂  Clip-Ordner");
        openButton.Click += (_, _) => openFolder();
        toggleButton.Click += (_, _) => { toggle(); UpdateStatus(); };
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(toggleButton, 2, 0);
        header.Controls.Add(openButton, 3, 0);
        root.Controls.Add(header, 0, 0);

        // ---------- Inhalt in zwei Spalten ----------
        var browse = new Button { Text = "…", Width = 34, Height = folder.Height + 2 };
        browse.Click += (_, _) => Browse();
        codecHint.LinkClicked += (_, _) => { if (missingCodec != null) CodecSupport.OpenStore(missingCodec); };
        codec.SelectedIndexChanged += (_, _) => UpdateCodecHint();
        postCompress.CheckedChanged += (_, _) => UpdateCodecHint();

        var left = Column(
            Section("🎥  Aufnahme",
                ("Qualität", quality), ("Bildrate", fps), ("Format", codec), ("", codecHint), ("Clip-Länge", length)),
            Section("🎧  Ton",
                ("Spiel-Sound", outputDevice), ("Mikrofon", micDevice),
                ("", Note("Automatisch = USB-Gerät (Headset, Mikro) hat Vorrang, sonst Windows-Standard."))));
        var right = Column(
            Section("⌨  Tasten",
                ("Clip speichern", clipKey), ("An / Aus", toggleKey),
                ("", Note("Ins Feld klicken und neue Kombination drücken (Entf = keine)."))),
            Section("💾  Speichern",
                ("Ordner", Row(folder, browse)), ("", postCompress),
                ("", Note("Rechnet nur, wenn kein Spiel läuft. Lange Clips brauchen Puffer-Platz (5 Min ≈ 750 MB)."))),
            Section("✨  Anzeige & System",
                ("", overlay), ("Position", corner), ("", toast), ("Position", toastCorner), ("", sound), ("", autostart)));

        var body = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0) };
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);
        root.Controls.Add(body, 0, 1);

        // ---------- Fußzeile ----------
        var save = new Button { Text = "Speichern", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 2, 10, 2) };
        save.Click += (_, _) => TrySave();
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 12) };
        footer.Controls.Add(cancel);
        footer.Controls.Add(save);
        root.Controls.Add(footer, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(root);
        LoadValues(current);

        refresh.Tick += (_, _) => UpdateStatus();
        refresh.Start();
        UpdateStatus();
        _ = LoadCodecInfoAsync();
    }

    async Task LoadCodecInfoAsync()
    {
        codecs = await CodecSupport.CheckAsync();
        if (!IsDisposed) UpdateCodecHint();
    }

    void UpdateCodecHint()
    {
        // Bei "Automatisch" zählt, was dieser PC wirklich aufnimmt; sonst das gewählte Format.
        var enc = codec.SelectedIndex switch { 1 => "hevc", 2 => "h264", _ => currentEncoder() };
        missingCodec = CodecSupport.Missing(codecs, enc, postCompress.Checked);
        if (codecs is null || enc is null)
        {
            codecHint.Text = "";
            return;
        }
        if (missingCodec is null)
        {
            codecHint.Text = "✓ Clips lassen sich mit der Windows-Medienwiedergabe abspielen.";
            codecHint.LinkArea = new LinkArea(0, 0);
            codecHint.ForeColor = Color.FromArgb(46, 160, 90);
            return;
        }
        var price = missingCodec == "AV1" ? "kostenlos" : "0,99 €";
        var link = $"{missingCodec}-Erweiterung holen ({price})";
        codecHint.Text = $"⚠ Der Windows-Medienwiedergabe fehlt die {missingCodec}-Erweiterung. Browser, Discord und VLC spielen die Clips trotzdem. {link}";
        codecHint.LinkArea = new LinkArea(codecHint.Text.Length - link.Length, link.Length);
        codecHint.ForeColor = Brand.Orange;
    }

    void LoadValues(AppSettings s)
    {
        quality.SelectedIndex = s.Height >= 1080 ? 1 : 0;
        fps.SelectedIndex = s.Fps >= 60 ? 1 : 0;
        codec.SelectedIndex = s.Codec switch { "hevc" => 1, "h264" => 2, _ => 0 };
        int li = Array.IndexOf(AppSettings.ClipLengths, s.ClipSeconds);
        length.SelectedIndex = li >= 0 ? li : 1;
        FillDevices(outputDevice, NAudio.CoreAudioApi.DataFlow.Render, s.AudioOutput);
        FillDevices(micDevice, NAudio.CoreAudioApi.DataFlow.Capture, s.Microphone);
        clipKey.Value = s.ClipHotkey;
        toggleKey.Value = s.ToggleHotkey;
        folder.Text = s.OutputFolder;
        postCompress.Checked = s.PostCompress;
        overlay.Checked = s.ShowOverlay;
        corner.SelectedIndex = (int)s.OverlayCorner;
        toast.Checked = s.ShowToast;
        toastCorner.SelectedIndex = (int)s.ToastCorner;
        sound.Checked = s.ClipSound;
        autostart.Checked = s.Autostart;
    }

    void UpdateStatus()
    {
        var (enabled, text, color) = status();
        statusLabel.Text = "● " + text;
        statusLabel.ForeColor = color;
        toggleButton.Text = enabled ? "⏸  Pausieren" : "▶  Aktivieren";
    }

    void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = folder.Text, UseDescriptionForTitle = true, Description = "Ordner für Clips" };
        if (dlg.ShowDialog(this) == DialogResult.OK) folder.Text = dlg.SelectedPath;
    }

    void TrySave()
    {
        var path = folder.Text.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            MessageBox.Show(this, "Bitte einen vollständigen Ordnerpfad angeben (z. B. D:\\Clips).", "Clipsy");
            return;
        }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Ordner kann nicht angelegt werden:\n{ex.Message}", "Clipsy");
            return;
        }
        if (!string.IsNullOrEmpty(toggleKey.Value) &&
            string.Equals(clipKey.Value, toggleKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Clip-Taste und An/Aus-Taste müssen unterschiedlich sein.", "Clipsy");
            return;
        }

        var s = original.Clone();
        s.Height = quality.SelectedIndex == 0 ? 720 : 1080;
        s.Fps = fps.SelectedIndex == 0 ? 30 : 60;
        s.Codec = codec.SelectedIndex switch { 1 => "hevc", 2 => "h264", _ => "auto" };
        s.ClipSeconds = AppSettings.ClipLengths[Math.Max(0, length.SelectedIndex)];
        s.AudioOutput = (outputDevice.SelectedItem as DeviceItem)?.Value ?? AudioDevices.Auto;
        s.Microphone = (micDevice.SelectedItem as DeviceItem)?.Value ?? AudioDevices.Off;
        s.ClipHotkey = clipKey.Value;
        s.ToggleHotkey = toggleKey.Value;
        s.OutputFolder = path;
        s.PostCompress = postCompress.Checked;
        s.ShowOverlay = overlay.Checked;
        s.OverlayCorner = (OverlayCorner)Math.Max(0, corner.SelectedIndex);
        s.ShowToast = toast.Checked;
        s.ToastCorner = (ToastCorner)Math.Max(0, toastCorner.SelectedIndex);
        s.ClipSound = sound.Checked;
        s.Autostart = autostart.Checked;
        Saved?.Invoke(s.Normalize());
        Close();
    }

    // ---------- Bausteine ----------

    sealed record DeviceItem(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    static void FillDevices(ComboBox combo, NAudio.CoreAudioApi.DataFlow flow, string current)
    {
        var devices = AudioDevices.List(flow);
        var autoId = AudioDevices.PickAuto(devices, AudioDevices.DefaultId(flow));
        var autoName = devices.FirstOrDefault(d => d.Id == autoId)?.Name;

        var items = new List<DeviceItem>
        {
            new(AudioDevices.Off, "Aus"),
            new(AudioDevices.Auto, "Automatisch" + (autoName is null ? "" : $" → {autoName}")),
            new(AudioDevices.Default, "Windows-Standard"),
        };
        items.AddRange(devices.Select(d => new DeviceItem(d.Id, d.Name + (d.IsUsb ? "  [USB]" : ""))));
        if (!items.Any(i => i.Value == current))
            items.Add(new DeviceItem(current, "Gespeichertes Gerät (nicht angeschlossen)"));

        combo.DropDownWidth = 480;
        combo.Items.Clear();
        combo.Items.AddRange(items.ToArray<object>());
        combo.SelectedIndex = items.FindIndex(i => i.Value == current);
    }

    static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, DropDownWidth = 420 };
        c.Items.AddRange(items);
        return c;
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

    static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(380, 0),
        ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6),
    };

    static Button HeaderButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
        BackColor = Color.FromArgb(60, 30, 95), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(8, 8, 0, 8),
        FlatAppearance = { BorderColor = Color.FromArgb(110, 70, 160) },
    };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(controls);
        return p;
    }

    static FlowLayoutPanel Column(params Control[] controls)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(4, 0, 4, 0) };
        p.Controls.AddRange(controls);
        return p;
    }

    static GroupBox Section(string title, params (string label, Control control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 4, 4, 2) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            int r = t.RowCount++;
            if (label.Length == 0)
            {
                t.Controls.Add(control, 0, r);
                t.SetColumnSpan(control, 2);
            }
            else
            {
                t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 0) }, 0, r);
                t.Controls.Add(control, 1, r);
            }
        }
        var g = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0, 0, 0, 10), Font = new Font("Segoe UI Semibold", 9.5f) };
        foreach (Control c in t.Controls) if (c is not GroupBox) c.Font = new Font("Segoe UI", 9.5f);
        g.Controls.Add(t);
        return g;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Textfeld, das die nächste gedrückte Tastenkombination übernimmt.</summary>
internal sealed class HotkeyBox : TextBox
{
    string value = "";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowNone { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => value;
        set { this.value = value ?? ""; Text = this.value.Length == 0 ? "(keine)" : this.value; }
    }

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == 0 && key is Keys.Delete or Keys.Back or Keys.Escape)
        {
            if (AllowNone) Value = "";
            return true;
        }
        if (Hotkey.FromKeyData(keyData) is { } hk) Value = hk.ToString();
        return true;
    }
}
