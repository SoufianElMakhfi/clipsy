using System.Drawing.Drawing2D;

namespace Clipsy;

/// <summary>Dunkles, abgerundetes Tray-Menü im Clipsy-Look.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    static readonly Color Bg = Color.FromArgb(24, 16, 38);
    static readonly Color Hover = Color.FromArgb(40, 5, 250, 245);
    static readonly Color Line = Color.FromArgb(50, 255, 255, 255);

    public DarkMenuRenderer() : base(new Colors()) => RoundedEdges = true;

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        using var path = Brand.RoundedRect(r, 6);
        using var b = new SolidBrush(Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(242, 238, 250) : Color.FromArgb(110, 100, 130);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Line);
        e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
    }
}
