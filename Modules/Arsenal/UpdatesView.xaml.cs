using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace FortKnox;

/// <summary>Linha da lista de atualizações (marcar e situação).</summary>
public sealed class UpdateRow(AppUpdate item) : INotifyPropertyChanged
{
    private bool _selected = true, _done;
    private string _status = "";
    public AppUpdate Item => item;
    public string Name => item.Name;
    public string Version => item.Version;
    public string Available => item.Available;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Done { get => _done; set { Set(ref _done, value); OnChanged(nameof(CanSelect)); } }
    public bool CanSelect => !Done;
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// ARSENAL · Atualizar apps (Fase 3.4d): mostra o que o winget tem de versão nova e atualiza os marcados, um por vez.
/// Ao fechar o app, para depois do pacote atual (o winget não é interrompido no meio de uma instalação).
/// </summary>
public partial class UpdatesView : UserControl
{
    private List<UpdateRow> _rows = new();
    private bool _busy, _stop, _loaded;
    private Action<string, string, string, string> _addEvent = (_, _, _, _) => { };

    public UpdatesView() => InitializeComponent();

    public void Attach(Action<string, string, string, string> addEvent) => _addEvent = addEvent;

    public bool IsBusy => _busy;
    public void RequestStop() => _stop = true;

    public void Activate() { if (!_loaded && !_busy) _ = LoadAsync(); }

    private async Task LoadAsync()
    {
        bool winget = UpdateService.WingetAvailable;
        BtnStore.Visibility = winget ? Visibility.Collapsed : Visibility.Visible;
        BtnUpdate.IsEnabled = false;
        if (!winget)
        {
            Summary.Text = "O winget não foi encontrado. Ele vem com o \"Instalador de Aplicativo\" da Microsoft Store: atualize e abra esta página de novo.";
            return;
        }
        _busy = true;
        Summary.Text = "Procurando atualizações…";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        try
        {
            var list = await UpdateService.ListAsync();
            _rows = list.Select(u => new UpdateRow(u)).ToList();
            Grid.ItemsSource = _rows;
            Summary.Text = _rows.Count switch
            {
                0 => "Tudo em dia: nenhum programa com versão nova no winget.",
                1 => "1 programa com versão nova.",
                _ => $"{_rows.Count} programas com versão nova.",
            };
            Status.Text = _rows.Count > 0 ? "Desmarque o que não quer atualizar agora e clique em Atualizar selecionados." : "";
            BtnUpdate.IsEnabled = _rows.Count > 0;
            _loaded = true;
        }
        catch (Exception ex) { Summary.Text = $"Não foi possível consultar o winget: {ex.Message}"; }
        finally
        {
            _busy = false;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) { if (_busy) return; _loaded = false; _ = LoadAsync(); }

    private void BtnAll_Click(object sender, RoutedEventArgs e) { if (!_busy) foreach (var r in _rows) r.Selected = !r.Done; }
    private void BtnNone_Click(object sender, RoutedEventArgs e) { if (!_busy) foreach (var r in _rows) r.Selected = false; }

    private void BtnStore_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NBLGGH4NNS1") { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = $"Não foi possível abrir a Microsoft Store: {ex.Message}"; }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _stop = true; BtnUpdate.IsEnabled = false;
            Status.Text = "Parando depois do programa atual…";
            return;
        }
        var sel = _rows.Where(r => r.Selected && !r.Done).ToList();
        if (sel.Count == 0) { Status.Text = "Nenhum programa marcado."; return; }

        _busy = true; _stop = false;
        BtnUpdate.Content = "Parar";
        BtnRefresh.IsEnabled = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        foreach (var r in sel) r.Status = "Na fila.";
        int ok = 0, done = 0;
        var failed = new List<string>();

        foreach (var row in sel)
        {
            if (_stop) { row.Status = "Não atualizado (parado)."; continue; }
            Status.Text = $"Atualizando {row.Name} ({done + 1} de {sel.Count})… pode levar alguns minutos.";
            row.Status = "Atualizando…";
            try
            {
                var (success, msg) = await UpdateService.UpgradeAsync(row.Item, CancellationToken.None);
                row.Status = msg;
                if (success)
                {
                    ok++; row.Done = true; row.Selected = false;
                    _addEvent("", "App atualizado", $"{row.Name} {row.Version} → {row.Available}", "");
                }
                else failed.Add(row.Name);
            }
            catch (Exception ex) { row.Status = $"Falhou: {ex.Message}"; failed.Add(row.Name); }
            done++;
            Progress.Value = (double)done / sel.Count;
        }

        _busy = false;
        BtnUpdate.Content = "Atualizar selecionados";
        BtnUpdate.IsEnabled = _rows.Any(r => !r.Done);
        BtnRefresh.IsEnabled = true;
        Progress.Visibility = Visibility.Collapsed;
        Status.Text = $"{ok} de {sel.Count} atualizados." +
                      (failed.Count > 0 ? $" Falharam: {string.Join(", ", failed)} (veja o motivo na lista)." : "") +
                      (_stop ? " Atualização interrompida." : "");
    }
}
