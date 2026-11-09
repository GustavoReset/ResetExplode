using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FortKnox;

/// <summary>SENTINELA · Placa de vídeo (Fase 3.3l; antes no MainWindow.xaml.cs): ficha técnica estilo GPU-Z e sensores ao vivo.</summary>
public partial class GpuView : UserControl
{
    private SensorMonitor _monitor = null!;
    private GpuInfo? _gpu;

    public GpuView() => InitializeComponent();

    public void Attach(SensorMonitor monitor) => _monitor = monitor;

    /// <summary>Depois que as GPUs são detectadas: um chip por placa (se houver mais de uma) e a primeira na tela.</summary>
    public void ShowGpus(IReadOnlyList<GpuInfo> gpus)
    {
        GpuChips.Children.Clear();
        for (int i = 0; i < gpus.Count && gpus.Count > 1; i++)
        {
            var g = gpus[i];
            var chip = new RadioButton { Content = g.Name, GroupName = "Gpu", Style = (Style)FindResource("Chip"), IsChecked = i == 0 };
            chip.Checked += (_, _) => ShowGpu(g);
            GpuChips.Children.Add(chip);
        }
        if (gpus.Count > 0) ShowGpu(gpus[0]);
    }

    /// <summary>A cada 1 s com a página aberta: P-state, PerfCap, NVENC/NVDEC.</summary>
    public void Tick() { if (_gpu != null && IsVisible) GpuExtras.ItemsSource = _gpu.LiveExtras(); }

    private void ShowGpu(GpuInfo g)
    {
        _gpu = g;
        GpuName.Text = g.Name;
        GpuSubtitle.Text = g.Subtitle;
        GpuVendor.Text = g.Vendor;
        GpuSections.ItemsSource = g.Sections;

        // Sensores do LibreHardwareMonitor deste hardware, em ordem fixa (clock, temperatura, carga...).
        var live = new ListCollectionView(_monitor.Rows)
        {
            Filter = o => ((SensorRow)o).Hardware == g.Name
        };
        live.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Order), ListSortDirection.Ascending));
        live.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Name), ListSortDirection.Ascending));
        GpuLive.ItemsSource = live;
        GpuExtras.ItemsSource = g.LiveExtras();
    }
}
