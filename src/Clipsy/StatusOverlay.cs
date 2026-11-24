namespace Clipsy;

/// <summary>
/// Kleiner Status-Punkt (grün = an, rot = aus, orange = startet/Fehler).
/// Klick-durchlässig, stiehlt nie den Fokus und ist per WDA_EXCLUDEFROMCAPTURE
/// in keiner Aufnahme sichtbar – auch nicht in Clipsys eigenen Clips.
/// </summary>
internal sealed class StatusOverlay : Form
{
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer topmostTimer = new() { Interval = 3000 };
    Color color = Color.Orange, flashColor;
    bool flashing;
    OverlayCorner corner = OverlayCorner.TopLeft;

    int Dot => LogicalToDeviceUnits(14);

    public StatusOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(Dot + 4, Dot + 4);

        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flashing = false; Invalidate(); };
        topmostTimer.Tick += (_, _) =>
        {
            if (Visible)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
        topmostTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOOLWINDOW | TRANSPARENT (klick-durch) | NOACTIVATE | TOPMOST | LAYERED
            cp.ExStyle |= 0x80 | 0x20 | 0x08000000 | 0x8 | 0x80000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        Reposition();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var c = flashing ? flashColor : color;
        using var brush = new SolidBrush(c);
        using var pen = new Pen(Color.FromArgb(25, 25, 25), 2);
        e.Graphics.FillEllipse(brush, 2, 2, Dot, Dot);
        e.Graphics.DrawEllipse(pen, 2, 2, Dot, Dot);
    }

    public void SetColor(Color c)
    {
        if (c == color) return;
        color = c;
        Invalidate();
    }

    public void Flash(Color c)
    {
        flashColor = c;
        flashing = true;
        Invalidate();
        flashTimer.Stop();
        flashTimer.Start();
    }

    public void SetCorner(OverlayCorner c)
    {
        corner = c;
        Reposition();
    }

    public void Reposition()
    {
        var b = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        int m = LogicalToDeviceUnits(6);
        Size = new Size(Dot + 4, Dot + 4);
        Location = corner switch
        {
            OverlayCorner.TopRight => new Point(b.Right - Width - m, b.Top + m),
            OverlayCorner.BottomLeft => new Point(b.Left + m, b.Bottom - Height - m),
            OverlayCorner.BottomRight => new Point(b.Right - Width - m, b.Bottom - Height - m),
            _ => new Point(b.Left + m, b.Top + m),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { flashTimer.Dispose(); topmostTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
