using System;
using Avalonia;
using Avalonia.Media;

namespace MortisFlash.Controls;

/// <summary>
/// Mortis family theme (MortisClouds, Memento): dark monochrome, white primary action,
/// subtle rules, serif headings. The accent is configurable as in Memento; the red ribbon is
/// reserved for things the application does on its own (timelapse recording).
/// </summary>
public static class MortisTheme
{
    public static readonly (string Name, Color Color)[] Accents =
    {
        ("Monochrome", Color.Parse("#FAFAFA")),
        ("Memento orange", Color.Parse("#FF6038")),
        ("Ribbon", Color.Parse("#E5533D")),
        ("Blue", Color.Parse("#5B9DFF")),
        ("Green", Color.Parse("#41C463")),
        ("Cyan", Color.Parse("#4DD0DA")),
        ("Violet", Color.Parse("#C797FF")),
        ("Yellow", Color.Parse("#E8C14A")),
    };

    // Neutrals (nekuzaky.com palette converted to hex, as in MortisClouds).
    public static readonly IBrush Bg = Brush("#0C0C0C");
    public static readonly IBrush Sidebar = Brush("#080808");
    public static readonly IBrush Elevated = Brush("#141414");
    public static readonly IBrush Hover = Brush("#1B1B1B");
    public static readonly IBrush Active = Brush("#242424");
    public static readonly IBrush Line = Brush("#61525252");
    public static readonly IBrush LineStrong = Brush("#9E525252");
    public static readonly IBrush Text = Brush("#D1D1D1");
    public static readonly IBrush TextStrong = Brush("#FAFAFA");
    public static readonly IBrush TextMute = Brush("#868686");
    public static readonly IBrush TextFaint = Brush("#6B6B6B");
    public static readonly IBrush Ribbon = Brush("#E5533D");

    public static Color AccentColor { get; private set; } = Accents[0].Color;
    public static IBrush Accent { get; private set; } = Brush("#FAFAFA");
    public static IBrush AccentSoft { get; private set; } = Brush("#1FFAFAFA");
    public static IBrush AccentFg { get; private set; } = Brush("#0C0C0C");

    public static event Action? Changed;

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    /// <summary>Applies an accent color to the whole UI (via dynamic resources).</summary>
    public static void Apply(Color accent)
    {
        AccentColor = accent;
        double lum = 0.2126 * Lin(accent.R) + 0.7152 * Lin(accent.G) + 0.0722 * Lin(accent.B);
        var fg = lum > 0.45 ? Color.Parse("#0C0C0C") : Colors.White;
        var hover = Shade(accent, lum > 0.45 ? -0.06 : 0.12);
        var pressed = Shade(accent, -0.14);
        var soft = Color.FromArgb(0x24, accent.R, accent.G, accent.B);

        Accent = new SolidColorBrush(accent).ToImmutable();
        AccentSoft = new SolidColorBrush(soft).ToImmutable();
        AccentFg = new SolidColorBrush(fg).ToImmutable();

        var res = Application.Current?.Resources;
        if (res != null)
        {
            res["AccentColor"] = accent;
            res["Accent"] = Accent;
            res["AccentHover"] = new SolidColorBrush(hover).ToImmutable();
            res["AccentPressed"] = new SolidColorBrush(pressed).ToImmutable();
            res["AccentSoft"] = AccentSoft;
            res["AccentFg"] = AccentFg;
            res["SystemAccentColor"] = accent;
            res["SystemAccentColorLight1"] = Shade(accent, 0.1);
            res["SystemAccentColorLight2"] = Shade(accent, 0.2);
            res["SystemAccentColorDark1"] = pressed;
            res["SliderTrackValueFill"] = Accent;
            res["SliderTrackValueFillPointerOver"] = Accent;
            res["SliderTrackValueFillPressed"] = Accent;
            res["SliderThumbBackground"] = Accent;
            res["SliderThumbBackgroundPointerOver"] = res["AccentHover"];
            res["SliderThumbBackgroundPressed"] = res["AccentPressed"];
            res["TextControlBorderBrushFocused"] = Accent;
            res["ToggleSwitchFillOn"] = Accent;
            res["CheckBoxCheckBackgroundFillChecked"] = Accent;
            res["CheckBoxCheckBackgroundStrokeChecked"] = Accent;
            res["CheckBoxCheckGlyphForegroundChecked"] = AccentFg;
        }
        Changed?.Invoke();
    }

    static double Lin(byte c)
    {
        double v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    static Color Shade(Color c, double f)
    {
        if (f >= 0)
            return Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
        double k = 1 + f;
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }
}
