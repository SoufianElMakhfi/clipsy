namespace Clipsy;

/// <summary>Das "Zahnrad": alle Einstellungen + Status, An/Aus und Ordner öffnen.</summary>
internal sealed class SettingsForm : Form
{
    public event Action<AppSettings>? Saved;

    readonly AppSettings original;
    readonly Func<(bool enabled, string text, Color color)> status;
    readonly Label statusLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly Button toggleButton = new() { AutoSize = true };
    readonly ComboBox quality = Combo("720p", "1080p (Standard)");
    readonly ComboBox fps = Combo("30 FPS", "60 FPS");
    readonly ComboBox length = Combo(AppSettings.ClipLengths.Select(s => $"{s} Sekunden").ToArray());
    readonly HotkeyBox clipKey = new() { Width = 200 };
    readonly HotkeyBox toggleKey = new() { Width = 200, AllowNone = true };
    readonly TextBox folder = new() { Width = 260 };
    readonly CheckBox audio = new() { Text = "Spiel-Sound aufnehmen", AutoSize = true };
    readonly CheckBox overlay = new() { Text = "Status-Punkt anzeigen (grün = an, rot = aus)", AutoSize = true };
    readonly ComboBox corner = Combo("Oben links", "Oben rechts", "Unten links", "Unten rechts");
    readonly CheckBox sound = new() { Text = "Ton beim Speichern eines Clips", AutoSize = true };
    readonly CheckBox autostart = new() { Text = "Mit Windows starten (empfohlen)", AutoSize = true };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };

    public SettingsForm(AppSettings current, string encoderInfo,
                        Func<(bool enabled, string text, Color color)> status, Action toggle, Action openFolder)
    {
        original = current;
        this.status = status;

        Text = "Clipsy – Einstellungen";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Status-Zeile
        var openButton = new Button { Text = "📂 Clip-Ordner öffnen", AutoSize = true };
        openButton.Click += (_, _) => openFolder();
        toggleButton.Click += (_, _) => { toggle(); UpdateStatus(); };
        var statusRow = Row(statusLabel, toggleButton, openButton);
        root.Controls.Add(statusRow, 0, root.RowCount++);
        root.SetColumnSpan(statusRow, 2);

        var browse = new Button { Text = "…", Width = 32 };
        browse.Click += (_, _) => Browse();

        AddRow(root, "Qualität:", quality);
        AddRow(root, "Bildrate:", fps);
        AddRow(root, "Clip-Länge:", length);
        AddRow(root, "Clip-Taste:", clipKey);
        AddRow(root, "An/Aus-Taste:", toggleKey);
        AddRow(root, "Speicherort:", Row(folder, browse));
        AddRow(root, "Punkt-Position:", corner);
        foreach (var cb in new[] { audio, overlay, sound, autostart }) AddRow(root, "", cb);

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(460, 0),
            Text = "Taste ändern: ins Feld klicken und neue Kombination drücken (Entf = keine).\n" +
                   $"Encoder: {encoderInfo}. Nach jedem PC-Neustart ist das Clippen automatisch wieder an.",
        };
        root.Controls.Add(hint, 0, root.RowCount++);
        root.SetColumnSpan(hint, 2);

        var save = new Button { Text = "Speichern", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => TrySave();
        var buttons = Row(save, cancel);
        buttons.Anchor = AnchorStyles.Right;
        root.Controls.Add(buttons, 0, root.RowCount++);
        root.SetColumnSpan(buttons, 2);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(root);
        LoadValues(current);

        refresh.Tick += (_, _) => UpdateStatus();
        refresh.Start();
        UpdateStatus();
    }

    void LoadValues(AppSettings s)
    {
        quality.SelectedIndex = s.Height >= 1080 ? 1 : 0;
        fps.SelectedIndex = s.Fps >= 60 ? 1 : 0;
        int li = Array.IndexOf(AppSettings.ClipLengths, s.ClipSeconds);
        length.SelectedIndex = li >= 0 ? li : 1;
        clipKey.Value = s.ClipHotkey;
        toggleKey.Value = s.ToggleHotkey;
        folder.Text = s.OutputFolder;
        corner.SelectedIndex = (int)s.OverlayCorner;
        audio.Checked = s.RecordAudio;
        overlay.Checked = s.ShowOverlay;
        sound.Checked = s.ClipSound;
        autostart.Checked = s.Autostart;
    }

    void UpdateStatus()
    {
        var (enabled, text, color) = status();
        statusLabel.Text = "● " + text;
        statusLabel.ForeColor = color;
        toggleButton.Text = enabled ? "Deaktivieren" : "Aktivieren";
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
        s.ClipSeconds = AppSettings.ClipLengths[Math.Max(0, length.SelectedIndex)];
        s.ClipHotkey = clipKey.Value;
        s.ToggleHotkey = toggleKey.Value;
        s.OutputFolder = path;
        s.OverlayCorner = (OverlayCorner)Math.Max(0, corner.SelectedIndex);
        s.RecordAudio = audio.Checked;
        s.ShowOverlay = overlay.Checked;
        s.ClipSound = sound.Checked;
        s.Autostart = autostart.Checked;
        Saved?.Invoke(s.Normalize());
        Close();
    }

    static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items);
        return c;
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        p.Controls.AddRange(controls);
        return p;
    }

    static void AddRow(TableLayoutPanel t, string label, Control control)
    {
        int row = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 0) }, 0, row);
        t.Controls.Add(control, 1, row);
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
        BackColor = SystemColors.Window;
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
