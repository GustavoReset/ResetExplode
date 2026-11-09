using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FortKnox.UI;

namespace FortKnox;

/// <summary>
/// SENTINELA · Sistema (Fase 3.3m; antes no MainWindow.xaml.cs e MainWindow.Disks.cs): informações do sistema por categoria,
/// cartões das unidades (C:, pendrive, HD externo, rede) e o botão EXPORTAR RELATÓRIO (Laudo Fort Knox em HTML ou JSON).
/// </summary>
public partial class SystemView : UserControl
{
    /// <summary>Dados que o relatório junta de outras partes do app.</summary>
    public sealed class Host
    {
        public required SensorMonitor Monitor { get; init; }
        public required Func<IReadOnlyList<GpuInfo>> Gpus { get; init; }
        public required AppSettings Settings { get; init; }
        /// <summary>Último teste de conexão do RADAR (null se não rodou nesta sessão).</summary>
        public required Func<IReadOnlyList<DiskHealthCard>?> LastNetwork { get; init; }
    }

    private Host _host = null!;

    public SystemView() => InitializeComponent();

    public void Attach(Host host) => _host = host;

    /// <summary>Unidades lidas por último (o PAINEL mostra o disco C:; os avisos de USB comparam antes e depois).</summary>
    public List<DriveCard> Drives { get; private set; } = new();

    public string Subtitle { get => SystemSub.Text; set => SystemSub.Text = value; }

    /// <summary>Categorias lidas do WMI na abertura do app (em segundo plano).</summary>
    public void SetInfo(List<InfoNode> info)
    {
        InfoCategories.ItemsSource = info;
        InfoCategories.SelectedIndex = 0;
        SystemSub.Text = $"{Environment.MachineName} · {info.Count} categorias";
    }

    private void InfoCategories_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InfoCategories.SelectedItem is not InfoNode node) return;
        InfoTitle.Text = node.Header;
        InfoDetails.ItemsSource = node.Children;
        bool storage = node.Header == "Armazenamento";
        DrivesPanel.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
        if (storage) RefreshDrives();
    }

    public async void RefreshDrives() => ShowDrives(await Task.Run(DriveMap.Read));

    private void ShowDrives(List<DriveCard> drives)
    {
        Drives = drives;
        DriveCards.ItemsSource = drives;
        long total = drives.Sum(d => d.Total), free = drives.Sum(d => d.Free);
        DrivesSummary.Text = $"{drives.Count} unidades · {DriveCard.Fmt(free)} livres de {DriveCard.Fmt(total)}";
    }

    /// <summary>Pendrive ou HD externo entrou/saiu: guarda as unidades novas e relê os discos físicos da categoria Armazenamento.</summary>
    public async Task OnVolumesChangedAsync(List<DriveCard> now)
    {
        Drives = now;
        if (InfoCategories.ItemsSource is IEnumerable<InfoNode> cats && cats.FirstOrDefault(c => c.Header == "Armazenamento") is { } storage)
        {
            await Task.Run(() => SystemInfoCollector.RefreshDisks(storage));
            if (InfoCategories.SelectedItem == storage) { InfoDetails.ItemsSource = null; InfoDetails.ItemsSource = storage.Children; }
        }
        if (DrivesPanel.IsVisible) ShowDrives(now);
    }

    // ================= Relatório exportável =================

    private async void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar relatório",
            Filter = "Relatório HTML (*.html)|*.html|Dados JSON (*.json)|*.json",
            DefaultExt = ".html",
            FileName = $"Fort-Knox-relatorio-{DateTime.Now:yyyyMMdd-HHmm}",
        };
        var owner = Window.GetWindow(this);
        if (dlg.ShowDialog(owner) != true) return;
        string path = dlg.FileName;
        bool json = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

        BtnReport.IsEnabled = false;
        BtnReport.Content = "GERANDO…";
        try
        {
            // Dados que vivem na interface são copiados aqui (thread da tela); a leitura dos discos roda em segundo plano.
            var sensorsSnapshot = _host.Monitor.Rows.ToList();
            var data = new ReportData
            {
                Date = DateTime.Now,
                Machine = Environment.MachineName,
                AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
                LogoBase64 = LoadLogoBase64(),
                System = (InfoCategories.ItemsSource as IEnumerable<InfoNode> ?? Enumerable.Empty<InfoNode>())
                         .Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList(),
                Gpus = _host.Gpus().Select(g => new ReportGpu(g.Name, g.Subtitle,
                         g.Sections.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList())).ToList(),
                Sensors = sensorsSnapshot.Select(s => new ReportSensor(s.Hardware, s.Category, s.Name, s.Value, s.Min, s.Max)).ToList(),
                Benchmarks = BuildBenchList(),
            };
            var disks = await Task.Run(() => DiskHealth.Read(sensorsSnapshot));
            data.Disks.AddRange(disks.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            var power = await Task.Run(() => BatteryInfo.Read(sensorsSnapshot));
            data.Power.AddRange(power.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            if (_host.LastNetwork() is { } net)
                data.Network.AddRange(net.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));

            string text = json ? ReportBuilder.Json(data) : ReportBuilder.Html(data);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true));

            SystemSub.Text = $"Relatório salvo em {path}";
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            FkDialog.Info(owner, "Não foi possível gerar o relatório", ex.Message);
        }
        finally
        {
            BtnReport.Content = "EXPORTAR RELATÓRIO";
            BtnReport.IsEnabled = true;
        }
    }

    private List<ReportBench> BuildBenchList()
    {
        string TestName(string t) => t switch
        {
            "gpu" => "Cena de jogo (GPU)", "sim" => "Simulação de jogo (CPU)", "mem" => "Memória e carregamento",
            "disk" => "Disco", "oc" => "Forja · overclock (estabilidade)", _ => t
        };
        var s = _host.Settings;
        var list = s.BenchRecords.Select(r => new ReportBench(r.Date, TestName(r.Test), r.Summary)).ToList();
        foreach (var r in s.BenchRuns)
        {
            var parts = new List<string>();
            if (r.Multi is double mt) parts.Add($"multi-core {mt:N0} pts");
            if (r.Single is double st) parts.Add($"single-core {st:N0} pts");
            if (r.Sustained) parts.Add("sustentado");
            list.Add(new ReportBench(r.Date, "Render (CPU)", string.Join(" · ", parts) + (string.IsNullOrEmpty(r.Cpu) ? "" : $" · {r.Cpu}")));
        }
        return list.OrderByDescending(b => b.Date).ToList();
    }

    private static string? LoadLogoBase64()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo-badge.png"));
            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}
