using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Clipsy;

/// <summary>
/// Modernes Einstellungsfenster (HTML/CSS in WebView2). Läuft nur, solange es offen ist –
/// beim Schließen werden die WebView2-Prozesse beendet, beim Zocken kostet es also nichts.
/// Jede Nachricht aus der Oberfläche wird hier geprüft; Navigation nach außen ist gesperrt.
/// </summary>
internal sealed class WebSettingsForm : Form
{
    public event Action<AppSettings>? Saved;

    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };
    readonly Func<string?> encoder;
    readonly Func<(bool enabled, string text, Color color)> status;
    readonly Func<string> mode;
    readonly Action toggle, openFolder;
    AppSettings current;

    public static bool IsAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    public WebSettingsForm(AppSettings settings, Func<string?> encoder, Func<(bool, string, Color)> status,
                           Func<string> mode, Action toggle, Action openFolder)
    {
        current = settings.Clone();
        this.encoder = encoder;
        this.status = status;
        this.mode = mode;
        this.toggle = toggle;
        this.openFolder = openFolder;

        Text = "Clipsy";
        Icon = Brand.AppIcon;
        BackColor = Color.FromArgb(12, 7, 22);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(920, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(web);

        statusTimer.Tick += (_, _) => PostStatus();
        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dunkle Titelleiste in Clipsy-Farbe (Windows 11; Windows 10 ignoriert die Farbe)
        int on = 1, caption = 0x00200A12; // COLORREF 0x00BBGGRR = #120A20
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Dir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
            web.DefaultBackgroundColor = BackColor;
            var core = web.CoreWebView2;
            var s = core.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false; // F5/F12/Strg+R usw. gehen an die Tasten-Felder
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("data:", StringComparison.Ordinal) && e.Uri != "about:blank") e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnMessage;
            core.NavigateToString(LoadHtml());
        }
        catch (Exception ex)
        {
            Log.Write($"Einstellungen (WebView2) nicht ladbar: {ex.Message}");
            MessageBox.Show(this, "Die Einstellungen konnten nicht geladen werden. Details im Log.", "Clipsy");
            Close();
        }
    }

    static string LoadHtml()
    {
        using var s = typeof(WebSettingsForm).Assembly.GetManifestResourceStream("settings.html")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static string LogoDataUri()
    {
        using var s = typeof(Brand).Assembly.GetManifestResourceStream("clipsy.png");
        if (s is null) return "";
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    void Post(object message)
    {
        if (web.CoreWebView2 is null || IsDisposed) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, json));
    }

    object StatusJson()
    {
        var (enabled, text, color) = status();
        var name = color == Brand.Green ? "green" : color == Brand.Red ? "red" : "orange";
        return new { enabled, text, color = name, mode = mode() };
    }

    void PostStatus() => Post(new { type = "status", status = StatusJson(), encoder = encoder() });

    static object Devices()
    {
        object List(NAudio.CoreAudioApi.DataFlow flow, out string? autoName)
        {
            var list = AudioDevices.List(flow);
            var autoId = AudioDevices.PickAuto(list, AudioDevices.DefaultId(flow));
            autoName = list.FirstOrDefault(d => d.Id == autoId)?.Name;
            return list.Select(d => new { id = d.Id, name = d.Name, usb = d.IsUsb }).ToList();
        }
        var output = List(NAudio.CoreAudioApi.DataFlow.Render, out var autoOutput);
        var input = List(NAudio.CoreAudioApi.DataFlow.Capture, out var autoInput);
        return new { output, input, autoOutput, autoInput };
    }

    async void PostInit()
    {
        var version = (Application.ProductVersion ?? "").Split('+')[0];
        Post(new
        {
            type = "init", settings = current, devices = Devices(), codecs = (object?)null,
            encoder = encoder(), status = StatusJson(), logo = LogoDataUri(), version,
        });
        statusTimer.Start();
        var c = await CodecSupport.CheckAsync();
        if (c is not null) Post(new { type = "codecs", codecs = new { av1 = c.Av1, hevc = c.Hevc } });
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonNode.Parse(e.WebMessageAsJson);
            switch ((string?)msg?["type"])
            {
                case "ready": PostInit(); break;
                case "save": HandleSave(msg!["settings"]); break;
                case "toggle": toggle(); PostStatus(); break;
                case "openFolder": openFolder(); break;
                case "browse": Browse(); break;
                case "openStore":
                    var codec = (string?)msg!["codec"];
                    if (codec is "AV1" or "HEVC") CodecSupport.OpenStore(codec);
                    break;
            }
        }
        catch (Exception ex) { Log.Write($"Einstellungen: ungültige Nachricht ({ex.Message})"); }
    }

    void HandleSave(JsonNode? node)
    {
        var s = node?.Deserialize<AppSettings>(json);
        if (s is null) return;

        string? error = null;
        if (!Path.IsPathFullyQualified(s.OutputFolder ?? "")) { error = "Ungültiger Ordner"; s.OutputFolder = current.OutputFolder; }
        else
        {
            try { Directory.CreateDirectory(s.OutputFolder); }
            catch { error = "Ordner kann nicht angelegt werden"; s.OutputFolder = current.OutputFolder; }
        }
        if (!Hotkey.TryParse(s.ClipHotkey, out _)) { error = "Ungültige Clip-Taste"; s.ClipHotkey = current.ClipHotkey; }
        if (!string.IsNullOrEmpty(s.ToggleHotkey) && !Hotkey.TryParse(s.ToggleHotkey, out _)) { error = "Ungültige An/Aus-Taste"; s.ToggleHotkey = current.ToggleHotkey; }
        if (!string.IsNullOrEmpty(s.ToggleHotkey) && string.Equals(s.ClipHotkey, s.ToggleHotkey, StringComparison.OrdinalIgnoreCase))
        { error = "Clip- und An/Aus-Taste müssen verschieden sein"; s.ToggleHotkey = current.ToggleHotkey; }

        current = s.Normalize();
        Saved?.Invoke(current.Clone());
        if (error is null) Post(new { type = "saved" });
        else Post(new { type = "error", message = error, settings = current });
    }

    void Browse()
    {
        using var dlg = new FolderBrowserDialog
        {
            SelectedPath = current.OutputFolder, UseDescriptionForTitle = true, Description = "Ordner für deine Clips",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Post(new { type = "folder", path = dlg.SelectedPath });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { statusTimer.Dispose(); web.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
