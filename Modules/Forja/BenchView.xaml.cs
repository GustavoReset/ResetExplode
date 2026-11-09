using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using MeterItem = FortKnox.MainWindow.MeterItem;

namespace FortKnox;

/// <summary>
/// FORJA · Benchmark (Fase 3.3i; antes no MainWindow.xaml.cs, MainWindow.Rt.cs e no trecho de disco do MainWindow.Disks.cs):
/// render de CPU, cena de jogo (GPU), ray tracing, simulação de jogo (CPU), memória e carregamento, e disco.
/// As dependências da janela principal entram por <see cref="Attach"/>; o histórico continua em AppSettings (PAINEL e relatório leem).
/// </summary>
public partial class BenchView : UserControl
{
    public sealed record MemRow(string Label, string Value, string Hint);

    public sealed class Host
    {
        public required LiveReadings Live { get; init; }
        public required AppSettings Settings { get; init; }
        public required Action Save { get; init; }
        public required Func<string> CpuName { get; init; }
        /// <summary>Outro teste pesado rodando fora do Benchmark (Stress test, verificação de overclock).</summary>
        public required Func<bool> OtherBusy { get; init; }
        /// <summary>Pausa a leitura dos sensores (consultas ao driver geram engasgos no frametime).</summary>
        public required Action<bool> PauseSensors { get; init; }
        public required Func<bool> GetOverlay { get; init; }
        public required Action<bool> SetOverlay { get; init; }
        /// <summary>Evento para "eventos recentes" do PAINEL: (ícone, título, detalhe, valor).</summary>
        public required Action<string, string, string, string> AddEvent { get; init; }
    }

    private Host _host = null!;
    private AppSettings _settings => _host.Settings;
    private SensorMonitor _monitor => _host.Live.Monitor;
    private string _cpuName => _host.CpuName();
    private GpuInfo? MainGpu => _host.Live.MainGpu;
    private bool _gpuBenchActive { set => _host.PauseSensors(value); }

    private readonly BenchRenderer _bench = new();
    private System.Windows.Media.Imaging.WriteableBitmap? _benchBitmap;
    private CancellationTokenSource? _benchCts;
    private DispatcherTimer? _benchTimer;
    private DateTime _benchStart;
    private string _benchPhase = "";
    private DateTime _benchPhaseStart;

    public BenchView() => InitializeComponent();

    public void Attach(Host host) => _host = host;

    /// <summary>Algum benchmark rodando (render de CPU ou um dos testes de jogo/disco)?</summary>
    public bool IsRunning => BenchRunning || _gameBenchRunning;

    /// <summary>Uma vez por segundo, com a página aberta: temperatura, clocks e consumo da CPU no render.</summary>
    public void Tick() { if (IsVisible) UpdateBenchLive(); }

    /// <summary>Ao fechar o app: cancela qualquer benchmark em andamento (arquivos temporários são apagados pelos próprios testes).</summary>
    public void CancelAll()
    {
        try { _benchCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _gameBenchCts?.Cancel(); } catch (ObjectDisposedException) { }
        _gpuBench?.Cancel();
        _rtBench?.Cancel();
    }

    private Brush Fg => (Brush)FindResource("Fg");
    private Brush Muted => (Brush)FindResource("Muted");

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void AddEvent(string icon, string title, string detail, string value) => _host.AddEvent(icon, title, detail, value);

    /// <summary>Chamado pelo Shell depois de Attach (imagem do render e históricos).</summary>
    public void Init()
    {
        _benchBitmap = new System.Windows.Media.Imaging.WriteableBitmap(BenchRenderer.Width, BenchRenderer.Height, 96, 96,
                                                                       PixelFormats.Bgra32, null);
        BenchImage.Source = _benchBitmap;
        UpdateBenchHistory();
        UpdateBenchLive();
    }

    private bool BenchRunning => _benchCts != null;

