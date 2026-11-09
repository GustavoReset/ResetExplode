using System.Windows;
using System.Windows.Controls;
using FortKnox.Core.Config;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Preferências/dados do ARSENAL (seção "arsenal" do modules.json).</summary>
public sealed class ArsenalSettings
{
    /// <summary>Programas de inicialização removidos, para "Restaurar removidos".</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();
}

/// <summary>ARSENAL · Inicialização: programas que abrem junto com o Windows (antigo MainWindow.Startup.cs).</summary>
public partial class StartupView : UserControl
{
    private ISettings<ArsenalSettings>? _prefs;

    public StartupView() => InitializeComponent();

    /// <summary>Ligado pelo ArsenalModule na primeira visita.</summary>
    public void Attach(ISettings<ArsenalSettings> prefs) => _prefs = prefs;

    private List<StartupBackup> Removed => _prefs?.Value.StartupRemoved ?? new();
    private Window? Owner => Window.GetWindow(this);

    public void Scan()
    {
        List<StartupEntry> list;
        try { list = StartupManager.List(); }
        catch (Exception ex) { StartupResult.Text = $"Não foi possível listar: {ex.Message}"; return; }

        StartupList.ItemsSource = list;
        int on = list.Count(e => e.Enabled);
        StartupSummary.Text = list.Count == 0 ? "NENHUM PROGRAMA NA INICIALIZAÇÃO"
                            : $"{list.Count} PROGRAMAS · {on} ATIVADOS · {list.Count - on} DESATIVADOS";
        int removed = Removed.Count;
        BtnStartupRestore.IsEnabled = removed > 0;
        BtnStartupRestore.Content = removed > 0 ? $"RESTAURAR REMOVIDOS ({removed})" : "RESTAURAR REMOVIDOS";
    }

    private bool RequireAdmin()
    {
        if (SensorMonitor.IsElevated) return true;
        FkDialog.Info(Owner, "Precisa de administrador", "Esta ação precisa do Fort Knox aberto como administrador.");
        return false;
    }

    private void BtnStartupScan_Click(object sender, RoutedEventArgs e)
    {
        StartupResult.Text = "";
        Scan();
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
        Scan();
    }

    private void StartupRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupEntry item } || _prefs == null) return;
        if (item.Machine && !RequireAdmin()) return;
        bool yes = FkDialog.Confirm(Owner, $"Remover \"{item.Name}\" da inicialização?",
            "O programa continua instalado, só deixa de abrir sozinho. Dá para desfazer em Restaurar removidos. " +
            "Se só quiser testar, prefira desligar o interruptor.", "Remover", kind: DialogKind.Destructive);
        if (!yes) return;
        try
        {
            var backup = StartupManager.Remove(item);
            _prefs.Update(p => p.StartupRemoved.Add(backup));
            StartupResult.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        Scan();
    }

    private void BtnStartupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_prefs == null) return;
        var removed = Removed;
        if (removed.Count == 0) return;
        if (removed.Any(b => b.Machine) && !RequireAdmin()) return;
        string names = string.Join(", ", removed.Take(6).Select(b => b.Name)) + (removed.Count > 6 ? $" e mais {removed.Count - 6}" : "");
        if (!FkDialog.Confirm(Owner, $"Devolver {removed.Count} programas à inicialização?", names, "Restaurar")) return;

        var failed = new List<string>();
        _prefs.Update(p =>
        {
            foreach (var b in p.StartupRemoved.ToList())
            {
                try { StartupManager.Restore(b); p.StartupRemoved.Remove(b); }
                catch (Exception ex) { failed.Add($"{b.Name} ({ex.Message})"); }
            }
        });
        StartupResult.Text = failed.Count == 0 ? "Programas restaurados." : $"Não restaurados: {string.Join("; ", failed)}";
        Scan();
    }
}
