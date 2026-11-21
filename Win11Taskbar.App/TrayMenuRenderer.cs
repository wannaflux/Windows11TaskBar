using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;
using Wpf = System.Windows;

namespace Win11Taskbar.App;

internal sealed record TrayPalette(Color Background, Color Border, Color Text, Color Muted,
    Color Accent, Color AccentSurface, Color Hover, Color Danger)
{
    public static TrayPalette Read()
    {
        if (WinForms.SystemInformation.HighContrast)
            return new(SystemColors.Menu, SystemColors.WindowText, SystemColors.MenuText,
                SystemColors.MenuText, SystemColors.MenuText, SystemColors.Menu,
                SystemColors.ControlLight, SystemColors.MenuText);

        return new(ReadBrush("SurfaceRaised", "#27293B"), ReadBrush("Line", "#33354A"),
            ReadBrush("Text", "#F5F4FD"), ReadBrush("Muted", "#A8A8BF"), ReadBrush("Accent", "#A69BFF"),
            ReadBrush("AccentSubtle", "#2C2947"), ReadBrush("Hover", "#2A2B3C"), ReadBrush("Danger", "#FF9AAB"));
    }

    private static Color ReadBrush(string key, string fallback)
    {
        if (Wpf.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
            return Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B);
        return ColorTranslator.FromHtml(fallback);
    }
}

internal sealed class TrayMenuRenderer : WinForms.ToolStripRenderer, IDisposable
{
    private int dpi = 96;
    private Font regular = MenuFont(13, FontStyle.Regular);
    private Font small = MenuFont(11, FontStyle.Regular);
    private Font bold = MenuFont(13, FontStyle.Bold);
    private Font title = MenuFont(15, FontStyle.Bold);
    public TrayPalette Palette { get; private set; } = TrayPalette.Read();

    public int Scale(int pixels) => (pixels * dpi + 48) / 96;

    public void SetTheme(TrayPalette palette, int monitorDpi)
    {
        Palette = palette;
        if (dpi == monitorDpi)
            return;
        dpi = monitorDpi;
        Dispose();
        regular = MenuFont(Scale(13), FontStyle.Regular);
        small = MenuFont(Scale(11), FontStyle.Regular);
        bold = MenuFont(Scale(13), FontStyle.Bold);
        title = MenuFont(Scale(15), FontStyle.Bold);
    }

