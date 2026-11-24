using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Clipsy;

public enum ToastCorner { TopRight, BottomRight }

/// <summary>
/// Kurze Meldung, die von rechts hereingleitet (oben rechts oder unten rechts über der Taskleiste),
/// ca. 2,5 s stehen bleibt und wieder hinausgleitet. Nie in Aufnahmen sichtbar.
/// </summary>
internal sealed class ClipToast : AlphaWindow
{
    const int InMs = 220, HoldMs = 2600, OutMs = 240, SlidePx = 70;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    readonly Stopwatch clock = new();
    Bitmap? card;
    Point target;

    public ClipToast() => anim.Tick += (_, _) => Step();

    double Scale => DeviceDpi / 96.0;

    public void ShowMessage(string title, string subtitle, Color accent, ToastCorner corner)
    {
        card?.Dispose();
        card = RenderCard(title, subtitle, accent);
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea; // ohne Taskleiste
        int m = (int)(16 * Scale);
        target = corner == ToastCorner.BottomRight
            ? new Point(wa.Right - card.Width - m, wa.Bottom - card.Height - m)
            : new Point(wa.Right - card.Width - m, wa.Top + m);

        if (!Visible) Show();
        KeepOnTop();
        clock.Restart();
        anim.Start();
        Step();
    }

    void Step()
    {
        if (card is null) return;
        double t = clock.Elapsed.TotalMilliseconds;
        double slide, alpha;
        if (t < InMs)
        {
            double p = 1 - Math.Pow(1 - t / InMs, 3); // ease-out
            slide = (1 - p) * SlidePx; alpha = p;
        }
        else if (t < InMs + HoldMs) { slide = 0; alpha = 1; }
        else if (t < InMs + HoldMs + OutMs)
        {
            double p = Math.Pow((t - InMs - HoldMs) / OutMs, 2); // ease-in
            slide = p * SlidePx; alpha = 1 - p;
        }
        else
        {
            anim.Stop();
            Hide();
            return;
        }
        Present(card, new Point(target.X + (int)(slide * Scale), target.Y), (byte)Math.Clamp(alpha * 255, 0, 255));
    }

    Bitmap RenderCard(string title, string subtitle, Color accent)
    {
        double s = Scale;
        int w = (int)(340 * s), h = (int)(78 * s), shadow = (int)(10 * s);
        var bmp = new Bitmap(w + shadow * 2, h + shadow * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType geht auf Transparenz nicht

        // weicher Schatten
        for (int i = shadow; i > 0; i -= Math.Max(1, shadow / 5))
        {
            using var sp = Brand.RoundedRect(new RectangleF(shadow - i * 0.6f, shadow - i * 0.3f, w + i * 1.2f, h + i * 1.2f), (float)(14 * s) + i);
            using var sb = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            g.FillPath(sb, sp);
        }

        var r = new RectangleF(shadow, shadow, w, h);
        using (var path = Brand.RoundedRect(r, (float)(14 * s)))
        {
            using var bg = new LinearGradientBrush(r, Color.FromArgb(246, 34, 18, 58), Color.FromArgb(246, 18, 10, 32), 0f);
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(70, 255, 255, 255), (float)Math.Max(1, s));
            g.DrawPath(border, path);

            // Akzentbalken links (Statusfarbe)
            g.SetClip(path);
            using var ab = new SolidBrush(accent);
            g.FillRectangle(ab, r.X, r.Y, (float)(5 * s), r.Height);
            g.ResetClip();
        }

        int logo = (int)(46 * s);
        g.DrawImage(Brand.Logo, shadow + (int)(18 * s), shadow + (h - logo) / 2, logo, logo);

        float tx = shadow + (float)(78 * s);
        using var titleFont = new Font("Segoe UI Semibold", 12f * (float)s, FontStyle.Regular, GraphicsUnit.Point);
        using var subFont = new Font("Segoe UI", 9.5f * (float)s, FontStyle.Regular, GraphicsUnit.Point);
        using var white = new SolidBrush(Color.White);
        using var grey = new SolidBrush(Color.FromArgb(200, 205, 195, 230));
        var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        float tw = shadow + w - tx - (float)(14 * s);
        g.DrawString(title, titleFont, white, new RectangleF(tx, shadow + (float)(15 * s), tw, (float)(26 * s)), fmt);
        g.DrawString(subtitle, subFont, grey, new RectangleF(tx, shadow + (float)(42 * s), tw, (float)(22 * s)), fmt);
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { anim.Dispose(); card?.Dispose(); }
        base.Dispose(disposing);
    }
}
