using System.Drawing.Drawing2D;

namespace Clipsy;

/// <summary>Farben, Logo und daraus abgeleitete Status-Icons.</summary>
internal static class Brand
{
    public static readonly Color Navy = Color.FromArgb(30, 8, 52);       // Logo-Hintergrund
    public static readonly Color Cyan = Color.FromArgb(0, 245, 255);     // Logo-Fisch
    public static readonly Color Pink = Color.FromArgb(255, 61, 110);    // Aufnahme-Punkt
    public static readonly Color Green = Color.FromArgb(46, 213, 115);
    public static readonly Color Red = Color.FromArgb(255, 71, 87);
    public static readonly Color Orange = Color.FromArgb(255, 168, 1);
    public static readonly Color Card = Color.FromArgb(22, 14, 38);

    static Bitmap? logo;
    static Icon? appIcon;

    public static Bitmap Logo => logo ??= LoadLogo();

    public static Icon AppIcon => appIcon ??= LoadIcon();

    static Bitmap LoadLogo()
    {
        using var s = typeof(Brand).Assembly.GetManifestResourceStream("clipsy.png");
        if (s != null) return new Bitmap(s);
        var b = new Bitmap(64, 64); // Notfall: einfacher Kreis
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var br = new SolidBrush(Cyan);
        g.FillEllipse(br, 4, 4, 56, 56);
        return b;
    }

    static Icon LoadIcon()
    {
        using var s = typeof(Brand).Assembly.GetManifestResourceStream("clipsy.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    /// <summary>Logo mit Status-Punkt (grün/rot/orange) unten rechts – für Tray und Bildschirm-Anzeige.</summary>
    public static Bitmap RenderStatus(int size, Color status)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(Logo, 0, 0, size, size);

        float d = size * 0.46f, x = size - d, y = size - d;
        float outline = Math.Max(1.5f, size / 14f);
        using var dark = new SolidBrush(Color.FromArgb(255, 12, 4, 22));
        g.FillEllipse(dark, x - outline, y - outline, d + outline, d + outline);
        using var fill = new SolidBrush(status);
        g.FillEllipse(fill, x, y, d - outline, d - outline);
        return bmp;
    }

    public static Icon StatusIcon(Color status)
    {
        using var bmp = RenderStatus(32, status);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