    private static Font MenuFont(int size, FontStyle style) =>
        new(WinForms.Control.DefaultFont.FontFamily, size, style, GraphicsUnit.Pixel);

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(Palette.Background);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width < 3 || bounds.Height < 3)
            return;

        var mode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = TrayIcon.RoundedRectangle(new RectangleF(0.5f, 0.5f,
            bounds.Width - 1f, bounds.Height - 1f), Scale(13));
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawPath(pen, outline);
        e.Graphics.SmoothingMode = mode;
    }

    protected override void OnRenderLabelBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not TrayHeading header)
            return;
        int x = Scale(10);
        int mark = Scale(34);
        TrayIcon.DrawMark(e.Graphics, new RectangleF(x, Scale(11), mark, mark), Palette.Accent);
        int labelStart = Scale(54);
        int labelWidth = Math.Max(0, header.Width - labelStart - x);
        DrawText(e.Graphics, "Win11Taskbar", title, Palette.Text, new Rectangle(labelStart,
            Scale(9), labelWidth, Scale(21)));

        using var signal = new SolidBrush(header.Active ? Palette.Accent : Palette.Muted);
        e.Graphics.FillEllipse(signal, labelStart, Scale(37), Scale(5), Scale(5));
        DrawText(e.Graphics, header.Subtitle, small, Palette.Muted, new Rectangle(labelStart + Scale(12),
            Scale(32), Math.Max(0, labelWidth - Scale(12)), Scale(15)));
        using var rule = new Pen(Palette.Border);
        e.Graphics.DrawLine(rule, Scale(6), e.Item.Height - 1, e.Item.Width - Scale(6), e.Item.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not TrayRow row || !(row.Selected || row.Pressed || row.Primary))
            return;

        var mode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = TrayIcon.RoundedRectangle(new RectangleF(2, 1,
            Math.Max(1, row.Width - 4), Math.Max(1, row.Height - 2)), Scale(7));
        var color = row.Selected || row.Pressed ? Palette.Hover : Palette.AccentSurface;
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, outline);
        if (row.Primary && !row.Selected)
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawPath(pen, outline);
        }
        e.Graphics.SmoothingMode = mode;
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not TrayRow row)
            return;

        int middle = row.Height / 2;
        int icon = Scale(16);
        int textStart = Scale(37);
        int textEnd = row.Width - Scale(13);
        bool submenu = row.HasDropDownItems;
        var textColor = !row.Enabled ? Palette.Muted : row.Destructive ? Palette.Danger : Palette.Text;
        var iconColor = row.Destructive ? Palette.Danger : row.Primary || row.Checked ? Palette.Accent : Palette.Muted;

        DrawGlyph(e.Graphics, row.Glyph, iconColor, Scale(12), middle - icon / 2, icon);
        if (submenu || row.Checked)
        {
            int rightIcon = textEnd - icon;
            DrawGlyph(e.Graphics, submenu ? TrayGlyph.Chevron : TrayGlyph.Check,
                submenu ? Palette.Muted : Palette.Accent, rightIcon, middle - icon / 2, icon);
            textEnd = rightIcon - Scale(7);
        }

        if (row.Detail is { Length: > 0 } detail)
        {
            var detailWidth = WinForms.TextRenderer.MeasureText(detail, small, Size.Empty,
                WinForms.TextFormatFlags.NoPadding | WinForms.TextFormatFlags.SingleLine).Width + Scale(3);
            DrawText(e.Graphics, detail, small, Palette.Muted,
                new Rectangle(textEnd - detailWidth, 0, detailWidth, row.Height));
            textEnd -= detailWidth + Scale(9);
        }

        DrawText(e.Graphics, row.Text ?? string.Empty, row.Primary ? bold : regular, textColor,
            new Rectangle(textStart, 0, Math.Max(0, textEnd - textStart), row.Height));
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        int center = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, Scale(6), center, e.Item.Width - Scale(6), center);
    }

    protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e) { }
    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e) { }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds)
    {
        WinForms.TextRenderer.DrawText(graphics, text, font, bounds, color,
            WinForms.TextFormatFlags.NoPrefix | WinForms.TextFormatFlags.NoPadding |
            WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.SingleLine |
            WinForms.TextFormatFlags.EndEllipsis | WinForms.TextFormatFlags.PreserveGraphicsClipping |
            WinForms.TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    private static void DrawGlyph(Graphics graphics, TrayGlyph glyph, Color color, int x, int y, int size)
    {
        if (glyph == TrayGlyph.None)
            return;

        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(x, y);
            graphics.ScaleTransform(size / 16f, size / 16f);
            using var pen = new Pen(color, 1.35f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };

            switch (glyph)
            {
                case TrayGlyph.Settings:
                    // Three adjustable tracks.
                    graphics.DrawLine(pen, 2, 4, 8, 4);
                    graphics.DrawLine(pen, 11, 4, 14, 4);
                    graphics.DrawLine(pen, 2, 8, 6, 8);
                    graphics.DrawLine(pen, 9, 8, 14, 8);
                    graphics.DrawLine(pen, 2, 12, 9, 12);
                    graphics.DrawLine(pen, 12, 12, 14, 12);
                    graphics.DrawLine(pen, 9.5f, 2, 9.5f, 6);
                    graphics.DrawLine(pen, 7.5f, 6, 7.5f, 10);
                    graphics.DrawLine(pen, 10.5f, 10, 10.5f, 14);
                    break;
                case TrayGlyph.Display:
                    using (var display = TrayIcon.RoundedRectangle(new RectangleF(2, 3, 12, 9), 2))
                        graphics.DrawPath(pen, display);
                    graphics.DrawLine(pen, 5, 9, 11, 9);
                    break;
                case TrayGlyph.Layers:
                    graphics.DrawPolygon(pen, new PointF[] { new(2, 6), new(8, 2.5f), new(14, 6), new(8, 9.5f) });
                    graphics.DrawLines(pen, new PointF[] { new(2, 10), new(8, 13.5f), new(14, 10) });
                    break;
                case TrayGlyph.Startup:
                    graphics.DrawArc(pen, 2, 2, 12, 12, 0, 360);
                    graphics.DrawLines(pen, new PointF[] { new(8, 4.5f), new(8, 8.5f), new(10.5f, 10) });
                    break;
                case TrayGlyph.Tools:
                    graphics.DrawLine(pen, 3.5f, 12.5f, 8, 8);
                    graphics.DrawArc(pen, 7, 2, 7, 7, 2, 265);
                    graphics.DrawLines(pen, new PointF[] { new(14.1f, 5.3f), new(11, 6), new(10.8f, 2.2f) });
                    break;
                case TrayGlyph.Restart:
                    graphics.DrawArc(pen, 2.5f, 2.5f, 11, 11, 0, 285);
                    graphics.DrawLines(pen, new PointF[] { new(10, 5.5f), new(13.5f, 5.5f), new(13.5f, 2) });
                    break;
                case TrayGlyph.Update:
                    graphics.DrawLine(pen, 8, 2, 8, 10);
                    graphics.DrawLines(pen, new PointF[] { new(4.5f, 6.5f), new(8, 10), new(11.5f, 6.5f) });
                    graphics.DrawLines(pen, new PointF[] { new(2.5f, 10.5f), new(2.5f, 13), new(13.5f, 13), new(13.5f, 10.5f) });
                    break;
                case TrayGlyph.Exit:
                    graphics.DrawLines(pen, new PointF[] { new(7, 2.5f), new(3, 2.5f), new(3, 13.5f), new(7, 13.5f) });
                    graphics.DrawLine(pen, 6, 8, 13, 8);
                    graphics.DrawLines(pen, new PointF[] { new(10, 5), new(13, 8), new(10, 11) });
                    break;
                case TrayGlyph.Check:
                    graphics.DrawLines(pen, new PointF[] { new(3.5f, 8.5f), new(6.5f, 11.5f), new(12.5f, 4.5f) });
                    break;
                case TrayGlyph.Chevron:
                    graphics.DrawLines(pen, new PointF[] { new(6, 4), new(10, 8), new(6, 12) });
                    break;
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    public void Dispose()
    {
        regular.Dispose();
        small.Dispose();
        bold.Dispose();
        title.Dispose();
    }
}
