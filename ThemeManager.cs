using System.Windows;
using System.Windows.Media;

namespace FortKnox;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }

    // Identidade Fort Knox v3: escuro = preto Bunker + grafite com ouro em foco; claro = Branco Lingote + Névoa com ouro Bronze
    // (o ouro puro some sobre o branco, por isso no claro os realces usam o tom Bronze Lastro).
    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Bg", "#0B0C0E", "#F6F5F1"),
        ("Panel", "#111316", "#EEEDE8"),
        ("Card", "#17191D", "#FFFFFF"),
        ("Hover", "#22252B", "#E6E4DD"),
        ("Line", "#262A31", "#DCDAD3"),
        ("LineStrong", "#3A3F47", "#B9B6AD"),
        ("Fg", "#F6F5F1", "#0B0C0E"),
        ("Muted", "#9AA0A9", "#4A5058"),
        ("Faint", "#5E646D", "#7D848E"),
        ("Ink", "#0B0C0E", "#F6F5F1"),
        ("ThumbBrush", "#2E333A", "#CFCCC4"),
        ("ThumbHover", "#5A616B", "#A5A29A"),
        ("ThumbDrag", "#D4A13A", "#8C6A2A"),
        ("Accent", "#D4A13A", "#B8842A"),
        ("AccentText", "#F0C766", "#8C6A2A"),
        ("AccentSoft", "#2A2414", "#F5EAD0"),
        ("OnAccent", "#0B0C0E", "#0B0C0E"),
        ("Danger", "#D7263D", "#B81F33"),
        ("Ok", "#2EBD6B", "#1E8F4F"),
        ("Warn", "#F29D38", "#C77310"),
        (SystemColors.ControlBrushKey, "#0B0C0E", "#F6F5F1"),
        (SystemColors.HighlightBrushKey, "#22252B", "#E6E4DD"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#22252B", "#E6E4DD"),
    };

    // Degradês de fundo (janela, painel lateral e cartões): (início, meio, fim) em cada modo.
    private static readonly (string Key, string[] Dark, string[] Light)[] Gradients =
    {
        ("WindowBg", new[] { "#1D2025", "#111316", "#0B0C0E" }, new[] { "#FFFFFF", "#F6F5F1", "#ECEAE4" }),
        ("CardFill", new[] { "#1B1E23", "#181A1E", "#15171A" }, new[] { "#FFFFFF", "#FFFFFF", "#FAF9F6" }),
        ("PanelFill", new[] { "#16181C", "#131518", "#0F1013" }, new[] { "#F3F2EE", "#EFEEE9", "#EAE8E2" }),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        foreach (var (key, dark, lightHex) in Palette)
        {
            var color = C(light ? lightHex : dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush; // o XAML usa DynamicResource: trocar o pincel atualiza todos os controles na hora
        }
        foreach (var (key, dark, lightHex) in Gradients)
        {
            var c = light ? lightHex : dark;
            Brush brush = key == "WindowBg"
                ? new RadialGradientBrush
                {
                    Center = new Point(0.25, 0), GradientOrigin = new Point(0.25, 0), RadiusX = 1.1, RadiusY = 1.3,
                    GradientStops = { new GradientStop(C(c[0]), 0), new GradientStop(C(c[1]), 0.55), new GradientStop(C(c[2]), 1) },
                }
                : new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = { new GradientStop(C(c[0]), 0), new GradientStop(C(c[1]), 0.5), new GradientStop(C(c[2]), 1) },
                };
            brush.Freeze();
            res[key] = brush;
        }
    }
}
