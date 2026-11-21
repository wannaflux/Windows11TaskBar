using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Win11Taskbar.Core;

public readonly record struct TaskbarMaterial(
    Color Tint,
    bool Floating,
    double SideInset,
    double VerticalInset,
    double Radius,
    bool Frosted,
    bool Light,
    bool NativeTintEnabled)
{
    public static TaskbarMaterial Resolve(Settings settings)
    {
        bool light = IsLightTaskbar();
        bool floating = settings.TaskbarStyle == "Floating";
        bool nativeTint = settings.ExperimentalEffects && !SystemParameters.HighContrast;
        var profile = settings.TaskbarStyle switch
        {
            "Floating"    => ("#EBF6F3", "#1D2B2C", 1.00, true),
            "Transparent" => ("#EFF6FA", "#18222B", 0.72, false),
            "Acrylic"     => ("#E9F2F8", "#263343", 0.88, true),
            "Solid"       => ("#F0F4F7", "#212831", 1.00, false),
            _             => ("#F1F5F7", "#242932", 1.00, true)
        };

        string hex = light ? profile.Item1 : profile.Item2;
        bool automatic = settings.FollowSystemTaskbarTheme;
        if (!automatic && settings.BackgroundColor is { Length: 7 } custom && custom[0] == '#')
            hex = custom;

        Color tint;
        try { tint = (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { tint = (Color)ColorConverter.ConvertFromString(light ? profile.Item1 : profile.Item2); }

        double opacity = settings.Opacity;
        if (settings.TaskbarStyle != "Solid")
        {
            // Explorer draws the original clock and tray foreground. Keep automatic glass readable
            // even when the desktop behind it is the opposite brightness.
            double minimum = automatic ? (light ? 0.56 : 0.61) : 0;
            opacity = minimum + opacity * (profile.Item3 - minimum);
        }
        else
        {
            opacity = 1;
        }

        tint.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        double sideInset = floating ? settings.TaskbarMargin : 0;
        double radius = floating ? settings.CornerRadius : Math.Min(settings.CornerRadius, 6);
        return new TaskbarMaterial(tint, floating, sideInset, Math.Min(sideInset, 2), radius,
            nativeTint && profile.Item4 && tint.A > 0, light, nativeTint);
    }

    public uint NativeTint => ((uint)Tint.A << 24) | ((uint)Tint.B << 16) | ((uint)Tint.G << 8) | Tint.R;

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
