using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>RESET BENCH · Ray tracing (DXR 1.1): aquecimento + 3 passadas, nota pela mediana e variação entre passadas.</summary>
public partial class MainWindow
{
    private GpuRtBench? _rtBench;

    private static string PresetName(string p) => p switch { "normal" => "Normal", "extrema" => "Extrema", _ => "Alta" };

    /// <summary>Histórico: cada barra compara com o melhor resultado do MESMO preset e resolução (só isso é comparável).</summary>
    private List<MeterItem> RtHistoryItems()
    {
        var list = _settings.BenchRecords.Where(r => r.Test.StartsWith("rt:")).OrderByDescending(r => r.Date).ToList();
        var best = list.GroupBy(r => r.Test).ToDictionary(g => g.Key, g => g.Max(r => r.Score));
        return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best[r.Test] > 0 ? r.Score / best[r.Test] : 0)).ToList();
    }

    private async void BtnRtBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _rtBench?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { RtStatus.Text = "Termine o outro teste antes."; return; }

        string adapterName = _gpus.Count > 0 ? _gpus[0].Name : "";
        RtStatus.Text = "Verificando suporte a ray tracing…";
        string? why = await Task.Run(() => GpuRtBench.CheckSupport(adapterName));
        if (why != null) { RtStatus.Text = why; return; }

        var size = (CheckedTag(RtResPanel) ?? "2560x1440").Split('x').Select(int.Parse).ToArray();
        string preset = CheckedTag(RtPresetPanel) ?? "alta";
        bool quick = CheckedTag(RtModePanel) == "quick";
        _rtBench = new GpuRtBench
        {
            Width = size[0], Height = size[1], Preset = preset, AdapterName = adapterName, Passes = 3,
            Warmup = TimeSpan.FromSeconds(quick ? 5 : 20), PassDuration = TimeSpan.FromSeconds(quick ? 10 : 30),
        };
        var bench = _rtBench;
        SetGameBenchRunning(BtnRtBench, true);
        RtResult.ItemsSource = null;
        RtVerdict.Text = "";
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            RtFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            RtPhase.Text = bench.Phase.Length > 0 ? bench.Phase : "—";
            RtProgress.ScaleX = bench.Progress;
            RtStatus.Text = $"Rodando · {size[0]}x{size[1]} · {PresetName(preset)} · {bench.Progress:P0}";
        }, Dispatcher);
        timer.Start();

        // Igual à cena de jogo: sensores e overlay pausados (consultas ao driver geram engasgos no frametime).
        _gpuBenchActive = true;
        bool overlayWasOn = ChkOverlay.IsChecked == true;
        if (overlayWasOn) ChkOverlay.IsChecked = false;
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        if (overlayWasOn) ChkOverlay.IsChecked = true;
        SetGameBenchRunning(BtnRtBench, false);
        RtProgress.ScaleX = 0;
        RtFpsNow.Text = "—";
        RtPhase.Text = "—";

        if (r == null)
        {
            RtStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        string driver = await Task.Run(() => DriverVersionOf(r.Adapter));
        RtStatus.Text = $"Concluído · {r.Adapter}" + (driver.Length > 0 ? $" · driver {driver}" : "");
        RtVerdict.Text = r.Reliable
            ? $"✔ Resultado confiável: as 3 passadas variaram só {r.VariationPct:0.0}%."
            : $"⚠ Variação de {r.VariationPct:0.0}% entre as passadas (acima de 3%). Algo atrapalhou: programa em segundo plano, " +
              "temperatura ou limite de energia. Feche outros programas e rode de novo antes de comparar.";
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Nota", $"{r.Score:N0} pts"),
            new("FPS (mediana)", $"{r.MedianFps:0.0}"),
            new("1% low (mediana)", $"{r.MedianLow1:0.0}"),
            new("Variação entre passadas", $"{r.VariationPct:0.0}%"),
            new("Raios por segundo (estimado)", $"{r.GigaRaysPerSec:0.0} bilhões"),
            new("Preset · resolução", $"{PresetName(r.Preset)} · {r.Width}x{r.Height}"),
        };
        for (int i = 0; i < r.Passes.Count; i++)
            rows.Add(new($"Passada {i + 1}", $"{r.Passes[i].AvgFps:0.0} FPS · 1% low {r.Passes[i].Low1Fps:0.0}"));
        RtResult.ItemsSource = rows;
        SaveBenchRecord($"rt:{r.Preset}:{r.Height}",
                        r.Score, $"{r.Score:N0} pts · {r.MedianFps:0} FPS · {PresetName(r.Preset)} {r.Height}p" + (r.Reliable ? "" : " · instável"));
    }

    private static string DriverVersionOf(string adapter)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (System.Management.ManagementObject mo in s.Get())
                if (mo["Name"] is string n && adapter.Contains(n.Trim(), StringComparison.OrdinalIgnoreCase) || mo["Name"] is string n2 && n2.Contains(adapter, StringComparison.OrdinalIgnoreCase))
                    return mo["DriverVersion"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }
}
