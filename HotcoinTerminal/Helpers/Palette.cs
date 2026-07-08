using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HotcoinTerminal.Helpers;

/// <summary>Единая палитра приложения (совпадает с ресурсами App.xaml).</summary>
public static class Palette
{
    public static readonly Color Up = Color.FromArgb(255, 0x2D, 0xD4, 0xBF);
    public static readonly Color Down = Color.FromArgb(255, 0xF8, 0x71, 0x71);
    public static readonly Color Accent = Color.FromArgb(255, 0x8B, 0x7C, 0xF6);
    public static readonly Color TextPrimary = Color.FromArgb(255, 0xE8, 0xEE, 0xF6);
    public static readonly Color TextSecondary = Color.FromArgb(255, 0x96, 0xA2, 0xB6);
    public static readonly Color TextTertiary = Color.FromArgb(255, 0x5F, 0x6B, 0x7D);

    public static readonly SolidColorBrush UpBrush = new(Up);
    public static readonly SolidColorBrush DownBrush = new(Down);
    public static readonly SolidColorBrush AccentBrush = new(Accent);
    public static readonly SolidColorBrush TextPrimaryBrush = new(TextPrimary);
    public static readonly SolidColorBrush TextSecondaryBrush = new(TextSecondary);
    public static readonly SolidColorBrush TextTertiaryBrush = new(TextTertiary);

    public static readonly SolidColorBrush UpFaintBrush = new(Color.FromArgb(40, 0x2D, 0xD4, 0xBF));
    public static readonly SolidColorBrush DownFaintBrush = new(Color.FromArgb(40, 0xF8, 0x71, 0x71));
    public static readonly SolidColorBrush AccentFaintBrush = new(Color.FromArgb(40, 0x8B, 0x7C, 0xF6));
    public static readonly SolidColorBrush NeutralFaintBrush = new(Color.FromArgb(28, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush GridLineBrush = new(Color.FromArgb(16, 0xFF, 0xFF, 0xFF));
}
