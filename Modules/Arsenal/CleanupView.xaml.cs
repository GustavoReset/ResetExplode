using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Linha da lista de categorias (marcar, tamanho encontrado).</summary>
public sealed class CleanupRow(CleanupCategory category) : INotifyPropertyChanged
{
    private bool _selected = category.PreSelect;
    private CleanupFinding? _finding;

    public CleanupCategory Category => category;
    public string Title => category.Title;
    public string Description => category.Description;
    public string Note => category.NeedsAdmin && !SensorMonitor.IsElevated ? "Parte destes arquivos só sai com o Fort Knox aberto como administrador." : "";
    public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
    public CleanupFinding? Finding { get => _finding; set { _finding = value; Changed(nameof(SizeText)); } }
    public string SizeText => _finding == null ? "—" : _finding.Files == 0 ? "vazio" : CleanupView.Size(_finding.Bytes);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// ARSENAL · Limpeza (Fase 3.4b): varre, mostra o tamanho por categoria e só apaga depois da confirmação.
/// A verificação usa o ScannerPanel do Design System; a lógica fica no <see cref="CleanupService"/> (sem WPF, testado).
/// </summary>
public partial class CleanupView : UserControl
{
    private readonly List<CleanupRow> _rows = CleanupService.DefaultCategories().Select(c => new CleanupRow(c)).ToList();
    private readonly ObservableCollection<ScanFinding> _findings = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _scanned;
    private Action<string, string, string, string> _addEvent = (_, _, _, _) => { };

    public CleanupView()
    {
        InitializeComponent();
        CatList.ItemsSource = _rows;
        foreach (var r in _rows)
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CleanupRow.Selected)) UpdateSummary(); };
        Scanner.Findings = _findings;
        Scanner.StartCommand = new RelayCommand(() => _ = ScanAsync());
        Scanner.CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    /// <summary>Eventos para o PAINEL ("Limpeza · 1,2 GB liberados").</summary>
    public void Attach(Action<string, string, string, string> addEvent) => _addEvent = addEvent;

    /// <summary>Primeira visita: verifica sozinho (só lê). A Lixeira é relida a cada visita.</summary>
    public void Activate()
    {
        UpdateBin();
        if (!_scanned && !_busy) _ = ScanAsync();
    }

    public void CancelAll() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.0} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BtnClean.IsEnabled = false;
        _findings.Clear();
        Scanner.FindingsCount = 0;
        Scanner.State = ScanState.Running;
        Scanner.Phase = "Procurando temporários e caches";
        Scanner.ScannedCount = 0;
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Scanner.Elapsed = sw.Elapsed, Dispatcher);
        timer.Start();
        long files = 0;
        try
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                Scanner.CurrentItem = row.Title;
                Scanner.Progress = (double)i / _rows.Count;
                var f = await Task.Run(() => CleanupService.Scan(row.Category, DateTime.Now, ct), ct);
                row.Finding = f;
                files += f.Files;
                Scanner.ScannedCount = files;
                if (f.Files > 0)
                {
                    _findings.Add(new ScanFinding(row.Title, $"{Size(f.Bytes)} em {f.Files:N0} arquivo(s).", HealthStatus.Info));
                    Scanner.FindingsCount = _findings.Count;
                }
            }
            _scanned = true;
            Scanner.Progress = 1;
            Scanner.State = ScanState.Done;
            long total = _rows.Where(r => r.Finding != null).Sum(r => r.Finding!.Bytes);
            Scanner.Phase = total > 0 ? $"Pronto. {Size(total)} podem ser liberados." : "Pronto. Nada para limpar agora.";
            Scanner.StartText = "Verificar de novo";
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            Scanner.State = ScanState.Cancelled;
            Scanner.Phase = "Verificação cancelada";
        }
        finally
        {
            timer.Stop();
            Scanner.Elapsed = sw.Elapsed;
            Scanner.CurrentItem = "";
            _busy = false;
            BtnClean.IsEnabled = _scanned;
        }
    }

    private void UpdateSummary()
    {
        long sel = _rows.Where(r => r.Selected && r.Finding != null).Sum(r => r.Finding!.Bytes);
        CatSummary.Text = $"O QUE PODE SAIR · {Size(sel)} MARCADOS";
        BtnClean.Content = sel > 0 ? $"Limpar {Size(sel)}" : "Limpar selecionados";
    }

    private void BtnAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; UpdateSummary(); }
    private void BtnNone_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; UpdateSummary(); }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
        var chosen = _rows.Where(r => r.Selected && r.Finding is { Files: > 0 }).Select(r => r.Finding!).ToList();
        if (chosen.Count == 0) { CleanResult.Text = "Nada marcado para limpar."; return; }
        long bytes = chosen.Sum(f => f.Bytes);
        if (!FkDialog.Confirm(Window.GetWindow(this), $"Apagar {Size(bytes)} de temporários e caches?",
                string.Join(", ", chosen.Select(f => f.Category.Title)) + ". Os programas recriam esses arquivos quando precisarem.",
                "Limpar", kind: DialogKind.Destructive))
            return;

        _busy = true;
        BtnClean.IsEnabled = false;
        Scanner.State = ScanState.Running;
        Scanner.Phase = "Limpando";
        Scanner.CurrentItem = "";
        var progress = new Progress<double>(p => Scanner.Progress = p);
        try
        {
            var r = await Task.Run(() => CleanupService.Clean(chosen, progress));
            CleanResult.Text = $"Pronto. {Size(r.FreedBytes)} liberados em {r.Deleted:N0} arquivo(s)." +
                               (r.Skipped > 0 ? $" {r.Skipped:N0} ficaram porque estavam em uso ou sem permissão." : "");
            _addEvent("", "Limpeza", $"{r.Deleted:N0} arquivos apagados", Size(r.FreedBytes));
        }
        catch (Exception ex) { CleanResult.Text = $"Não foi possível concluir a limpeza: {ex.Message}"; }
        finally { _busy = false; }
        _scanned = false;
        await ScanAsync(); // mostra o que sobrou
    }

    private void UpdateBin()
    {
        var (bytes, items) = CleanupService.RecycleBin();
        BinText.Text = items == 0 ? "A Lixeira está vazia." : $"{items:N0} item(ns) · {Size(bytes)}";
        BtnBin.IsEnabled = items > 0;
    }

    private void BtnBin_Click(object sender, RoutedEventArgs e)
    {
        var (bytes, items) = CleanupService.RecycleBin();
        if (items == 0) { UpdateBin(); return; }
        var owner = Window.GetWindow(this);
        if (!FkDialog.Confirm(owner, $"Esvaziar a Lixeira ({Size(bytes)})?",
                $"{items:N0} item(ns) serão apagados de vez. Não dá para desfazer.", "Esvaziar", kind: DialogKind.Destructive))
            return;
        bool ok = CleanupService.EmptyRecycleBin(owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero);
        if (ok) _addEvent("", "Lixeira esvaziada", $"{items:N0} item(ns)", Size(bytes));
        CleanResult.Text = ok ? $"Lixeira esvaziada: {Size(bytes)} liberados." : "O Windows não conseguiu esvaziar a Lixeira.";
        UpdateBin();
    }
}
