using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace Win11Taskbar.App;

internal static class StudioVisuals
{
    internal sealed record Preset(string Key, string Name, string Description, string Color);

    internal static IReadOnlyList<Preset> Presets { get; } =
    [
        new("Windows 11", "Windows 11", "Во всю ширину", "#5E7FCB"),
        new("Floating", "Парящая", "Со скруглением", "#8E78D4"),
        new("Transparent", "Прозрачная", "Минимум цвета", "#6BAAC2"),
        new("Acrylic", "Акрил", "Матовое стекло", "#4788A7"),
        new("Solid", "Сплошная", "Плотный фон", "#697795")
    ];

    static readonly Dictionary<string, string> Glyphs = new()
    {
        ["General"] = "M8.4,2.5 H15.6 L16.2,6.1 L19.5,7.8 L22.7,6.5 L24.5,10 L21.8,12.5 V15.6 L24.5,18.1 L22.7,21.5 L19.5,20.2 L16.2,22 L15.6,25.5 H8.4 L7.8,22 L4.5,20.2 L1.3,21.5 L-.5,18.1 L2.2,15.6 V12.5 L-.5,10 L1.3,6.5 L4.5,7.8 L7.8,6.1 Z M16.4,14 A4.4,4.4 0 1 0 7.6,14 A4.4,4.4 0 1 0 16.4,14",
        ["Appearance"] = "M12,1.5 C14,7 16,9 22.5,12 C16,15 14,17 12,22.5 C10,17 8,15 1.5,12 C8,9 10,7 12,1.5 Z M19.3,2.4 V7.6 M16.7,5 H21.9",
        ["Taskbar"] = "M3,4 H21 A2,2 0 0 1 23,6 V18 A2,2 0 0 1 21,20 H3 A2,2 0 0 1 1,18 V6 A2,2 0 0 1 3,4 Z M2,15 H22 M8,17.5 H10 M12,17.5 H14 M16,17.5 H18",
        ["Animation"] = "M2,16 H6 L9,5 L14,20 L17,9 H22",
        ["Advanced"] = "M2,6 H8 M12,6 H22 M2,12 H15 M19,12 H22 M2,18 H5 M9,18 H22 M12,6 A2,2 0 1 0 8,6 A2,2 0 1 0 12,6 M19,12 A2,2 0 1 0 15,12 A2,2 0 1 0 19,12 M9,18 A2,2 0 1 0 5,18 A2,2 0 1 0 9,18",
        ["About"] = "M22,12 A10,10 0 1 0 2,12 A10,10 0 1 0 22,12 M12,11 V17 M12,7 V7.2",
        ["Search"] = "M15.5,9 A6.5,6.5 0 1 0 2.5,9 A6.5,6.5 0 1 0 15.5,9 M14,14 L21,21",
        ["View"] = "M3,5 H15 V17 H3Z M9,20 H21 V9",
        ["Network"] = "M1,6 Q8,-.5 15,6 M3.5,9 Q8,5 12.5,9 M6,12 Q8,10.3 10,12 M8,14 V14.1",
        ["Sound"] = "M1,6 H4 L8,2 V14 L4,10 H1Z M10,5 Q13,8 10,11 M12,2 Q18,8 12,14",
        ["Tray"] = "M2,9 L7,4 L12,9"
    };

    public static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    public static SolidColorBrush Brush(string value) => Brush(Parse(value));

    public static SolidColorBrush Brush(Color value)
    {
        var brush = new SolidColorBrush(value);
        brush.Freeze();
        return brush;
    }

    public static Path Glyph(string name, double size, Brush foreground)
    {
        var geometry = Geometry.Parse(Glyphs[name]);
        geometry.Freeze();
        return new Path
        {
            Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform,
            Stroke = foreground, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static Border Thumbnail(Preset preset)
    {
        var wallpaper = new LinearGradientBrush(Parse("#24243C"), Parse(preset.Color), 30);
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var color in new[] { "#75C8FF", "#A99AFF", "#FFD58C" })
            dots.Children.Add(new Border { Width = 5, Height = 5, Background = Brush(color), CornerRadius = new(1.5), Margin = new(2, 0, 2, 0) });

        bool floating = preset.Key == "Floating";
        var bar = new Border
        {
            Height = 13,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = floating ? new(8, 0, 8, 5) : new(0),
            Background = Brush(preset.Key switch { "Transparent" => "#40202436", "Acrylic" => "#A5233651", _ => "#E41B1D32" }),
            CornerRadius = new(floating ? 5 : 0),
            Child = dots
        };
        return new Border { Height = 43, CornerRadius = new(7), Background = wallpaper, Child = bar };
    }

    public static FrameworkElement LauncherIcon(string name, double size, Brush neutral)
    {
        return name switch
        {
            "Start" => StartIcon(size),
            "Browser" => AppIcon(size, "#1478E5", "#37C9CE", "M3,14 C5,6 10,5 13,10 C15,14 17,12 19,8 C18,16 14,20 8,18 C5,17 4,16 3,14Z", "#E7FAFF"),
            "Files" => AppIcon(size, "#FACD73", "#F1A950", "M4,8 H9 L11,10 H19 V17 H4Z", "#FFF2C1"),
            "Chat" => AppIcon(size, "#6D59DB", "#B283F9", "M4,5 H19 V15 H10 L6,18 V15 H4Z", "#F8EFFF"),
            "Terminal" => AppIcon(size, "#25283E", "#404665", "M5,7 L9,11 L5,15 L6.5,15 L10.5,11 L6.5,7Z M12,14 H18 V15.5 H12Z", "#AAF2D5"),
            _ => Glyph(name, size * .78, neutral)
        };
    }

    static Grid StartIcon(double size)
    {
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
        foreach (var color in new[] { "#76D1FF", "#46BBFF", "#489FFC", "#3686F0" })
            tiles.Children.Add(new Border { Margin = new(.75), CornerRadius = new(1), Background = Brush(color) });
        var result = new Grid { Width = size * .90, Height = size * .90 };
        result.Children.Add(tiles);
        return result;
    }

    static Border AppIcon(double size, string start, string end, string geometry, string symbol)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new(size * .27),
            Background = new LinearGradientBrush(Parse(start), Parse(end), 45),
            Child = new Path
            {
                Data = Geometry.Parse(geometry), Fill = Brush(symbol), Width = size * .63, Height = size * .63,
                Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}
