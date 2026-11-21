using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Win11Taskbar.App;

public partial class HexColorDialog : Window
{
    private static readonly (string Hex, string Name)[] Swatches =
    [
        ("#222536", "Графит"), ("#F3F4FB", "Фарфор"), ("#ED6F79", "Коралл"),
        ("#F6AD65", "Янтарь"), ("#56CDA2", "Мята"), ("#43C8D2", "Бирюза"),
        ("#6AA7FF", "Лазурь"), ("#A69BFF", "Лаванда"), ("#DB86CB", "Роза")
    ];

    private readonly Dictionary<Color, Button> swatches = new();
    private Color candidate;
    private double hue;
    private double saturation;
    private double value;
    private bool ready;
    private bool synchronizing;

    public string? SelectedHex { get; private set; }

    public HexColorDialog(string title, Color original)
    {
        InitializeComponent();
        Title = "Выбор цвета — " + title;
        DialogHeading.Text = title;
        CurrentSample.Background = StudioVisuals.Brush(original);
        BuildSwatches();
        ready = true;
        SelectColor(original, writeHex: true);
        Loaded += (_, _) =>
        {
            HexInput.Focus();
            HexInput.SelectAll();
        };
    }

    private void BuildSwatches()
    {
        foreach (var (hex, name) in Swatches)
        {
            var color = StudioVisuals.Parse(hex);
            var button = new Button
            {
                Background = StudioVisuals.Brush(color),
                Height = 32,
                MinHeight = 0,
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                ToolTip = $"{name} · {hex}",
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M1,5 L4,8 L9,2"),
                    Stroke = StudioVisuals.Brush(ReadabilityColor(color)),
                    StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Visibility = Visibility.Hidden
                }
            };
            KeyboardNavigation.SetTabIndex(button, 3);
            AutomationProperties.SetName(button, $"{name}, {hex}");
            button.Click += (_, _) => SelectColor(color, writeHex: true);
            swatches.Add(color, button);
            SwatchGrid.Children.Add(button);
        }
    }

    internal static bool TryParseHex(string input, out Color color)
    {
        var digits = input.AsSpan().Trim();
        if (!digits.IsEmpty && digits[0] == '#')
            digits = digits[1..];
        if (digits.Length == 6 &&
            uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        color = default;
        return false;
    }

    internal static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void HexChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || synchronizing)
            return;
        if (TryParseHex(HexInput.Text, out var color))
            SelectColor(color, writeHex: false);
        else
            SetValidation(false);
    }

    private void SetValidation(bool valid)
    {
        ApplyButton.IsEnabled = valid;
        HexHint.Text = valid ? "Формат #RRGGBB" : "Введите 6 цифр или букв от A до F";
        HexHint.SetResourceReference(TextBlock.ForegroundProperty, valid ? "Muted" : "Danger");
        HexInput.SetResourceReference(Control.BorderBrushProperty, valid ? "Line" : "Danger");
    }

    private void SelectColor(Color color, bool writeHex)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        value = Math.Max(r, Math.Max(g, b));
        double delta = value - Math.Min(r, Math.Min(g, b));
        saturation = value == 0 ? 0 : delta / value;
        if (delta > 0)
        {
            double sector = value == r ? (g - b) / delta :
                value == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
            hue = (sector * 60 + 360) % 360;
        }

        UpdateSelection(color, writeHex);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double sector = hue % 360 / 60;
        double chroma = value * saturation;
        double secondary = chroma * (1 - Math.Abs(sector % 2 - 1));
        double offset = value - chroma;
        var (r, g, b) = ((int)sector) switch
        {
            0 => (chroma, secondary, 0d),
            1 => (secondary, chroma, 0d),
            2 => (0d, chroma, secondary),
            3 => (0d, secondary, chroma),
            4 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        byte Channel(double amount) => (byte)Math.Round((amount + offset) * 255);
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }

    private static Color ReadabilityColor(Color color) =>
        color.R * .2126 + color.G * .7152 + color.B * .0722 > 150 ? Color.FromRgb(30, 32, 49) : Colors.White;

    private void UpdateSelection(Color color, bool writeHex)
    {
        candidate = color;
        synchronizing = true;
        try
        {
            HueSlider.Value = hue;
            HueFill.Background = StudioVisuals.Brush(FromHsv(hue, 1, 1));
            var brush = StudioVisuals.Brush(color);
            ToneMarkerFill.Fill = brush;
            NewSample.Background = brush;
            if (writeHex)
                HexInput.Text = FormatHex(color);

            foreach (var (swatchColor, button) in swatches)
            {
                bool selected = swatchColor == color;
                ((UIElement)button.Content).Visibility = selected ? Visibility.Visible : Visibility.Hidden;
                button.SetResourceReference(Control.BorderBrushProperty, selected ? "Focus" : "Line");
            }
        }
        finally
        {
            synchronizing = false;
        }

        PositionMarker();
        SetValidation(true);
    }

    private void HueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || synchronizing)
            return;
        hue = e.NewValue;
        UpdateSelection(FromHsv(hue, saturation, value), writeHex: true);
    }

    private void PlaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        TonePlane.Focus();
        TonePlane.CaptureMouse();
        SelectPoint(e.GetPosition(ToneCanvas));
        e.Handled = true;
    }

    private void PlaneMouseMove(object sender, MouseEventArgs e)
    {
        if (TonePlane.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SelectPoint(e.GetPosition(ToneCanvas));
    }

    private void PlaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!TonePlane.IsMouseCaptured)
            return;
        SelectPoint(e.GetPosition(ToneCanvas));
        TonePlane.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SelectPoint(Point position)
    {
        if (ToneCanvas.ActualWidth <= 0 || ToneCanvas.ActualHeight <= 0)
            return;
        saturation = Math.Clamp(position.X / ToneCanvas.ActualWidth, 0, 1);
        value = 1 - Math.Clamp(position.Y / ToneCanvas.ActualHeight, 0, 1);
        UpdateSelection(FromHsv(hue, saturation, value), writeHex: true);
    }

    private void PlaneKeyDown(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .05 : .01;
        switch (e.Key)
        {
            case Key.Left: saturation = Math.Max(0, saturation - step); break;
            case Key.Right: saturation = Math.Min(1, saturation + step); break;
            case Key.Up: value = Math.Min(1, value + step); break;
            case Key.Down: value = Math.Max(0, value - step); break;
            default: return;
        }
        UpdateSelection(FromHsv(hue, saturation, value), writeHex: true);
        e.Handled = true;
    }

    private void PlaneSizeChanged(object sender, SizeChangedEventArgs e) => PositionMarker();

    private void PositionMarker()
    {
        if (!ready)
            return;
        Canvas.SetLeft(ToneMarker, saturation * ToneCanvas.ActualWidth - ToneMarker.Width / 2);
        Canvas.SetTop(ToneMarker, (1 - value) * ToneCanvas.ActualHeight - ToneMarker.Height / 2);
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseHex(HexInput.Text, out var color))
            return;
        SelectedHex = FormatHex(color);
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
