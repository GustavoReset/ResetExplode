using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeterItem = FortKnox.MainWindow.MeterItem;

namespace FortKnox;

/// <summary>
/// ARENA · Gargalo (Fase 3.3j; antes no MainWindow.xaml.cs): equilíbrio teórico GPU × CPU, leitura ao vivo e análise de sessão.
/// A janela principal chama <see cref="Tick"/> a cada 1 s (ciclo dos sensores).
/// </summary>
public partial class BottleneckView : UserControl
{
    private LiveReadings _live = null!;
    private FrameMonitor _frames = null!;
    private BottleneckSession? _bnSession;
    private double _balanceRatio = 1;
    private bool _ready;

    public BottleneckView() => InitializeComponent();

    private SensorMonitor _monitor => _live.Monitor;
    private Brush Fg => (Brush)FindResource("Fg");
    private Brush Ink => (Brush)FindResource("Ink");
    private Brush Muted => (Brush)FindResource("Muted");
    private Brush Card => (Brush)FindResource("Card");

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    /// <summary>Chamado quando CPU e GPU foram detectadas (o FrameMonitor já foi iniciado pela janela principal).</summary>
    public void Init(LiveReadings live, FrameMonitor frames, string cpuName, string gpuName, double cpuIdx, double gpuIdx)
    {
        _live = live;
        _frames = frames;
        var (ratio, text) = BottleneckAnalyzer.Balance(gpuIdx, cpuIdx);
        _balanceRatio = ratio;
        BnCpuName.Text = $"CPU · {GameCatalog.ShortCpuName(cpuName)}";
        BnGpuName.Text = $"GPU · {GameCatalog.ShortGpuName(gpuName)}";
        BnBalanceText.Text = text;
        _ready = true;
        BnBalanceCanvas_SizeChanged(this, null!);
    }

    /// <summary>Uma amostra por segundo: leitura ao vivo (com a página aberta) e a sessão de análise (sempre que houver uma).</summary>
    public void Tick() { if (_ready) UpdateBottleneck(); }

    /// <summary>Ao fechar o app: descarta a sessão em andamento.</summary>
    public void CancelAll() => _bnSession = null;

