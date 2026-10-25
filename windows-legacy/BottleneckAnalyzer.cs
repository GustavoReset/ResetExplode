using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

public enum Limiter { Gpu, Cpu, Vram, Ram, Thermal, Capped }

/// <summary>Uma leitura (1/s) de tudo que pode limitar o FPS.</summary>
public sealed record LoadSample(
    float? GpuLoad, float? CpuTotal, float? CpuMaxCore, string? BusiestCore,
    double? VramPct, float? RamPct, float? GpuTemp, float? CpuTemp, bool GpuThermalCap, double? Fps)
{
    /// <summary>
    /// Regras (na ordem): VRAM cheia → térmico → GPU ≥ 90% (normal em jogo pesado) → CPU/núcleo ≥ 85% com GPU sobrando →
    /// RAM cheia → nada no limite (trava de FPS, V-Sync ou menu).
    /// </summary>
    public Limiter Classify()
    {
        if (VramPct >= 97) return Limiter.Vram;
        if (GpuThermalCap || CpuTemp >= 95) return Limiter.Thermal;
        if (GpuLoad >= 90) return Limiter.Gpu;
        if (CpuMaxCore >= 85 || CpuTotal >= 85) return Limiter.Cpu;
        if (RamPct >= 92) return Limiter.Ram;
        return Limiter.Capped;
    }
}

public static class BottleneckAnalyzer
{
    /// <summary>Lê os sensores atuais (LibreHardwareMonitor + NVML) da GPU principal e da CPU.</summary>
    public static LoadSample Sample(SensorMonitor m, GpuInfo? gpu, FrameStats? frames)
    {
        var rows = m.Rows;
        float? gpuLoad = null, gpuTemp = null;
        double? vramPct = null;
        bool thermal = false;
        if (gpu != null)
        {
            gpuLoad = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Type == SensorType.Load && r.Name == "GPU Core")?.RawValue;
            var temps = rows.Where(r => r.Hardware == gpu.Name && r.Type == SensorType.Temperature).ToList();
            gpuTemp = (temps.FirstOrDefault(r => r.Name == "GPU Core") ?? temps.FirstOrDefault())?.RawValue;

            if (gpu.NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(gpu.NvmlHandle) is Nvml.Memory mem && mem.Total > 0)
                vramPct = 100.0 * mem.Used / mem.Total;
            else
            {
                var used = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Name == "GPU Memory Used")?.RawValue;
                var total = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Name == "GPU Memory Total")?.RawValue;
                if (used != null && total > 0) vramPct = 100.0 * used.Value / total.Value;
            }
            if (gpu.NvmlHandle != IntPtr.Zero && Nvml.ThrottleReasons(gpu.NvmlHandle) is ulong r)
                thermal = (r & 0x60) != 0; // SW/HW thermal slowdown
        }

        var cores = rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Load &&
                                    r.Name.StartsWith("CPU Core #") && !r.Name.Contains("Thread") && r.RawValue != null).ToList();
        var busiest = cores.OrderByDescending(r => r.RawValue).FirstOrDefault();
        float? ram = rows.FirstOrDefault(r => r.HardwareType == HardwareType.Memory && r.Type == SensorType.Load && r.Name == "Memory")?.RawValue;

        return new LoadSample(gpuLoad, m.CpuLoad, busiest?.RawValue, busiest?.Name.Replace("CPU Core #", "núcleo "),
                              vramPct, ram, gpuTemp, m.CpuTemperature, thermal, frames?.Fps);
    }

    public static string Title(Limiter l) => l switch
    {
        Limiter.Gpu => "GPU no limite",
        Limiter.Cpu => "Gargalo de CPU",
        Limiter.Vram => "Memória de vídeo cheia",
        Limiter.Ram => "Memória RAM cheia",
        Limiter.Thermal => "Limitado por temperatura",
        _ => "Nada no limite"
    };

    public static string Advice(Limiter l) => l switch
    {
        Limiter.Gpu => "Normal em jogos pesados: a placa de vídeo está sendo bem aproveitada. Para mais FPS, reduza " +
                       "resolução ou qualidade gráfica, ou ative DLSS/FSR.",
        Limiter.Cpu => "A CPU (ou um núcleo dela) não acompanha a GPU. Subir resolução/gráficos quase não custa FPS. " +
                       "Para ganhar FPS: reduza opções que pesam na CPU (distância de visão, multidões, física), feche " +
                       "programas em segundo plano e confira se o XMP/EXPO da RAM está ativo na BIOS.",
        Limiter.Vram => "A memória da placa de vídeo encheu, o que causa travadas. Reduza a qualidade das texturas " +
                        "ou a resolução.",
        Limiter.Ram => "Pouca RAM livre: o Windows passa a usar o disco e o jogo engasga. Feche navegadores e outros programas.",
        Limiter.Thermal => "Temperatura alta está derrubando o clock. Verifique ventilação do gabinete, poeira e pasta térmica.",
        _ => "Nem CPU nem GPU estão no limite: provavelmente há trava de FPS, V-Sync/G-Sync limitando, ou o jogo " +
             "está em menu/pausa. Normal se o FPS já é o desejado."
    };

    /// <summary>Equilíbrio teórico pelo desempenho relativo (GPU: RTX 4070 = 100; CPU: Ryzen 7 7700X = 100).</summary>
    public static (double Ratio, string Text) Balance(double gpuIdx, double cpuIdx)
    {
        double ratio = gpuIdx / cpuIdx;
        string text = ratio switch
        {
            > 1.8 => "GPU muito mais forte que a CPU: em boa parte dos jogos a CPU vai limitar o FPS, principalmente em 1080p.",
            > 1.3 => "GPU um pouco à frente da CPU: jogos competitivos em 1080p tendem a ficar limitados pela CPU; " +
                     "em 1440p/4K a GPU volta a ser o limite.",
            >= 0.7 => "Hardware equilibrado: na maioria dos jogos a GPU será o limite, que é o cenário ideal.",
            > 0.4 => "CPU mais forte que a GPU: a placa de vídeo é o limite. Upgrade de GPU traria o maior ganho.",
            _ => "GPU muito fraca para esta CPU: a placa de vídeo limita quase tudo."
        };
        return (ratio, text);
    }
}

