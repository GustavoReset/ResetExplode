using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FortKnox;

/// <summary>Saúde dos discos, benchmark de disco (FORJA) e exportação de relatório.</summary>
public partial class MainWindow
{
    // ================= Saúde dos discos =================

    private bool _diskHealthBusy;

    private async void RefreshDiskHealth()
    {
        if (_diskHealthBusy) return;
        _diskHealthBusy = true;
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
            _diskHealthBusy = false;
            BtnDiskRefresh.IsEnabled = true;
        }
    }

    private void BtnDiskRefresh_Click(object sender, RoutedEventArgs e) => RefreshDiskHealth();

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
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        bool json = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

        BtnReport.IsEnabled = false;
        BtnReport.Content = "GERANDO…";
        try
        {
            // Dados que vivem na interface são copiados aqui (thread da tela); a leitura dos discos roda em segundo plano.
            var sensorsSnapshot = _monitor.Rows.ToList();
            var data = new ReportData
            {
                Date = DateTime.Now,
                Machine = Environment.MachineName,
                AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
                LogoBase64 = LoadLogoBase64(),
                System = (InfoCategories.ItemsSource as IEnumerable<InfoNode> ?? Enumerable.Empty<InfoNode>())
                         .Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList(),
                Gpus = _gpus.Select(g => new ReportGpu(g.Name, g.Subtitle,
                         g.Sections.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList())).ToList(),
                Sensors = sensorsSnapshot.Select(s => new ReportSensor(s.Hardware, s.Category, s.Name, s.Value, s.Min, s.Max)).ToList(),
                Benchmarks = BuildBenchList(),
            };
            var disks = await Task.Run(() => DiskHealth.Read(sensorsSnapshot));
            data.Disks.AddRange(disks.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            var power = await Task.Run(() => BatteryInfo.Read(sensorsSnapshot));
            data.Power.AddRange(power.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            if (_lastNet != null)
                data.Network.AddRange(_lastNet.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));

            string text = json ? ReportBuilder.Json(data) : ReportBuilder.Html(data);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true));

            SystemSub.Text = $"Relatório salvo em {path}";
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível gerar o relatório:\n" + ex.Message, "Fort Knox", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var list = _settings.BenchRecords.Select(r => new ReportBench(r.Date, TestName(r.Test), r.Summary)).ToList();
        foreach (var r in _settings.BenchRuns)
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