    /// <summary>Posição do marcador: escala logarítmica da razão GPU/CPU (0,4× … 2,5×), centro = equilibrado.</summary>
    private void BnBalanceCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded && BnBalanceCanvas.ActualWidth <= 0) return;
        double w = BnBalanceCanvas.ActualWidth;
        double t = Math.Clamp((Math.Log(_balanceRatio) - Math.Log(0.4)) / (Math.Log(2.5) - Math.Log(0.4)), 0, 1);
        Canvas.SetLeft(BnBalanceDot, t * (w - BnBalanceDot.Width));
    }

    private static MeterItem Meter(string label, double? pct, string? suffix = null)
        => new(label, pct == null ? "—" : $"{pct:0} %{suffix}", Math.Clamp((pct ?? 0) / 100, 0, 1));

    private void UpdateBottleneck()
    {
        var frames = _frames.GetForegroundStats();
        var s = BottleneckAnalyzer.Sample(_monitor, _live.MainGpu, frames);
        var lim = s.Classify();

        if (IsVisible || _bnSession != null)
        {
            BnLiveTitle.Text = BottleneckAnalyzer.Title(lim);
            BnLiveAdvice.Text = BottleneckAnalyzer.Advice(lim);
            BnLiveGame.Text = frames != null
                ? $"{frames.ProcessName} · {frames.Fps:0} FPS · 1% low {frames.Low1Fps:0}"
                : "Nenhum jogo em primeiro plano (o FPS aparece quando o jogo estiver na tela)";
            bool alert = lim is Limiter.Cpu or Limiter.Vram or Limiter.Ram or Limiter.Thermal;
            BnLiveCard.Background = alert ? Fg : Card;
            BnLiveTitle.Foreground = alert ? Ink : Fg;
            BnLiveLabel.Foreground = BnLiveGame.Foreground = BnLiveAdvice.Foreground = alert ? Ink : Muted;

            BnMeters.ItemsSource = new[]
            {
                Meter("Carga da GPU", s.GpuLoad),
                Meter("Carga total da CPU", s.CpuTotal),
                Meter(s.BusiestCore != null ? $"Núcleo mais ocupado ({s.BusiestCore})" : "Núcleo mais ocupado", s.CpuMaxCore),
                Meter("VRAM em uso", s.VramPct),
                Meter("RAM em uso", s.RamPct),
                new MeterItem("Temperaturas (GPU / CPU)",
                              $"{(s.GpuTemp is float g ? $"{g:0} °C" : "—")} / {(s.CpuTemp is float c ? $"{c:0} °C" : "—")}",
                              Math.Clamp(Math.Max(s.GpuTemp ?? 0, s.CpuTemp ?? 0) / 100.0, 0, 1)),
            };
        }

        if (_bnSession != null)
        {
            _bnSession.Add(s);
            var el = DateTime.Now - _bnSession.Start;
            BnSessionStatus.Text = $"Coletando… {el:m\\:ss} de {_bnSession.Duration:m\\:ss} · {_bnSession.Count} amostras · " +
                                   (frames != null ? $"jogo detectado: {frames.ProcessName}" : "abra o jogo agora");
            if (_bnSession.Done) FinishBottleneckSession();
        }
    }

    private void BtnBnSession_Click(object sender, RoutedEventArgs e)
    {
        if (_bnSession != null)
        {
            _bnSession = null;
            BtnBnSession.Content = "INICIAR ANÁLISE";
            BtnBnSession.Style = (Style)FindResource("PrimaryButton");
            BnSessionStatus.Text = "Análise cancelada.";
            return;
        }
        int minutes = int.Parse(CheckedTag(BnDurationPanel) ?? "1");
        _bnSession = new BottleneckSession { Duration = TimeSpan.FromMinutes(minutes) };
        BnResult.Visibility = Visibility.Collapsed;
        BtnBnSession.Content = "CANCELAR";
        BtnBnSession.Style = (Style)FindResource("OutlineButton");
        BnSessionStatus.Text = "Coletando… abra o jogo agora.";
    }

    private void FinishBottleneckSession()
    {
        var sess = _bnSession!;
        _bnSession = null;
        BtnBnSession.Content = "INICIAR ANÁLISE";
        BtnBnSession.Style = (Style)FindResource("PrimaryButton");
        BnSessionStatus.Text = $"Análise concluída às {DateTime.Now:HH:mm} ({sess.Count} amostras).";

        var main = sess.Main ?? Limiter.Capped;
        BnResultTitle.Text = $"Resultado: {BottleneckAnalyzer.Title(main)}";
        var parts = new List<string>();
        if (sess.AvgFps is double fps) parts.Add($"FPS médio {fps:0}");
        if (sess.Avg(x => x.GpuLoad) is double g) parts.Add($"GPU média {g:0} %");
        if (sess.Avg(x => x.CpuTotal) is double c) parts.Add($"CPU média {c:0} %");
        if (sess.Avg(x => x.CpuMaxCore) is double k) parts.Add($"núcleo mais ocupado {k:0} %");
        if (sess.Max(x => x.VramPct) is double v) parts.Add($"VRAM máx. {v:0} %");
        if (sess.Max(x => x.GpuTemp) is double gt) parts.Add($"GPU máx. {gt:0} °C");
        BnResultSummary.Text = string.Join(" · ", parts);
        BnDistribution.ItemsSource = sess.Distribution()
            .Select(d => new MeterItem($"{BottleneckAnalyzer.Title(d.Limiter)} — % do tempo", $"{d.Pct:0} %", d.Pct / 100)).ToList();
        BnResultAdvice.Text = BottleneckAnalyzer.Advice(main);
        BnResult.Visibility = Visibility.Visible;
    }

}
