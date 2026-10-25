using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Inicialização: programas que abrem junto com o Windows.</summary>
public partial class MainWindow
{
    private void ScanStartup()
    {
        List<StartupEntry> list;
        try { list = StartupManager.List(); }
        catch (Exception ex) { StartupResult.Text = $"Não foi possível listar: {ex.Message}"; return; }

        StartupList.ItemsSource = list;
        int on = list.Count(e => e.Enabled);
        StartupSummary.Text = list.Count == 0 ? "NENHUM PROGRAMA NA INICIALIZAÇÃO"
                            : $"{list.Count} PROGRAMAS · {on} ATIVADOS · {list.Count - on} DESATIVADOS";
        BtnStartupRestore.IsEnabled = _settings.StartupRemoved.Count > 0;
        BtnStartupRestore.Content = _settings.StartupRemoved.Count > 0
            ? $"RESTAURAR REMOVIDOS ({_settings.StartupRemoved.Count})" : "RESTAURAR REMOVIDOS";
    }

    private void BtnStartupScan_Click(object sender, RoutedEventArgs e)
    {
        StartupResult.Text = "";
        ScanStartup();
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupEntry item } box) return;
        bool turnOn = box.IsChecked == true;
        if (item.Machine && !RequireAdmin()) { box.IsChecked = item.Enabled; return; }
        try
        {
            StartupManager.SetEnabled(item, turnOn);
            StartupResult.Text = $"{item.Name}: {(turnOn ? "ativado" : "desativado")}. Vale na próxima inicialização do Windows.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: falhou ({ex.Message})."; }
        ScanStartup();
    }

    private void StartupRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupEntry item }) return;
        if (item.Machine && !RequireAdmin()) return;
        var r = MessageBox.Show($"Remover \"{item.Name}\" da inicialização do Windows?\n\n{item.Command}\n\n" +
                                "O programa continua instalado, só deixa de abrir sozinho. " +
                                "Dá para desfazer em \"Restaurar removidos\".\n\n" +
                                "Se só quiser testar, prefira desligar o interruptor.",
                                "Inicialização", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            _settings.StartupRemoved.Add(StartupManager.Remove(item));
            _settings.Save();
            StartupResult.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        ScanStartup();
    }

    private void BtnStartupRestore_Click(object sender, RoutedEventArgs e)
    {
        var removed = _settings.StartupRemoved;
        if (removed.Count == 0) return;
        if (removed.Any(b => b.Machine) && !RequireAdmin()) return;
        string names = string.Join("\n", removed.Take(12).Select(b => $"  • {b.Name}"))
                       + (removed.Count > 12 ? $"\n  … e mais {removed.Count - 12}" : "");
        var r = MessageBox.Show($"Devolver {removed.Count} programas à inicialização do Windows?\n\n{names}",
                                "Inicialização", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        foreach (var b in removed.ToList())
        {
            try { StartupManager.Restore(b); removed.Remove(b); }
            catch (Exception ex) { failed.Add($"{b.Name} ({ex.Message})"); }
        }
        _settings.Save();
        StartupResult.Text = failed.Count == 0 ? "Programas restaurados." : $"Não restaurados: {string.Join("; ", failed)}";
        ScanStartup();
    }
}
