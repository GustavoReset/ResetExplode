using System.Windows;
using System.Windows.Controls;

namespace FortKnox;

/// <summary>SENTINELA · Discos (Fase 3.3l; antes em MainWindow.Disks.cs): saúde S.M.A.R.T. de cada disco físico.</summary>
public partial class DisksView : UserControl
{
    private SensorMonitor _monitor = null!;
    private bool _busy;

    public DisksView() => InitializeComponent();

    public void Attach(SensorMonitor monitor) => _monitor = monitor;

    public async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        BtnDiskRefresh.IsEnabled = false;
        DiskHealthStatus.Text = "Lendo os discos…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => DiskHealth.Read(sensors));
            DiskCards.ItemsSource = cards;
            int bad = cards.Count(c => c.Verdict is DiskVerdict.Warning or DiskVerdict.Critical);
            DiskHealthStatus.Text = cards.Count == 0
                ? "Nenhum disco encontrado."
                : bad == 0
                    ? $"{cards.Count} disco(s) · nenhum problema encontrado · lido às {DateTime.Now:HH:mm:ss}"
                    : $"{cards.Count} disco(s) · {bad} precisa(m) de atenção · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { DiskHealthStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _busy = false;
            BtnDiskRefresh.IsEnabled = true;
        }
    }

    private void BtnDiskRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
