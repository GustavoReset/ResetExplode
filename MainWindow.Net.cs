using System.Windows;
using System.Windows.Threading;

namespace FortKnox;

/// <summary>Tema escuro/claro (botão da barra de título). Rede foi para Modules/Network/RadarView; Energia para Modules/Sentinela/EnergyView.</summary>
public partial class MainWindow
{
    // ================= Tema (escuro / claro) =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight);
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
        UpdateThemeButton();
    }

    /// <summary>Mostra o ícone do modo para onde o clique leva (sol = ir para o claro, lua = ir para o escuro). O símbolo da marca é vetorial e serve nos dois modos.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "\uE708" : "\uE706";
        BtnTheme.ToolTip = light ? "Modo escuro" : "Modo claro";
    }
}