/// <summary>Acumula amostras durante uma sessão de jogo e produz o relatório.</summary>
public sealed class BottleneckSession
{
    private readonly List<(LoadSample Sample, Limiter Limiter)> _samples = new();
    public DateTime Start { get; } = DateTime.Now;
    public TimeSpan Duration { get; init; }
    public int Count => _samples.Count;
    public bool Done => DateTime.Now - Start >= Duration;

    public void Add(LoadSample s) => _samples.Add((s, s.Classify()));

    public IReadOnlyList<(Limiter Limiter, double Pct)> Distribution() =>
        _samples.GroupBy(x => x.Limiter)
                .Select(g => (g.Key, 100.0 * g.Count() / Math.Max(1, _samples.Count)))
                .OrderByDescending(x => x.Item2).ToList();

    public Limiter? Main => _samples.Count == 0 ? null : Distribution()[0].Limiter;

    public double? AvgFps
    {
        get
        {
            var f = _samples.Where(x => x.Sample.Fps > 0).Select(x => x.Sample.Fps!.Value).ToList();
            return f.Count > 0 ? f.Average() : null;
        }
    }

    public double? Avg(Func<LoadSample, float?> sel)
    {
        var v = _samples.Select(x => sel(x.Sample)).Where(x => x != null).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    public double? Max(Func<LoadSample, double?> sel)
    {
        var v = _samples.Select(x => sel(x.Sample)).Where(x => x != null).Select(x => x!.Value).ToList();
        return v.Count > 0 ? v.Max() : null;
    }
}