    private async void BtnBench_Click(object sender, RoutedEventArgs e)
    {
        if (BenchRunning) { _benchCts!.Cancel(); return; }
        if (_host.OtherBusy() || _gameBenchRunning) { BenchHint.Text = "Pare o outro teste antes de rodar o benchmark."; return; }

        string mode = CheckedTag(BenchModePanel) ?? "both";
        bool sustained = CheckedTag(BenchDurationPanel) == "10";
        var minDuration = sustained ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;
        int threads = Environment.ProcessorCount;

        _benchCts = new CancellationTokenSource();
        var ct = _benchCts.Token;
        BtnBench.Content = "PARAR";
        BtnBench.Style = (Style)FindResource("OutlineButton");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = false;
        BenchHint.Text = "";
        BenchIdle.Visibility = Visibility.Collapsed;
        BenchMt.Text = BenchSt.Text = "—";
        BenchRatio.Text = "";
        _benchStart = DateTime.Now;
        _benchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => UpdateBenchView(), Dispatcher);
        _benchTimer.Start();

        double? mt = null, st = null;
        try
        {
            if (mode is "mt" or "both")
            {
                _benchPhase = $"Multi-core · {threads} threads";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(threads, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { mt = r.Score; BenchMt.Text = $"{r.Score:N0}"; }
            }
            if (!ct.IsCancellationRequested && mode is "st" or "both")
            {
                _benchPhase = "Single-core · 1 thread";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(1, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { st = r.Score; BenchSt.Text = $"{r.Score:N0}"; }
            }
        }
        catch (OperationCanceledException) { }

        _benchTimer.Stop();
        UpdateBenchView();
        bool cancelled = ct.IsCancellationRequested;
        _benchCts = null;
        BtnBench.Content = "INICIAR";
        BtnBench.Style = (Style)FindResource("PrimaryButton");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = true;
        BenchBuckets.Children.Clear();

        var total = DateTime.Now - _benchStart;
        if (cancelled)
        {
            BenchStatus.Text = "Cancelado";
            return;
        }
        BenchStatus.Text = $"Concluído em {total:m\\:ss}";
        if (mt is double m && st is double s)
            BenchRatio.Text = $"Multi ÷ single: {m / s:0.0}x com {threads} threads " +
                              $"({Environment.ProcessorCount / 2} núcleos com SMT costumam render 8–12x).";

        _settings.BenchRuns.Insert(0, new BenchRun(DateTime.Now, mt, st, sustained, GameCatalog.ShortCpuName(_cpuName)));
        AddEvent("", "FORJA", "Render de CPU", mt is double mte ? $"{mte:N0} pts" : st is double ste ? $"{ste:N0} pts" : "");
        if (_settings.BenchRuns.Count > 10) _settings.BenchRuns.RemoveRange(10, _settings.BenchRuns.Count - 10);
        _settings.Save();
        UpdateBenchHistory();
    }

    /// <summary>Copia a imagem parcial e desenha o contorno dos blocos que cada thread está renderizando.</summary>
    private void UpdateBenchView()
    {
        _benchBitmap!.WritePixels(new Int32Rect(0, 0, BenchRenderer.Width, BenchRenderer.Height),
                                  _bench.Pixels, BenchRenderer.Width * 4, 0);

        BenchBuckets.Children.Clear();
        var active = _bench.Active;
        foreach (int b in active)
        {
            if (b < 0) continue;
            var (x, y, w, h) = _bench.Buckets[b];
            var rect = new Rectangle { Width = w, Height = h, Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5 };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            BenchBuckets.Children.Add(rect);
        }

        if (BenchRunning)
        {
            double frac = _bench.BucketsTotal > 0 ? (double)_bench.BucketsDone / _bench.BucketsTotal : 0;
            BenchProgress.ScaleX = frac;
            // Tempo restante da passada atual, pela velocidade até agora (só depois de 3% para não oscilar).
            var phaseElapsed = DateTime.Now - _benchPhaseStart;
            string eta = frac >= 0.03 ? $" · faltam ~{TimeSpan.FromSeconds(phaseElapsed.TotalSeconds * (1 - frac) / frac):m\\:ss}" : "";
            BenchStatus.Text = $"{_benchPhase} · {frac:P0}{eta}";
            BenchElapsed.Text = (DateTime.Now - _benchStart).ToString(@"mm\:ss");
        }
        else BenchProgress.ScaleX = 0;
    }

    /// <summary>Temperatura, clock médio dos núcleos e consumo da CPU (atualizado junto com os sensores).</summary>
    private void UpdateBenchLive()
    {
        var clocks = _monitor.Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Clock &&
                                              r.Name.StartsWith("Core #") && r.RawValue > 0).Select(r => r.RawValue!.Value).ToList();
        BenchLive.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("CPU", GameCatalog.ShortCpuName(_cpuName)),
            new KeyValuePair<string, string>("Threads", $"{Environment.ProcessorCount}"),
            new KeyValuePair<string, string>("Temperatura", _monitor.CpuTemperature is float t ? $"{t:0.#} °C" : "—"),
            new KeyValuePair<string, string>("Clock médio", clocks.Count > 0 ? $"{clocks.Average():0} MHz" : "—"),
            new KeyValuePair<string, string>("Consumo", _monitor.CpuPower is float p ? $"{p:0.#} W" : "—"),
            new KeyValuePair<string, string>("Carga", _monitor.CpuLoad is float l ? $"{l:0} %" : "—"),
        };
    }

    private void UpdateBenchHistory()
    {
        var runs = _settings.BenchRuns;
        BenchHistoryEmpty.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        double bestMt = runs.Max(r => r.Multi) ?? 1, bestSt = runs.Max(r => r.Single) ?? 1;
        BenchHistory.ItemsSource = runs.Select(r =>
        {
            string label = $"{r.Date:dd/MM HH:mm}{(r.Sustained ? " · 10 min" : "")}";
            string value = $"{(r.Multi is double m ? $"{m:N0}" : "—")} / {(r.Single is double s ? $"{s:N0}" : "—")}";
            double frac = r.Multi is double mm ? mm / bestMt : r.Single is double ss ? ss / bestSt : 0;
            return new MeterItem(label, value, frac);
        }).ToList();
    }


    private bool _gameBenchRunning;
    private GpuGameBench? _gpuBench;
    private CancellationTokenSource? _gameBenchCts;
    private System.Windows.Media.Imaging.WriteableBitmap? _simBitmap;
    private int[] _simPixels = Array.Empty<int>();
    private const int SimView = 440;


    private void BenchTest_Checked(object sender, RoutedEventArgs e)
    {
        if (BenchPanelRender == null) return; // durante InitializeComponent
        string test = (string)((FrameworkElement)sender).Tag;
        BenchPanelRender.Visibility = test == "render" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelGpu.Visibility = test == "gpu" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelRt.Visibility = test == "rt" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelSim.Visibility = test == "sim" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelMem.Visibility = test == "mem" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelDisk.Visibility = test == "disk" ? Visibility.Visible : Visibility.Collapsed;
        if (test == "disk") InitDiskBench();
        BenchSub.Text = test switch
        {
            "gpu" => "Cena de jogo em Direct3D 11 para medir a placa de vídeo, no estilo 3DMark / Unigine",
            "rt" => "Ray tracing por hardware (DXR 1.1, Direct3D 12): usa os núcleos de RT da placa, com 3 passadas e nota pela mediana",
            "sim" => "Quanto FPS a CPU aguenta numa simulação de jogo com milhares de unidades",
            "mem" => "Latência e banda da RAM e velocidade de carregamento do disco",
            "disk" => "Velocidade de leitura e escrita do disco: sequencial e acessos aleatórios de 4 KB, no estilo CrystalDiskMark",
            _ => "Benchmark de CPU por renderização 3D (path tracing), no mesmo princípio do Cinebench",
        };
        UpdateGameBenchHistory();
    }

    private void SaveBenchRecord(string test, double score, string summary)
    {
        AddEvent("", "Benchmark concluído", summary, "");
        _settings.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, test, score, summary));
        // Mantém os 10 mais recentes de cada teste.
        _settings.BenchRecords = _settings.BenchRecords.GroupBy(r => r.Test).SelectMany(g => g.Take(10))
                                          .OrderByDescending(r => r.Date).ToList();
        _settings.Save();
        UpdateGameBenchHistory();
    }

    private void UpdateGameBenchHistory()
    {
        List<MeterItem> For(string test)
        {
            var list = _settings.BenchRecords.Where(r => r.Test == test).ToList();
            double best = list.Count > 0 ? list.Max(r => r.Score) : 1;
            return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best > 0 ? r.Score / best : 0)).ToList();
        }
        GbHistory.ItemsSource = For("gpu");
        RtHistory.ItemsSource = RtHistoryItems();
        SimHistory.ItemsSource = For("sim");
        MemHistory.ItemsSource = For("mem");
        DiskHistory.ItemsSource = For("disk");
    }

    private void SetGameBenchRunning(Button btn, bool running)
    {
        _gameBenchRunning = running;
        btn.Content = running ? "CANCELAR" : "INICIAR";
        btn.Style = (Style)FindResource(running ? "OutlineButton" : "PrimaryButton");
        BenchTestPanel.IsEnabled = !running;
    }

    // ----- Cena de jogo (GPU) -----

    private async void BtnGpuBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gpuBench?.Cancel(); return; }
        if (IsRunning || _host.OtherBusy()) { GbStatus.Text = "Termine o outro teste antes."; return; }

        var size = (CheckedTag(GbResPanel) ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int secs = int.Parse(CheckedTag(GbDurPanel) ?? "60");
        _gpuBench = new GpuGameBench
        {
            Width = size[0], Height = size[1], Duration = TimeSpan.FromSeconds(secs),
            AdapterName = MainGpu?.Name ?? ""
        };
        SetGameBenchRunning(BtnGpuBench, true);
        GbResult.ItemsSource = null;
        var bench = _gpuBench;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            GbFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            GbProgress.ScaleX = bench.Progress;
            GbStatus.Text = $"Rodando · {size[0]}x{size[1]} · {bench.Progress:P0}";
        }, Dispatcher);
        timer.Start();
        _gpuBenchActive = true;
        GbGpuNow.Text = "sensores pausados";
        bool overlayWasOn = _host.GetOverlay();
        if (overlayWasOn) _host.SetOverlay(false); // o overlay também consulta a GPU

        // Thread dedicada: a cena roda num loop próprio com prioridade alta.
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        if (overlayWasOn) _host.SetOverlay(true);
        GbGpuNow.Text = "—";
        SetGameBenchRunning(BtnGpuBench, false);
        GbProgress.ScaleX = 0;
        GbFpsNow.Text = "—";

        if (r == null)
        {
            GbStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        GbStatus.Text = $"Concluído · {r.Adapter}";
        GbResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Nota", $"{r.Score:N0} pts"),
            new KeyValuePair<string, string>("FPS médio", $"{r.AvgFps:0.0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0.0}"),
            new KeyValuePair<string, string>("Resolução", $"{r.Width}x{r.Height}"),
            new KeyValuePair<string, string>("Frames", $"{r.Frames:N0}"),
        };
        SaveBenchRecord("gpu", r.Score, $"{r.Score:N0} pts · {r.AvgFps:0} FPS · {r.Height}p");
    }

    // ----- Simulação de jogo (CPU) -----

    private async void BtnSimBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (IsRunning || _host.OtherBusy()) { SimStatus.Text = "Termine o outro teste antes."; return; }

        int secs = int.Parse(CheckedTag(SimDurPanel) ?? "20");
        _simBitmap ??= new System.Windows.Media.Imaging.WriteableBitmap(SimView, SimView, 96, 96, PixelFormats.Bgra32, null);
        if (_simPixels.Length == 0) _simPixels = new int[SimView * SimView];
        SimImage.Source = _simBitmap;
        SimIdle.Visibility = Visibility.Collapsed;

        var sim = new GameSimBench();
        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnSimBench, true);
        SimResult.ItemsSource = null;
        SimStatus.Text = "Aquecendo (2 s)…";
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) =>
        {
            DrawSim(sim);
            SimProgress.ScaleX = sim.Progress;
            if (sim.FramesDone > 0) SimStatus.Text = $"Simulando · {sim.Progress:P0}";
            SimFpsNow.Text = sim.CurrentFps > 0 ? $"{sim.CurrentFps:0} FPS" : "";
        }, Dispatcher);
        timer.Start();

        var r = await Task.Run(() => sim.Run(TimeSpan.FromSeconds(secs), ct));
        timer.Stop();
        bool cancelled = ct.IsCancellationRequested;
        SetGameBenchRunning(BtnSimBench, false);
        SimProgress.ScaleX = 0;
        SimFpsNow.Text = "";
        if (cancelled) { SimStatus.Text = "Cancelado"; return; }

        SimStatus.Text = $"Concluído · {r.Frames:N0} frames em {r.Seconds:0} s";
        SimResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("FPS médio (nota)", $"{r.AvgFps:0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0}"),
            new KeyValuePair<string, string>("Thread principal", $"{r.MainThreadMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Física + IA (paralelo)", $"{r.ParallelMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Unidades", $"{GameSimBench.Entities:N0}"),
        };
        SaveBenchRecord("sim", r.Score, $"{r.AvgFps:0} FPS · 1% low {r.Low1Fps:0}");
    }

    /// <summary>Vista de cima da batalha: time A em branco, time B em cinza.</summary>
    private void DrawSim(GameSimBench sim)
    {
        Array.Fill(_simPixels, unchecked((int)0xFF050505));
        float[] xs = sim.ViewX, ys = sim.ViewY; byte[] team = sim.ViewTeam;
        float k = SimView / GameSimBench.World;
        for (int i = 0; i < GameSimBench.Entities; i++)
        {
            int px = (int)(xs[i] * k), py = (int)(ys[i] * k);
            if ((uint)px < SimView && (uint)py < SimView)
                _simPixels[py * SimView + px] = team[i] == 0 ? unchecked((int)0xFFF5F5F5) : unchecked((int)0xFF5A5A5A);
        }
        _simBitmap!.WritePixels(new Int32Rect(0, 0, SimView, SimView), _simPixels, SimView * 4, 0);
    }

    // ----- Memória e carregamento -----

    private async void BtnMemBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (IsRunning || _host.OtherBusy()) { MemStatus.Text = "Termine o outro teste antes."; return; }

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnMemBench, true);
        var rows = new List<MemRow>();
        void Show() => MemResult.ItemsSource = rows.ToList();
        (string Phase, double Frac) progress = ("", 0);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) =>
        {
            MemStatus.Text = progress.Phase;
            MemProgress.ScaleX = progress.Frac;
        }, Dispatcher);
        timer.Start();

        try
        {
            progress = ("Medindo latência da RAM…", 0.05);
            double lat = await Task.Run(() => MemoryBench.MeasureLatencyNs(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Latência da RAM", $"{lat:0.0} ns", "Acesso aleatório. Menor é melhor; pesa muito em jogos competitivos."));
            Show();

            progress = ("Medindo banda da RAM…", 0.2);
            var bw = await Task.Run(() => MemoryBench.MeasureBandwidth(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Banda de leitura da RAM", $"{bw.Read:0.0} GB/s", "Todas as threads lendo em sequência."));
            rows.Add(new MemRow("Banda de cópia da RAM", $"{bw.Copy:0.0} GB/s", "Leitura + escrita."));
            Show();

            var load = await Task.Run(() => MemoryBench.MeasureLoading((phase, f) => progress = (phase, 0.35 + f * 0.6), ct));
            rows.Add(new MemRow($"SSD {load.Drive} · leitura sequencial", $"{load.SeqReadMBs:N0} MB/s", "Pacote de 512 MB, sem cache do Windows."));
            rows.Add(new MemRow("Assets pequenos", $"{load.FilesPerSec:N0} arq/s",
                                $"Mil arquivos de 64 KB com 8 threads ({load.SmallFilesMBs:N0} MB/s), como engines modernas."));
            rows.Add(new MemRow("Carregamento do mapa", $"{load.MapLoadSeconds:0.00} s", "Pacote + assets: quanto menor, mais rápido o jogo carrega."));
            Show();

            MemStatus.Text = "Concluído · arquivos de teste apagados";
            // Nota para a barra do histórico: mais rápido = maior (inverso da latência × velocidade de carga).
            SaveBenchRecord("mem", 1000 / lat + 10 / load.MapLoadSeconds,
                            $"{lat:0} ns · {bw.Read:0} GB/s · mapa {load.MapLoadSeconds:0.00} s");
        }
        catch (OperationCanceledException) { MemStatus.Text = "Cancelado (arquivos de teste apagados)"; }
        catch (Exception ex) { MemStatus.Text = $"Falhou: {ex.Message}"; }
        finally
        {
            timer.Stop();
            MemProgress.ScaleX = 0;
            SetGameBenchRunning(BtnMemBench, false);
        }
    }

    private GpuRtBench? _rtBench;

    private static string PresetName(string p) => p switch { "normal" => "Normal", "extrema" => "Extrema", _ => "Alta" };

    /// <summary>Histórico: cada barra compara com o melhor resultado do MESMO preset e resolução (só isso é comparável).</summary>
    private List<MeterItem> RtHistoryItems()
    {
        var list = _settings.BenchRecords.Where(r => r.Test.StartsWith("rt:")).OrderByDescending(r => r.Date).ToList();
        var best = list.GroupBy(r => r.Test).ToDictionary(g => g.Key, g => g.Max(r => r.Score));
        return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best[r.Test] > 0 ? r.Score / best[r.Test] : 0)).ToList();
    }

    private async void BtnRtBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _rtBench?.Cancel(); return; }
        if (IsRunning || _host.OtherBusy()) { RtStatus.Text = "Termine o outro teste antes."; return; }

        string adapterName = MainGpu?.Name ?? "";
        RtStatus.Text = "Verificando suporte a ray tracing…";
        string? why = await Task.Run(() => GpuRtBench.CheckSupport(adapterName));
        if (why != null) { RtStatus.Text = why; return; }

        var size = (CheckedTag(RtResPanel) ?? "2560x1440").Split('x').Select(int.Parse).ToArray();
        string preset = CheckedTag(RtPresetPanel) ?? "alta";
        bool quick = CheckedTag(RtModePanel) == "quick";
        _rtBench = new GpuRtBench
        {
            Width = size[0], Height = size[1], Preset = preset, AdapterName = adapterName, Passes = 3,
            Warmup = TimeSpan.FromSeconds(quick ? 5 : 20), PassDuration = TimeSpan.FromSeconds(quick ? 10 : 30),
        };
        var bench = _rtBench;
        SetGameBenchRunning(BtnRtBench, true);
        RtResult.ItemsSource = null;
        RtVerdict.Text = "";
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            RtFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            RtPhase.Text = bench.Phase.Length > 0 ? bench.Phase : "—";
            RtProgress.ScaleX = bench.Progress;
            RtStatus.Text = $"Rodando · {size[0]}x{size[1]} · {PresetName(preset)} · {bench.Progress:P0}";
        }, Dispatcher);
        timer.Start();

        // Igual à cena de jogo: sensores e overlay pausados (consultas ao driver geram engasgos no frametime).
        _gpuBenchActive = true;
        bool overlayWasOn = _host.GetOverlay();
        if (overlayWasOn) _host.SetOverlay(false);
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        if (overlayWasOn) _host.SetOverlay(true);
        SetGameBenchRunning(BtnRtBench, false);
        RtProgress.ScaleX = 0;
        RtFpsNow.Text = "—";
        RtPhase.Text = "—";

        if (r == null)
        {
            RtStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        string driver = await Task.Run(() => DriverVersionOf(r.Adapter));
        RtStatus.Text = $"Concluído · {r.Adapter}" + (driver.Length > 0 ? $" · driver {driver}" : "");
        RtVerdict.Text = r.Reliable
            ? $"✔ Resultado confiável: as 3 passadas variaram só {r.VariationPct:0.0}%."
            : $"⚠ Variação de {r.VariationPct:0.0}% entre as passadas (acima de 3%). Algo atrapalhou: programa em segundo plano, " +
              "temperatura ou limite de energia. Feche outros programas e rode de novo antes de comparar.";
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Nota", $"{r.Score:N0} pts"),
            new("FPS (mediana)", $"{r.MedianFps:0.0}"),
            new("1% low (mediana)", $"{r.MedianLow1:0.0}"),
            new("Variação entre passadas", $"{r.VariationPct:0.0}%"),
            new("Raios por segundo (estimado)", $"{r.GigaRaysPerSec:0.0} bilhões"),
            new("Preset · resolução", $"{PresetName(r.Preset)} · {r.Width}x{r.Height}"),
        };
        for (int i = 0; i < r.Passes.Count; i++)
            rows.Add(new($"Passada {i + 1}", $"{r.Passes[i].AvgFps:0.0} FPS · 1% low {r.Passes[i].Low1Fps:0.0}"));
        RtResult.ItemsSource = rows;
        SaveBenchRecord($"rt:{r.Preset}:{r.Height}",
                        r.Score, $"{r.Score:N0} pts · {r.MedianFps:0} FPS · {PresetName(r.Preset)} {r.Height}p" + (r.Reliable ? "" : " · instável"));
    }

    private static string DriverVersionOf(string adapter)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (System.Management.ManagementObject mo in s.Get())
                if (mo["Name"] is string n && adapter.Contains(n.Trim(), StringComparison.OrdinalIgnoreCase) || mo["Name"] is string n2 && n2.Contains(adapter, StringComparison.OrdinalIgnoreCase))
                    return mo["DriverVersion"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }
    // ================= Benchmark de disco =================

    private void InitDiskBench()
    {
        if (_gameBenchRunning) return; // não mexe nos botões durante um teste
        string? keep = CheckedTag(DiskDrivePanel);
        DiskDrivePanel.Children.Clear();
        var drives = DiskBench.Drives();
        if (drives.Count == 0)
        {
            DiskStatus.Text = "Nenhuma unidade com espaço livre suficiente (1,5 GB).";
            BtnDiskBench.IsEnabled = false;
            return;
        }
        BtnDiskBench.IsEnabled = true;
        string pick = keep != null && drives.Any(d => d.Root == keep) ? keep
                    : drives.FirstOrDefault(d => d.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase)).Root ?? drives[0].Root;
        foreach (var (root, text) in drives)
            DiskDrivePanel.Children.Add(new RadioButton
            {
                Content = text, Tag = root, GroupName = "DiskDrive",
                Style = (Style)FindResource("Chip"), IsChecked = root == pick,
            });
    }

    private async void BtnDiskBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (IsRunning || _host.OtherBusy()) { DiskStatus.Text = "Termine o outro teste antes."; return; }

        string root = CheckedTag(DiskDrivePanel) ?? "";
        if (root.Length == 0) { DiskStatus.Text = "Escolha uma unidade."; return; }
        long fileMb = long.Parse(CheckedTag(DiskSizePanel) ?? "1024");

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnDiskBench, true);
        DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = false;
        DiskResult.ItemsSource = null;
        (string Phase, double Frac) progress = ("Preparando…", 0);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) =>
        {
            DiskStatus.Text = progress.Phase;
            DiskProgress.ScaleX = progress.Frac;
        }, Dispatcher);
        timer.Start();

        try
        {
            // Os sensores consultam o driver do disco: pausa durante o teste para não atrapalhar a medição.
            _gpuBenchActive = true;
            var r = await Task.Run(() => DiskBench.Run(root, fileMb, (phase, f) => progress = (phase, f), ct), ct);
            ct.ThrowIfCancellationRequested();

            var rows = new List<MemRow>
            {
                new("Leitura sequencial", $"{r.SeqReadMBs:N0} MB/s", "Blocos de 1 MB. Mede copiar arquivos grandes e carregar jogos."),
                new("Escrita sequencial", $"{r.SeqWriteMBs:N0} MB/s", "Blocos de 1 MB, direto no disco. SSDs com cache SLC caem depois de gravar dezenas de GB."),
                new("Leitura 4 KB aleatória (1 por vez)", $"{r.ReadIopsQ1:N0} IOPS", $"Latência média de {r.ReadLatencyUs:0.0} µs. É o que define a sensação de rapidez do sistema e dos programas."),
                new("Escrita 4 KB aleatória (1 por vez)", $"{r.WriteIopsQ1:N0} IOPS", $"Latência média de {r.WriteLatencyUs:0.0} µs."),
                new("Leitura 4 KB aleatória (8 em paralelo)", $"{r.ReadIopsQ8:N0} IOPS", "Várias leituras ao mesmo tempo, como ao abrir muitos programas."),
            };
            DiskResult.ItemsSource = rows;
            DiskStatus.Text = $"Concluído em {r.Drive} · arquivo de teste ({r.FileMb} MB) apagado";
            SaveBenchRecord("disk", r.Score,
                $"{r.Drive} · {r.SeqReadMBs:N0}/{r.SeqWriteMBs:N0} MB/s · 4K {r.ReadIopsQ1 / 1000:0.#}k IOPS");
        }
        catch (OperationCanceledException) { DiskStatus.Text = "Cancelado (arquivo de teste apagado)"; }
        catch (Exception ex)
        {
            var inner = (ex as AggregateException)?.InnerException ?? ex;
            DiskStatus.Text = $"Falhou: {inner.Message}";
        }
        finally
        {
            _gpuBenchActive = false;
            timer.Stop();
            DiskProgress.ScaleX = 0;
            DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = true;
            SetGameBenchRunning(BtnDiskBench, false);
        }
    }

}
