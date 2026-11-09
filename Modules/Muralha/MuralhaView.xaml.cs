using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FortKnox.Muralha;
using FortKnox.UI;
using Microsoft.Win32;

namespace FortKnox;

/// <summary>Um item encontrado na varredura, com o que a pessoa decidiu.</summary>
public sealed class ThreatRow(ScanResult r) : INotifyPropertyChanged
{
    private bool _pending = true;
    private string _note = "";
    public ScanResult Result => r;
    public string Path => r.Path;
    public string FileName => System.IO.Path.GetFileName(r.Path);
    public string Reason => r.Reason;
    public string VerdictText => r.Verdict == Verdict.Malicioso ? "Malicioso" : "Suspeito";
    public HealthStatus Status => r.Verdict == Verdict.Malicioso ? HealthStatus.Ruim : HealthStatus.Atencao;
    public bool Pending { get => _pending; set => Set(ref _pending, value); }
    public string Note { get => _note; set => Set(ref _note, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}

/// <summary>Preferências/dados da MURALHA (seção "muralha" do modules.json).</summary>
public sealed class MuralhaSettings
{
    public DateTime? LastScan { get; set; }
    public int LastScanFiles { get; set; }
    public int LastScanFindings { get; set; }
}

/// <summary>
/// MURALHA · Varredura (Fase 3.5a): varredura rápida ou de uma pasta com o motor em camadas, itens encontrados com o motivo e
/// a quarentena cifrada. Nada sai do lugar sem confirmação; tudo na quarentena pode voltar.
/// </summary>
public partial class MuralhaView : UserControl
{
    private readonly ObservableCollection<ScanFinding> _findings = new();
    private readonly ObservableCollection<ThreatRow> _threats = new();
    private MuralhaEngine _engine = new(SignatureBase.Embedded());
    private Quarantine _quarantine = new(Quarantine.DefaultFolder);
    private Core.Config.ISettings<MuralhaSettings>? _prefs;
    private Action<string, string, string, string> _addEvent = (_, _, _, _) => { };
    private CancellationTokenSource? _cts;

    public MuralhaView()
    {
        InitializeComponent();
        Scanner.Findings = _findings;
        Scanner.StartCommand = new RelayCommand(() => _ = ScanAsync(MuralhaEngine.QuickRoots(), 3, "MURALHA · VARREDURA RÁPIDA"));
        Scanner.CancelCommand = new RelayCommand(() => _cts?.Cancel());
        ThreatList.ItemsSource = _threats;
    }

    public void Attach(Core.Config.ISettings<MuralhaSettings> prefs, Action<string, string, string, string> addEvent)
    {
        _prefs = prefs;
        _addEvent = addEvent;
    }

    public bool IsBusy => _cts != null;
    public void CancelAll() => _cts?.Cancel();
    public Quarantine Quarantine => _quarantine;

    private Window? Owner => Window.GetWindow(this);

    public void Activate()
    {
        RefreshQuarantine();
        var p = _prefs?.Value;
        Summary.Text = p?.LastScan is DateTime last
            ? $"Última varredura em {last:dd/MM/yyyy HH:mm}: {p.LastScanFiles:N0} arquivos, {p.LastScanFindings} item(ns) encontrado(s) · complementa o Microsoft Defender"
            : "Nenhuma varredura ainda · proteção complementar ao Microsoft Defender · nada sai do lugar sem você confirmar";
        Footer.Text = $"Base de assinaturas {_engine.Signatures.Version} ({_engine.Signatures.Hashes.Count} hash(es), {_engine.Signatures.Rules.Count} regras) · " +
                      (Defender.Available ? "segunda opinião do Microsoft Defender para o que ficar suspeito" : "Microsoft Defender não encontrado (sem segunda opinião)") +
                      " · arquivos com assinatura digital válida são considerados confiáveis · varredura completa e proteção em tempo real chegam nas próximas fases.";
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new OpenFolderDialog { Title = "Pasta para verificar" };
        if (dlg.ShowDialog(Owner) != true) return;
        _ = ScanAsync(new[] { dlg.FolderName }, int.MaxValue, "MURALHA · " + Path.GetFileName(dlg.FolderName.TrimEnd('\\')).ToUpperInvariant());
    }

    private async Task ScanAsync(IReadOnlyList<string> roots, int depth, string title)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _findings.Clear();
        _threats.Clear();
        ThreatCard.Visibility = Visibility.Collapsed;
        BtnFolder.IsEnabled = false;
        Scanner.Title = title;
        Scanner.State = ScanState.Running;
        Scanner.Phase = "Listando arquivos";
        Scanner.Progress = double.NaN;
        Scanner.ScannedCount = 0;
        Scanner.FindingsCount = 0;
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Scanner.Elapsed = sw.Elapsed, Dispatcher);
        int scanned = 0;
        try
        {
            var files = await Task.Run(() => MuralhaEngine.Enumerate(roots, depth, ct).ToList(), ct);
            Scanner.Phase = $"Verificando {files.Count:N0} arquivos";
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Scanner.CurrentItem = files[i];
                ScanResult r;
                try { r = await Task.Run(() => _engine.ScanFileAsync(files[i], ct), ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // em uso ou sem permissão
                scanned++;
                Scanner.ScannedCount = scanned;
                Scanner.Progress = (double)(i + 1) / files.Count;
                if (r.Verdict == Verdict.Limpo) continue;
                var row = new ThreatRow(r);
                _threats.Add(row);
                _findings.Add(new ScanFinding(row.FileName, r.Reason, row.Status));
                Scanner.FindingsCount = _findings.Count;
                ThreatCard.Visibility = Visibility.Visible;
            }
            Scanner.Progress = 1;
            Scanner.State = ScanState.Done;
            Scanner.Phase = _threats.Count == 0 ? $"Pronto. Nada suspeito em {scanned:N0} arquivos." : $"Pronto. {_threats.Count} item(ns) para você decidir.";
            Scanner.StartText = "Verificar de novo";
            _prefs?.Update(p => { p.LastScan = DateTime.Now; p.LastScanFiles = scanned; p.LastScanFindings = _threats.Count; });
            _addEvent("", "Varredura da MURALHA", _threats.Count == 0 ? $"{scanned:N0} arquivos, nada suspeito" : $"{scanned:N0} arquivos, {_threats.Count} item(ns) encontrado(s)", "");
        }
        catch (OperationCanceledException)
        {
            Scanner.State = ScanState.Cancelled;
            Scanner.Phase = $"Varredura cancelada depois de {scanned:N0} arquivos";
        }
        catch (Exception ex)
        {
            Scanner.State = ScanState.Error;
            Scanner.Phase = $"A varredura parou: {ex.Message}";
        }
        finally
        {
            timer.Stop();
            Scanner.Elapsed = sw.Elapsed;
            Scanner.CurrentItem = "";
            BtnFolder.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            UpdateThreatTitle();
            Activate();
        }
    }

    private void UpdateThreatTitle()
    {
        int pending = _threats.Count(t => t.Pending);
        ThreatTitle.Text = $"ITENS ENCONTRADOS ({_threats.Count})";
        BtnQuarantineAll.IsEnabled = pending > 0;
    }

    private void BtnIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ThreatRow row }) return;
        row.Pending = false;
        row.Note = "Ignorado nesta varredura. O arquivo continua onde está.";
        UpdateThreatTitle();
    }

    private void BtnQuarantine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ThreatRow row }) return;
        if (!FkDialog.Confirm(Owner, $"Mover {row.FileName} para a quarentena?",
                "O arquivo sai do lugar e fica cifrado na quarentena. Se for algo seu, dá para restaurar depois.", "Mover para a quarentena",
                kind: DialogKind.Destructive))
            return;
        MoveToQuarantine(new[] { row });
    }

    private void BtnQuarantineAll_Click(object sender, RoutedEventArgs e)
    {
        var rows = _threats.Where(t => t.Pending).ToList();
        if (rows.Count == 0) return;
        if (!FkDialog.Confirm(Owner, $"Mover {rows.Count} item(ns) para a quarentena?",
                "Eles saem do lugar e ficam cifrados na quarentena. Dá para restaurar qualquer um depois.", "Mover para a quarentena",
                kind: DialogKind.Destructive))
            return;
        MoveToQuarantine(rows);
    }

    private void MoveToQuarantine(IEnumerable<ThreatRow> rows)
    {
        int ok = 0;
        foreach (var row in rows)
        {
            try
            {
                _quarantine.Add(row.Path, row.Result.Verdict, row.Reason);
                row.Pending = false;
                row.Note = "Na quarentena.";
                ok++;
                _addEvent("", "Item na quarentena", row.FileName, "");
            }
            catch (FileNotFoundException) { row.Pending = false; row.Note = "O arquivo já não existe (o Defender pode ter removido)."; }
            catch (Exception ex) { row.Note = ex.Message; }
        }
        Status.Text = ok > 0 ? $"{ok} item(ns) movido(s) para a quarentena." : "";
        UpdateThreatTitle();
        RefreshQuarantine();
    }

    private void RefreshQuarantine()
    {
        List<QuarantineItem> items;
        try { items = _quarantine.List(); }
        catch (Exception ex) { Status.Text = $"Não foi possível ler a quarentena: {ex.Message}"; items = new(); }
        QuarantineList.ItemsSource = items;
        QuarantineTitle.Text = items.Count == 0 ? "QUARENTENA" : $"QUARENTENA ({items.Count})";
        QuarantineEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuarantineItem it }) return;
        if (!FkDialog.Confirm(Owner, "Restaurar este arquivo?",
                $"Ele volta para {it.OriginalPath}. Só restaure se tiver certeza de que é seu e confiável.", "Restaurar"))
            return;
        try
        {
            string to = _quarantine.Restore(it.Id);
            Status.Text = $"Restaurado em {to}.";
            _addEvent("", "Restaurado da quarentena", Path.GetFileName(to), "");
        }
        catch (Exception ex) { Status.Text = $"Não foi possível restaurar: {ex.Message}"; }
        RefreshQuarantine();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuarantineItem it }) return;
        if (!FkDialog.Confirm(Owner, "Excluir de vez?",
                $"A cópia cifrada de {Path.GetFileName(it.OriginalPath)} será apagada. Não tem como desfazer.", "Excluir de vez",
                kind: DialogKind.Destructive))
            return;
        try { _quarantine.Delete(it.Id); Status.Text = "Excluído da quarentena."; }
        catch (Exception ex) { Status.Text = $"Não foi possível excluir: {ex.Message}"; }
        RefreshQuarantine();
    }
}
