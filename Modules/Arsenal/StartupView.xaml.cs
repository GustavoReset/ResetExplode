using System.Windows;
using System.Windows.Controls;
using FortKnox.Core.Changes;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Preferências/dados do ARSENAL (seção "arsenal" do modules.json).</summary>
public sealed class ArsenalSettings
{
    /// <summary>Só leitura para a migração: até a Fase 3.4e os removidos ficavam aqui; agora são ferrolhos.</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();
}

/// <summary>ARSENAL · Inicialização: programas que abrem junto com o Windows (antigo MainWindow.Startup.cs).</summary>
public partial class StartupView : UserControl
{
    private IChangeJournal? _journal;

    public StartupView() => InitializeComponent();

    /// <summary>Ligado pelo ArsenalModule: remover vira um ferrolho com a cópia para desfazer (Fase 3.4e).</summary>
    public void Attach(IChangeJournal journal) => _journal = journal;

    private List<Ferrolho> Removed => _journal == null ? new() : ArsenalChanges.RemovedStartup(_journal);
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

    private void BtnStartupScan_Click(object sender, RoutedEventArgs e)
    {
        StartupResult.Text = "";
        Scan();
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupEntry item } box) return;
        bool turnOn = box.IsChecked == true;
        if (item.Machine && !AdminGuard.Require(this)) { box.IsChecked = item.Enabled; return; }
        try
        {
            StartupManager.SetEnabled(item, turnOn);
            StartupResult.Text = $"{item.Name}: {(turnOn ? "ativado" : "desativado")}. Vale na próxima inicialização do Windows.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: falhou ({ex.Message})."; }
        Scan();
    }

    private async void StartupRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupEntry item } || _journal == null) return;
        if (item.Machine && !AdminGuard.Require(this)) return;
        bool yes = FkDialog.Confirm(Owner, $"Remover \"{item.Name}\" da inicialização?",
            "O programa continua instalado, só deixa de abrir sozinho. Dá para desfazer em Restaurar removidos. " +
            "Se só quiser testar, prefira desligar o interruptor.", "Remover", kind: DialogKind.Destructive);
        if (!yes) return;
        try
        {
            await _journal.ApplyAsync(ArsenalChanges.StartupRequest(item));
            StartupResult.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        Scan();
    }

    private static string NameOf(Ferrolho f) =>
        f.Title.Replace("Removido da inicialização: ", "").Replace(ArsenalChanges.MachineSuffix, "");

    private async void BtnStartupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_journal == null) return;
        var removed = Removed;
        if (removed.Count == 0) return;
        if (removed.Any(f => f.Title.EndsWith(ArsenalChanges.MachineSuffix, StringComparison.Ordinal)) && !AdminGuard.Require(this)) return;
        string names = string.Join(", ", removed.Take(6).Select(NameOf)) + (removed.Count > 6 ? $" e mais {removed.Count - 6}" : "");
        if (!FkDialog.Confirm(Owner, $"Devolver {removed.Count} programas à inicialização?", names, "Restaurar")) return;

        var failed = new List<string>();
        foreach (var f in removed)
        {
            try { await _journal.RevertAsync(f.Id); }
            catch (Exception ex) { failed.Add($"{NameOf(f)} ({ex.Message})"); }
        }
        StartupResult.Text = failed.Count == 0 ? "Programas restaurados." : $"Não restaurados: {string.Join("; ", failed)}";
        Scan();
    }
}
