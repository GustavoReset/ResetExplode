using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FortKnox.Core.Config;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Linha da lista de restos (marcar, tamanho).</summary>
public sealed class LeftoverRow(Leftover item) : INotifyPropertyChanged
{
    private bool _selected = item.Recommended;
    public Leftover Item => item;
    public string Path => item.Path;
    public string Reason => item.Reason;
    public string SizeText => item.Kind == LeftoverKind.RegistryKey ? "registro" : item.Bytes > 0 ? CleanupView.Size(item.Bytes) : "vazia";
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// ARSENAL · Desinstalar (Fase 3.4c): lista os programas, abre o desinstalador oficial de cada um e, depois que ele termina,
/// procura o que sobrou. Os restos só saem com confirmação: pastas para a Lixeira, chaves com cópia .reg.
/// </summary>
public partial class UninstallView : UserControl
{
    private List<InstalledProgram> _all = new();
    private ListCollectionView? _view;
    private List<LeftoverRow> _leftovers = new();
    private bool _busy, _loaded;
    private Action<string, string, string, string> _addEvent = (_, _, _, _) => { };
    private string _backupFolder = Path.Combine(AppSettings.Folder, "backups");

    public UninstallView() => InitializeComponent();

    public void Attach(Action<string, string, string, string> addEvent, string dataFolder)
    {
        _addEvent = addEvent;
        _backupFolder = Path.Combine(dataFolder, "backups");
    }

    public void Activate() { if (!_loaded && !_busy) _ = LoadAsync(); }

    private Window? Owner => Window.GetWindow(this);

    private async Task LoadAsync()
    {
        _busy = true;
        Summary.Text = "Lendo os programas instalados…";
        try
        {
            _all = await Task.Run(UninstallService.List);
            _view = new ListCollectionView(_all);
            ApplyFilter();
            Grid.ItemsSource = _view;
            long total = _all.Sum(p => p.SizeBytes);
            Summary.Text = $"{_all.Count} programas · {CleanupView.Size(total)} informados pelos instaladores · o desinstalador oficial de cada um é usado";
            _loaded = true;
        }
        catch (Exception ex) { Summary.Text = $"Não foi possível ler a lista: {ex.Message}"; }
        finally { _busy = false; }
    }

    private void ApplyFilter()
    {
        if (_view == null) return;
        string q = Search.Text.Trim();
        _view.Filter = q.Length == 0 ? null : o => o is InstalledProgram p &&
            (p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) { _loaded = false; _ = LoadAsync(); }

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledProgram p } || _busy) return;
        if (p.Machine && !AdminGuard.Require(this)) return;
        if (!FkDialog.Confirm(Owner, $"Desinstalar {p.Name}?",
                "O desinstalador do próprio programa vai abrir: responda às perguntas dele. Quando ele terminar, o Fort Knox procura " +
                "o que sobrou e mostra antes de remover qualquer coisa.", "Desinstalar"))
            return;

        _busy = true;
        LeftoverCard.Visibility = Visibility.Collapsed;
        Status.Text = $"Desinstalador de {p.Name} aberto. Esperando ele terminar…";
        try
        {
            int? code = await UninstallService.RunUninstallerAsync(p, CancellationToken.None);
            bool still = await Task.Run(() => UninstallService.StillInstalled(p));
            if (still)
            {
                Status.Text = $"{p.Name} continua instalado (o desinstalador foi cancelado ou pediu para reiniciar). Nada foi removido.";
                return;
            }
            _addEvent("", "Programa desinstalado", p.Name, "");
            Status.Text = $"{p.Name} desinstalado. Procurando restos…";
            var others = (await Task.Run(UninstallService.List)).Where(x => x.RegistryKey != p.RegistryKey).ToList();
            var found = await Task.Run(() =>
                UninstallService.FindFolderLeftovers(p, UninstallService.SearchRoots.Default(), others)
                    .Concat(UninstallService.FindRegistryLeftovers(p, others)).ToList());
            ShowLeftovers(p, found);
            Status.Text = found.Count == 0 ? $"{p.Name} desinstalado. Nenhum resto encontrado." : $"{p.Name} desinstalado. Veja os restos acima.";
        }
        catch (Exception ex) { Status.Text = $"Não foi possível desinstalar {p.Name}: {ex.Message}"; }
        finally
        {
            _busy = false;
            _loaded = false;
            await LoadAsync();
        }
    }

    private void ShowLeftovers(InstalledProgram p, List<Leftover> found)
    {
        _leftovers = found.Select(l => new LeftoverRow(l)).ToList();
        LeftoverList.ItemsSource = _leftovers;
        LeftoverTitle.Text = $"RESTOS DE {p.Name.ToUpperInvariant()}";
        LeftoverSub.Text = $"{found.Count} item(ns) com o nome do programa que o desinstalador deixou. Confira antes de remover.";
        LeftoverCard.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnKeep_Click(object sender, RoutedEventArgs e) => LeftoverCard.Visibility = Visibility.Collapsed;

    private void BtnRemoveLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _leftovers.Where(r => r.Selected).Select(r => r.Item).ToList();
        if (chosen.Count == 0) { Status.Text = "Nenhum resto marcado."; return; }
        if (chosen.Any(l => l.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) || l.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            && !AdminGuard.Require(this)) return;
        if (!FkDialog.Confirm(Owner, $"Remover {chosen.Count} resto(s)?",
                "Pastas vão para a Lixeira e chaves do registro ganham uma cópia .reg antes de sair.", "Remover", kind: DialogKind.Destructive))
            return;
        var (removed, failed, backup) = UninstallService.RemoveLeftovers(chosen, _backupFolder);
        Status.Text = $"{removed} resto(s) removido(s)." +
                      (backup != null ? $" Cópia do registro em {backup}." : "") +
                      (failed.Count > 0 ? $" Não removidos: {string.Join("; ", failed.Take(4))}" : "");
        LeftoverCard.Visibility = Visibility.Collapsed;
    }
}
