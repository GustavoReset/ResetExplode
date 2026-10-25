using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Saúde dos discos, benchmark de disco (RESET BENCH) e exportação de relatório.</summary>
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
        if (AnyBenchRunning || TestRunning) { DiskStatus.Text = "Termine o outro teste antes."; return; }

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

    // ================= Relatório exportável =================

    private async void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar relatório",
            Filter = "Relatório HTML (*.html)|*.html|Dados JSON (*.json)|*.json",
            DefaultExt = ".html",
            FileName = $"RESET-EXPLODE-relatorio-{DateTime.Now:yyyyMMdd-HHmm}",
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
            MessageBox.Show(this, "Não foi possível gerar o relatório:\n" + ex.Message, "RESET EXPLODE", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            "disk" => "Disco", "oc" => "RESET OC (estabilidade)", _ => t
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
