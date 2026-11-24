namespace Clipsy;

/// <summary>
/// Clipsy-Logo mit Status-Punkt in einer Bildschirmecke (grün = an, rot = aus, orange = startet/Fehler).
/// Nie in Aufnahmen sichtbar, klick-durchlässig.
/// </summary>
internal sealed class StatusOverlay : AlphaWindow
{
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 700 };
    readonly System.Windows.Forms.Timer topmostTimer = new() { Interval = 3000 };
    Color color = Brand.Orange;
    Color? flash;
    OverlayCorner corner = OverlayCorner.TopLeft;

    public StatusOverlay()
    {
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flash = null; Render(); };
        topmostTimer.Tick += (_, _) => { if (Visible) KeepOnTop(); };
        topmostTimer.Start();
    }

    int SizePx => (int)Math.Round(28 * DeviceDpi / 96.0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Render();
    }

    public void SetColor(Color c)
    {
        if (c == color) return;
        color = c;
        Render();
    }

    public void Flash(Color c)
    {
        flash = c;
        Render();
        flashTimer.Stop();
        flashTimer.Start();
    }

    public void SetCorner(OverlayCorner c)
    {
        corner = c;
        Render();
    }

    void Render()
    {
        if (!IsHandleCreated) return;
        int s = SizePx;
        var b = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        int m = (int)Math.Round(8 * DeviceDpi / 96.0);
        var pos = corner switch
        {
            OverlayCorner.TopRight => new Point(b.Right - s - m, b.Top + m),
            OverlayCorner.BottomLeft => new Point(b.Left + m, b.Bottom - s - m),
            OverlayCorner.BottomRight => new Point(b.Right - s - m, b.Bottom - s - m),
            _ => new Point(b.Left + m, b.Top + m),
        };
        using var bmp = Brand.RenderStatus(s, flash ?? color, badge: 0.34f); // kleiner → Fisch-Auge bleibt sichtbar
        Present(bmp, pos, 235);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { flashTimer.Dispose(); topmostTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
