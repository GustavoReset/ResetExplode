using System.Windows;
using System.Windows.Controls;

namespace FortKnox;

/// <summary>
/// RADAR · Rede (Fase 3.3m; o teste clássico antes ficava em MainWindow.Net.cs): diagnóstico de lag em ~30 s
/// (latência, jitter, perda, DNS, velocidade e bufferbloat) e, abaixo, as ferramentas da Fase 3 (traceroute e consumo por processo).
/// </summary>
public partial class RadarView : UserControl
{
    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private Func<bool> _otherBusy = () => false;

    public RadarView() => InitializeComponent();

    /// <summary>otherBusy = benchmark ou Stress test rodando (o teste de rede espera para não medir com o PC ocupado).</summary>
    public void Attach(Func<bool> otherBusy) => _otherBusy = otherBusy;

    /// <summary>Traceroute e consumo por processo (o RadarModule para o ETW ao sair da página).</summary>
    public NetToolsView Tools => NetTools;

    /// <summary>Último resultado do teste de conexão (entra no relatório exportável).</summary>
    public IReadOnlyList<DiskHealthCard>? LastResult { get; private set; }

    /// <summary>Ao fechar o app: cancela o teste em andamento.</summary>
    public void CancelAll() { try { _netCts?.Cancel(); } catch (ObjectDisposedException) { } }

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (_otherBusy()) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            LastResult = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + ex.Message; NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }
}
