using System.Windows;
using System.Windows.Input;
using FortKnox.UI;

namespace FortKnox;

/// <summary>
/// Configurações do app (botão de engrenagem na barra de título). Primeira seção: como o COFRE abre.
/// O interruptor do Windows Hello só fica ativo se o Windows disser que o Hello está configurado neste PC;
/// senão fica cinza e o Cofre usa a senha mestra.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DevNote.Visibility = MasterPassword.IsDevPassword ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += async (_, _) => await LoadHelloAsync();
    }

    private Core.Config.ISettings<CofreSettings>? Prefs => App.Core?.Settings.Section<CofreSettings>(CofreModule.SettingsId);

    private async Task LoadHelloAsync()
    {
        var avail = await HelloVerifier.CheckAsync();
        bool available = avail.Outcome == HelloOutcome.Verified; // CheckAsync usa "Verified" para "disponível"
        bool wanted = Prefs?.Value.UseWindowsHello ?? true;

        ChkHello.IsEnabled = available && Prefs != null;
        ChkHello.IsChecked = available && wanted;
        if (available)
        {
            HelloBadge.Status = HealthStatus.Bom;
            HelloBadge.Text = "Disponível";
            HelloText.Text = "O Windows Hello está configurado neste PC.";
        }
        else
        {
            HelloBadge.Status = HealthStatus.Neutro;
            HelloBadge.Text = "Indisponível";
            HelloText.Text = avail.Message + " O Cofre abre com a senha mestra.";
        }
    }

    private void ChkHello_Click(object sender, RoutedEventArgs e)
    {
        bool on = ChkHello.IsChecked == true;
        Prefs?.Update(p => p.UseWindowsHello = on);
        App.Core?.Log.Info("COFRE: Windows Hello " + (on ? "ligado" : "desligado") + " nas Configurações");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
