using System.Windows;
using System.Windows.Media;

namespace ResetExplode;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }

    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Bg", "#000000", "#FFFFFF"),
        ("Panel", "#080808", "#F2F2F2"),
        ("Card", "#0E0E0E", "#FFFFFF"),
        ("Hover", "#181818", "#E4E4E4"),
        ("Line", "#1F1F1F", "#CFCFCF"),
        ("LineStrong", "#3A3A3A", "#8A8A8A"),
        ("Fg", "#F5F5F5", "#111111"),
        ("Muted", "#8C8C8C", "#3F3F3F"),
        ("Faint", "#4A4A4A", "#5C5C5C"),
        ("Ink", "#000000", "#FFFFFF"),
        ("ThumbBrush", "#2E2E2E", "#CFCFCF"),
        ("ThumbHover", "#6A6A6A", "#A5A5A5"),
        ("ThumbDrag", "#9A9A9A", "#7A7A7A"),
        (SystemColors.ControlBrushKey, "#000000", "#FFFFFF"),
        (SystemColors.HighlightBrushKey, "#181818", "#E4E4E4"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#181818", "#E4E4E4"),
    };

    public static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        foreach (var (key, dark, lightHex) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? lightHex : dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush; // o XAML usa DynamicResource: trocar o pincel atualiza todos os controles na hora
        }
    }
}
