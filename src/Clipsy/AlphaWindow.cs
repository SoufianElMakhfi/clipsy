using System.Runtime.InteropServices;

namespace Clipsy;

/// <summary>
/// Randloses Fenster mit echter Pro-Pixel-Transparenz (weiche Kanten, Schatten, Ein-/Ausblenden).
/// Klick-durchlässig, stiehlt nie den Fokus und ist per WDA_EXCLUDEFROMCAPTURE in keiner Aufnahme sichtbar.
/// </summary>
internal abstract class AlphaWindow : Form
{
    protected AlphaWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT (klick-durch) | TOOLWINDOW | NOACTIVATE | TOPMOST
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Nur für Screenshots/Tests abschaltbar; normal immer aus jeder Aufnahme ausgeblendet.
        if (Environment.GetEnvironmentVariable("CLIPSY_SHOW_IN_CAPTURE") != "1")
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Bild (32 bpp ARGB) an Bildschirmposition anzeigen; alpha = Gesamt-Deckkraft 0–255.</summary>
    protected void Present(Bitmap bmp, Point screenPos, byte alpha = 255)
    {
        if (!IsHandleCreated) return;
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBmp);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT();
            var dst = new Native.POINT { X = screenPos.X, Y = screenPos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected void KeepOnTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst, ref SIZE psize,
                                           IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}
