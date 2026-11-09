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
    public DateTime? LastFullScan { get; set; }

    /// <summary>"Confiar sempre": SHA-256 → caminho do arquivo quando foi marcado.</summary>
    public Dictionary<string, string> TrustedHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// MURALHA · Varredura (Fases 3.5a e 3.5b): varredura rápida, completa ou de uma pasta com o motor em camadas (agora com YARA),
/// itens encontrados com o motivo e a quarentena cifrada. Nada sai do lugar sem confirmação; tudo na quarentena pode voltar.
/// </summary>
public partial class MuralhaView : UserControl
{
    private readonly ObservableCollection<ScanFinding> _findings = new();
    private readonly ObservableCollection<ThreatRow> _threats = new();
    private readonly SignatureBase _signatures = SignatureBase.Embedded();
    private readonly TrustList _trusted = new();
    private MuralhaEngine? _engine;
    private YaraLayer? _yara;
    private string? _yaraError;
    private Quarantine _quarantine = new(Quarantine.DefaultFolder);
    private Core.Config.ISettings<MuralhaSettings>? _prefs;
    private Action<string, string, string, string> _addEvent = (_, _, _, _) => { };
    private CancellationTokenSource? _cts;

    public MuralhaView()
    {
        InitializeComponent();
        Scanner.Findings = _findings;
        Scanner.StartCommand = new RelayCommand(() => _ = ScanAsync(MuralhaEngine.QuickRoots(), 3, null, "MURALHA · VARREDURA RÁPIDA", full: false));
        Scanner.CancelCommand = new RelayCommand(() => _cts?.Cancel());
        ThreatList.ItemsSource = _threats;
    }

    public void Attach(Core.Config.ISettings<MuralhaSettings> prefs, Action<string, string, string, string> addEvent)
    {
        _prefs = prefs;
        _addEvent = addEvent;
        foreach (var (hash, path) in prefs.Value.TrustedHashes) _trusted.Add(hash, $"Você marcou como confiável ({Path.GetFileName(path)}).");
    }

    public bool IsBusy => _cts != null;
    public void CancelAll() => _cts?.Cancel();
    public Quarantine Quarantine => _quarantine;

    private Window? Owner => Window.GetWindow(this);

    /// <summary>Monta o motor uma vez: hashes dos binários do Fort Knox e as regras YARA (o libyara carrega na primeira vez).</summary>
    private async Task<MuralhaEngine> EnsureEngineAsync()
    {
        if (_engine != null) return _engine;
        await Task.Run(() =>
        {
            _trusted.AddFortKnoxFiles(AppContext.BaseDirectory);
            _yara = YaraLayer.TryCreate(YaraLayer.EmbeddedRules(), out _yaraError);
        });
        var yara = _yara;
        _engine = new MuralhaEngine(_signatures) { Trusted = _trusted, Yara = yara == null ? null : yara.ScanFile };
        return _engine;
    }

    public void Activate()
    {
        RefreshQuarantine();
        var p = _prefs?.Value;
        Summary.Text = p?.LastScan is DateTime last
            ? $"Última varredura em {last:dd/MM/yyyy HH:mm}: {p.LastScanFiles:N0} arquivos, {p.LastScanFindings} item(ns) encontrado(s)" +
              (p.LastFullScan is DateTime full ? $" · última completa em {full:dd/MM/yyyy}" : " · nenhuma varredura completa ainda") +
              " · complementa o Microsoft Defender"
            : "Nenhuma varredura ainda · proteção complementar ao Microsoft Defender · nada sai do lugar sem você confirmar";
        string yara = _engine == null ? "regras YARA carregam na primeira varredura"
            : _yara != null ? $"{_yara.RuleCount} regras YARA" : $"sem YARA ({_yaraError})";
        int userTrusted = p?.TrustedHashes.Count ?? 0;
        Footer.Text = $"Base de assinaturas {_signatures.Version} ({_signatures.Hashes.Count} hash(es), {_signatures.Rules.Count} regras) · {yara} · " +
                      (Defender.Available ? "segunda opinião do Microsoft Defender para o que ficar suspeito" : "Microsoft Defender não encontrado (sem segunda opinião)") +
                      " · arquivos com assinatura digital válida (no arquivo ou no catálogo do Windows) e os do próprio Fort Knox são considerados confiáveis" +
                      (userTrusted > 0 ? $" · {userTrusted} arquivo(s) marcado(s) por você como confiável" : "") +
                      " · proteção em tempo real chega na próxima fase.";
        BtnForgetTrusted.Visibility = userTrusted > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new OpenFolderDialog { Title = "Pasta para verificar" };
        if (dlg.ShowDialog(Owner) != true) return;
        _ = ScanAsync(new[] { dlg.FolderName }, int.MaxValue, null, "MURALHA · " + Path.GetFileName(dlg.FolderName.TrimEnd('\\')).ToUpperInvariant(), full: false);
    }

