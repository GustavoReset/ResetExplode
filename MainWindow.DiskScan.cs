using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Teste de setores defeituosos (leitura apenas) na página de discos.</summary>
public partial class MainWindow
{
    private async void BtnDiskScan_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { DiskStatus.Text = "Termine o outro teste antes."; return; }

        string root = CheckedTag(DiskDrivePanel) ?? "";
        if (root.Length == 0) { DiskStatus.Text = "Escolha uma unidade."; return; }
        bool full = (CheckedTag(DiskScanPanel) ?? "quick") == "full";

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnDiskScan, true);
        BtnDiskBench.IsEnabled = false;
        DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = DiskScanPanel.IsEnabled = false;
        DiskResult.ItemsSource = null;
        (string Phase, double Frac) progress = ("Preparando…", 0);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) =>
        {
            DiskStatus.Text = progress.Phase;
            DiskProgress.ScaleX = progress.Frac;
        }, Dispatcher);
        timer.Start();

        try
        {
            _gpuBenchActive = true; // pausa a leitura dos sensores de disco durante a varredura
            var r = await Task.Run(() => DiskSurfaceScan.Run(root, full, (t, f) => progress = (t, f), ct), ct);

            string verdict = r.BadSectors > 0
                ? "DEFEITO: há setores que não puderam ser lidos. Faça backup agora e considere trocar o disco."
                : r.SlowReads > 5 ? "Atenção: várias leituras muito lentas. Confira a saúde (SMART) do disco."
                : "Nenhum setor ruim encontrado.";
            var rows = new List<MemRow>
            {
                new("Resultado", r.BadSectors > 0 ? "COM DEFEITO" : r.SlowReads > 5 ? "ATENÇÃO" : "OK", verdict),
                new("Setores ilegíveis (4 KB)", r.BadSectors.ToString("N0"), r.BadRanges.Count > 0
                    ? "Primeiros locais: " + string.Join(", ", r.BadRanges.Take(5).Select(b => $"{b.Offset / 1048576.0:N0} MB"))
                    : "Cada falha de leitura é um setor com problema."),
                new("Leituras lentas", r.SlowReads.ToString("N0"), "Blocos de 4 MB que demoraram mais de 0,7 s. Poucos são normais em HD."),
                new("Lido", $"{r.ScannedBytes / 1073741824.0:0.0} GB", full ? "Volume inteiro." : "Amostra de cerca de 10% do volume (modo rápido)."),
                new("Velocidade média", $"{r.AvgMBs:N0} MB/s", "Quedas fortes no meio da varredura indicam região com problema."),
            };
            DiskResult.ItemsSource = rows;
            DiskStatus.Text = (r.Cancelled ? "Cancelado" : "Concluído") + $" em {r.Drive} · {verdict}";
        }
        catch (OperationCanceledException) { DiskStatus.Text = "Cancelado."; }
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
            BtnDiskBench.IsEnabled = true;
            DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = DiskScanPanel.IsEnabled = true;
            SetGameBenchRunning(BtnDiskScan, false);
            BtnDiskScan.Content = "VERIFICAR";
        }
    }
}
