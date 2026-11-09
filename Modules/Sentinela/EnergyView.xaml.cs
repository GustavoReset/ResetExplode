using System.Windows;
using System.Windows.Controls;

namespace FortKnox;

/// <summary>SENTINELA · Energia (Fase 3.3l; antes em MainWindow.Net.cs): bateria de notebook, fonte de energia e plano do Windows.</summary>
public partial class EnergyView : UserControl
{
    private SensorMonitor _monitor = null!;
    private bool _busy;

    public EnergyView() => InitializeComponent();

    public void Attach(SensorMonitor monitor) => _monitor = monitor;

    public async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _busy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
