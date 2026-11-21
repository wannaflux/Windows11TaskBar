using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Win11Taskbar.App;

internal static class TrayIcon
{
    public static Icon Create()
    {
        using var image = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        DrawMark(graphics, new RectangleF(0, 0, 32, 32), Color.FromArgb(166, 155, 255));
        var handle = image.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    internal static void DrawMark(Graphics graphics, RectangleF bounds, Color accent)
    {
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 32, bounds.Height / 32);

            using var path = RoundedRectangle(new RectangleF(1, 1, 30, 30), 8);
            using var background = new LinearGradientBrush(
                new RectangleF(1, 1, 30, 30), accent, Color.FromArgb(122, 104, 230), 45);
            graphics.FillPath(background, path);

            using var tiles = new SolidBrush(Color.FromArgb(37, 28, 82));
            graphics.FillRectangle(tiles, 7, 12, 5, 6);
            graphics.FillRectangle(tiles, 13.5f, 9, 5, 9);
            graphics.FillRectangle(tiles, 20, 12, 5, 6);
            using var track = new Pen(tiles.Color, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(track, 7.75f, 22, 24.25f, 22);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
