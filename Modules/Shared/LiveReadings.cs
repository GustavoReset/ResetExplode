using LibreHardwareMonitor.Hardware;

namespace FortKnox;

/// <summary>
/// Leituras ao vivo da GPU principal e da CPU, compartilhadas por PAINEL, VIGIA e FORJA (antes eram métodos do MainWindow).
/// NVML quando a GPU é NVIDIA e responde; senão, LibreHardwareMonitor.
/// </summary>
public sealed class LiveReadings(SensorMonitor monitor, Func<GpuInfo?> mainGpu)
{
    public SensorMonitor Monitor => monitor;
    public GpuInfo? MainGpu => mainGpu();

    public SensorRow? GpuRow(SensorType type, string name, bool startsWith = false) =>
        mainGpu() is not { } g ? null : monitor.Rows.FirstOrDefault(r => r.Hardware == g.Name && r.Type == type &&
                                                                       (startsWith ? r.Name.StartsWith(name) : r.Name == name) && r.RawValue != null);

    public uint? MainGpuClock(int nvmlType, string lhmName)
    {
        if (mainGpu() is { NvmlHandle: var h } && h != IntPtr.Zero && Nvml.Clock(h, nvmlType) is uint c) return c;
        return GpuRow(SensorType.Clock, lhmName)?.RawValue is float v && v > 0 ? (uint)v : null;
    }

    /// <summary>VRAM usada e total em GB: NVML quando disponível, senão LibreHardwareMonitor (MB).</summary>
    public (double Used, double Total)? MainGpuVram()
    {
        if (mainGpu() is not { } g) return null;
        if (g.NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(g.NvmlHandle) is Nvml.Memory m && m.Total > 0)
            return (m.Used / 1073741824.0, m.Total / 1073741824.0);
        if (GpuRow(SensorType.SmallData, "GPU Memory Used")?.RawValue is float u && GpuRow(SensorType.SmallData, "GPU Memory Total")?.RawValue is float t && t > 0)
            return (u / 1024.0, t / 1024.0);
        return null;
    }

    public float? MainGpuPower()
    {
        if (mainGpu() is not { } g) return null;
        if (g.NvmlHandle != IntPtr.Zero && Nvml.PowerUsageMw(g.NvmlHandle) is uint mw) return mw / 1000f;
        return monitor.Rows.FirstOrDefault(r => r.Hardware == g.Name && r.Type == SensorType.Power)?.RawValue;
    }

    /// <summary>Temperatura do núcleo da GPU principal (dedicada).</summary>
    public float? MainGpuTemp()
    {
        if (mainGpu() is not { } g) return null;
        var temps = monitor.Rows.Where(r => r.Hardware == g.Name && r.Type == SensorType.Temperature).ToList();
        return (temps.FirstOrDefault(r => r.Name == "GPU Core") ?? temps.FirstOrDefault())?.RawValue;
    }

    public string MainGpuPerfCap()
        => mainGpu()?.LiveExtras().FirstOrDefault(kv => kv.Key.StartsWith("PerfCap")).Value ?? "";

    public (float? Avg, float? Max) CpuClocks()
    {
        var cores = monitor.Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Clock &&
                                            r.Name.StartsWith("Core #") && !r.Name.Contains("Effective") && r.RawValue > 0)
                                .Select(r => r.RawValue!.Value).ToList();
        return cores.Count > 0 ? (cores.Average(), cores.Max()) : (null, null);
    }
}
