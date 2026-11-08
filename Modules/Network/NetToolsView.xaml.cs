using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FortKnox;

public sealed record HopRow(int Ttl, string Name, string AvgText, string LossText, double BarWidth);
public sealed record UsageRow(string Name, int Pid, string Down, string Up, string TotalDown, string TotalUp);

/// <summary>
/// Traceroute e consumo por processo, dentro da página Rede. Só cuida de tela: o trabalho está em TraceRoute (Task, cancelável)
/// e ProcessBandwidthMonitor (ETW em thread própria, lido por um temporizador de 1 s só enquanto o monitoramento está ligado).
/// </summary>
public partial class NetToolsView : UserControl
{
    private const double BarMax = 160;
    private const int MaxUsageRows = 30;

    private CancellationTokenSource? _traceCts;
    private ProcessBandwidthMonitor? _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public NetToolsView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshUsage();
    }

    // ------------------------------------------------------------------ Ciclo de vida (MainWindow.Net.cs)

    /// <summary>Para tudo que roda em segundo plano: rastreio em andamento e monitor de rede (sessão ETW).</summary>
    public void StopAll()
    {
        _traceCts?.Cancel();
        StopMonitor();
    }

    // ------------------------------------------------------------------ Traceroute

    private void NHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _traceCts == null) NTrace_Click(sender, e);
    }

    private async void NTrace_Click(object sender, RoutedEventArgs e)
    {
        if (_traceCts != null) { NTraceStatus.Text = "Cancelando…"; _traceCts.Cancel(); return; }

        string host = NHost.Text.Trim();
        _traceCts = new CancellationTokenSource();
        var ct = _traceCts.Token;
        NBtnTrace.Content = "CANCELAR";
        NHops.ItemsSource = null;
        NFindings.ItemsSource = null;
        NTraceStatus.Text = $"Rastreando {host}…";
        var progress = new Progress<IReadOnlyList<TraceHop>>(ShowHops); // criado na thread da interface: callbacks chegam nela
        try
        {
            var r = await TraceRoute.RunAsync(host, resolveNames: NResolve.IsChecked == true, progress: progress, ct: ct);
            ShowHops(r.Hops);
            NFindings.ItemsSource = TraceRoute.Analyze(r);
            NTraceStatus.Text = (r.Reached ? "Chegou ao destino" : "Não chegou ao destino") + $" · {r.Target} ({r.TargetAddress}) · {r.Hops.Count} salto(s) · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { NTraceStatus.Text = "Rastreio cancelado."; }
        catch (ArgumentException ex) { NTraceStatus.Text = ex.Message; }
        catch (Exception ex) { NTraceStatus.Text = "Erro inesperado no rastreio: " + ex.Message; } // async void: nada pode escapar daqui
        finally
        {
            _traceCts?.Dispose();
            _traceCts = null;
            NBtnTrace.Content = "RASTREAR";
        }
    }

    private void ShowHops(IReadOnlyList<TraceHop> hops)
    {
        double max = hops.Where(h => h.Responded).Select(h => h.Avg).DefaultIfEmpty(1).Max();
        NHops.ItemsSource = hops.Select(h => new HopRow(
            h.Ttl, h.Display,
            h.Responded ? $"{h.Avg:0.#} ms" : "—",
            h.Responded ? (h.LossPct > 0 ? $"{h.LossPct:0}% perda" : "") : "100%",
            h.Responded ? Math.Max(3, BarMax * h.Avg / Math.Max(max, 1)) : 0)).ToList();
    }

    // ------------------------------------------------------------------ Consumo por processo

    private void NMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled) { StopMonitor(); NMonitorStatus.Text = "Monitoramento parado."; return; }

        _monitor ??= new ProcessBandwidthMonitor();
        _monitor.Start();
        if (_monitor.Error != null)
        {
            NMonitorStatus.Text = _monitor.Error;
            _monitor.Dispose();
            _monitor = null;
            return;
        }
        NUsageGrid.ItemsSource = null;
        NBtnMonitor.Content = "PARAR";
        NMonitorStatus.Text = "Monitorando… aguarde alguns segundos e use a internet normalmente.";
        _timer.Start();
    }

    private void StopMonitor()
    {
        _timer.Stop();
        _monitor?.Dispose(); // encerra a sessão ETW no Windows
        _monitor = null;
        NBtnMonitor.Content = "INICIAR MONITORAMENTO";
    }

    private void RefreshUsage()
    {
        var m = _monitor;
        if (m == null) return;
        if (!m.Running)
        {
            // a thread do ETW terminou sozinha (sessão derrubada por outro programa, por exemplo)
            string why = m.Error ?? "a sessão de monitoramento foi encerrada pelo Windows";
            StopMonitor();
            NMonitorStatus.Text = "Monitoramento interrompido: " + why;
            return;
        }
        var rows = m.Snapshot().Take(MaxUsageRows)
            .Select(u => new UsageRow(u.Name, u.Pid, Rate(u.DownBps), Rate(u.UpBps), Bytes(u.TotalDown), Bytes(u.TotalUp))).ToList();
        NUsageGrid.ItemsSource = rows;
        NMonitorStatus.Text = rows.Count == 0 ? "Monitorando… nenhum tráfego detectado ainda." : $"Monitorando · {rows.Count} programa(s) com tráfego · {DateTime.Now:HH:mm:ss}";
    }

    internal static string Rate(double bytesPerSec) => bytesPerSec < 1 ? "—" : Bytes((long)bytesPerSec) + "/s";

    internal static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };
}