    private void BtnFull_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var roots = MuralhaEngine.FullRoots();
        if (!FkDialog.Confirm(Owner, "Fazer a varredura completa?",
                $"Verifica os executáveis, scripts e documentos com macro de todos os discos ({string.Join(", ", roots.Select(r => r.TrimEnd('\\')))}). " +
                "Pode levar de vários minutos a mais de uma hora; dá para usar o PC enquanto isso e cancelar quando quiser.", "Começar"))
            return;
        var skip = MuralhaEngine.FullScanSkips(AppContext.BaseDirectory, Quarantine.DefaultFolder);
        _ = ScanAsync(roots, int.MaxValue, skip, "MURALHA · VARREDURA COMPLETA", full: true);
    }

    private async Task ScanAsync(IReadOnlyList<string> roots, int depth, IReadOnlySet<string>? skip, string title, bool full)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _findings.Clear();
        _threats.Clear();
        ThreatCard.Visibility = Visibility.Collapsed;
        BtnFolder.IsEnabled = BtnFull.IsEnabled = false;
        Scanner.Title = title;
        Scanner.State = ScanState.Running;
        Scanner.Phase = "Preparando o motor";
        Scanner.Progress = double.NaN;
        Scanner.ScannedCount = 0;
        Scanner.FindingsCount = 0;
        var sw = Stopwatch.StartNew();
        int listed = 0, scanned = 0, done = 0, total = 0;
        string current = "";
        bool listing = true;
        // A tela só é atualizada pelo timer: com milhares de arquivos por minuto, atualizar a cada arquivo travaria a janela.
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) =>
        {
            Scanner.Elapsed = sw.Elapsed;
            if (listing) { if (listed > 0) Scanner.Phase = $"Listando arquivos ({Volatile.Read(ref listed):N0})"; return; }
            Scanner.CurrentItem = Volatile.Read(ref current);
            Scanner.ScannedCount = Volatile.Read(ref scanned);
            if (total > 0) Scanner.Progress = (double)Volatile.Read(ref done) / total;
        }, Dispatcher);
        try
        {
            var engine = await EnsureEngineAsync();
            Scanner.Phase = "Listando arquivos";
            var files = await Task.Run(() =>
            {
                var list = new List<string>();
                foreach (var f in MuralhaEngine.Enumerate(roots, depth, ct, skip)) { list.Add(f); Interlocked.Increment(ref listed); }
                return list;
            }, ct);
            total = files.Count;
            listing = false;
            Scanner.Phase = $"Verificando {total:N0} arquivos";

            // Varredura em paralelo (metade dos núcleos, no máximo 4) para não roubar o PC inteiro.
            var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
            await Parallel.ForEachAsync(files, opts, async (file, token) =>
            {
                Volatile.Write(ref current, file);
                ScanResult r;
                try { r = await engine.ScanFileAsync(file, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref done); return; } // em uso ou sem permissão
                Interlocked.Increment(ref scanned);
                Interlocked.Increment(ref done);
                if (r.Verdict == Verdict.Limpo) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    var row = new ThreatRow(r);
                    _threats.Add(row);
                    _findings.Add(new ScanFinding(row.FileName, r.Reason, row.Status));
                    Scanner.FindingsCount = _findings.Count;
                    ThreatCard.Visibility = Visibility.Visible;
                });
            });
            Scanner.ScannedCount = scanned;
            Scanner.Progress = 1;
            Scanner.State = ScanState.Done;
            Scanner.Phase = _threats.Count == 0 ? $"Pronto. Nada suspeito em {scanned:N0} arquivos." : $"Pronto. {_threats.Count} item(ns) para você decidir.";
            Scanner.StartText = "Verificar de novo";
            _prefs?.Update(p =>
            {
                p.LastScan = DateTime.Now; p.LastScanFiles = scanned; p.LastScanFindings = _threats.Count;
                if (full) p.LastFullScan = DateTime.Now;
            });
            string kind = full ? "Varredura completa da MURALHA" : "Varredura da MURALHA";
            _addEvent("", kind, _threats.Count == 0 ? $"{scanned:N0} arquivos, nada suspeito" : $"{scanned:N0} arquivos, {_threats.Count} item(ns) encontrado(s)", "");
        }
        catch (OperationCanceledException)
        {
            Scanner.ScannedCount = scanned;
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
            BtnFolder.IsEnabled = BtnFull.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            UpdateThreatTitle();
            Activate();
        }
    }

    private void BtnTrust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ThreatRow row }) return;
        string sha = row.Result.Sha256;
        if (sha.Length != 64) return;
        if (!FkDialog.Confirm(Owner, $"Confiar sempre em {row.FileName}?",
                "A MURALHA não vai mais acusar este arquivo, em nenhuma pasta. Se ele for alterado (outro conteúdo), volta a ser verificado. " +
                "Só faça isso se souber de onde ele veio.", "Confiar sempre"))
            return;
        _trusted.Add(sha, $"Você marcou como confiável ({row.FileName}).");
        _prefs?.Update(p => p.TrustedHashes[sha] = row.Path);
        foreach (var t in _threats.Where(t => t.Pending && t.Result.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)))
        {
            t.Pending = false;
            t.Note = "Marcado como confiável. A MURALHA não acusa mais este arquivo.";
        }
        _addEvent("", "Arquivo marcado como confiável", row.FileName, "");
        UpdateThreatTitle();
        Activate();
    }

    private void BtnForgetTrusted_Click(object sender, RoutedEventArgs e)
    {
        int n = _prefs?.Value.TrustedHashes.Count ?? 0;
        if (n == 0) return;
        if (!FkDialog.Confirm(Owner, $"Esquecer os {n} arquivo(s) marcados como confiáveis?",
                "Eles voltam a ser verificados normalmente na próxima varredura.", "Esquecer"))
            return;
        foreach (var hash in _prefs!.Value.TrustedHashes.Keys.ToList()) _trusted.Remove(hash);
        _prefs.Update(p => p.TrustedHashes.Clear());
        Status.Text = "Lista de arquivos confiáveis limpa.";
        Activate();
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
