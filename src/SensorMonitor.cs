using System.Collections.ObjectModel;
using System.Globalization;

namespace ResetExplode;

/// <summary>Sensores do Linux: hwmon (temperatura, ventoinha, tensão, energia), cpufreq, /proc/stat, RAPL e nvidia-smi.</summary>
public sealed class SensorMonitor
{
    public ObservableCollection<SensorRow> Rows { get; } = new();
    private readonly List<(SensorRow Row, Func<float?> Read)> _readers = new();
    private long[]? _prevCpu;
    private float? _cpuLoad;
    private (long Uj, DateTime T)? _prevRapl;
    private float? _cpuPower;
    private Dictionary<string, float> _nv = new();
    private readonly bool _hasNvidia = Sys.HasCommand("nvidia-smi");

    public float? CpuLoad => _cpuLoad;
    public float? CpuPower => _cpuPower;
    public float? CpuTemp => Rows.Where(r => r.Type == SensorType.Temperature && (r.Hardware.Contains("k10temp") || r.Hardware.Contains("coretemp") || r.Hardware.Contains("zenpower")))
        .Select(r => r.RawValue).Where(v => v != null).DefaultIfEmpty(null).Max();

    public SensorMonitor() => Discover();

    private void Add(string id, string hw, string name, SensorType t, Func<float?> read)
    {
        var row = new SensorRow { Id = id, Hardware = hw, Name = name, Type = t };
        Rows.Add(row);
        _readers.Add((row, read));
    }

    private static float? F(string path, double scale)
        => Sys.ReadLong(path) is { } v ? (float)(v * scale) : null;

    private void Discover()
    {
        Add("cpu/load", "CPU", "Carga total", SensorType.Load, () => _cpuLoad);
        Add("mem/load", "Memória", "Uso de RAM", SensorType.Load, () =>
        {
            var mi = Sys.KeyValues(Sys.Read("/proc/meminfo"));
            long G(string k) => mi.TryGetValue(k, out var v) && long.TryParse(v.Split(' ')[0], out var x) ? x : 0;
            long tot = G("MemTotal");
            return tot == 0 ? null : 100f * (tot - G("MemAvailable")) / tot;
        });
        int cores = Environment.ProcessorCount;
        for (int i = 0; i < cores; i++)
        {
            int c = i;
            string p = $"/sys/devices/system/cpu/cpu{c}/cpufreq/scaling_cur_freq";
            if (File.Exists(p)) Add($"cpu/clock/{c}", "CPU", $"Clock thread {c}", SensorType.Clock, () => F(p, 0.001));
        }

        foreach (var hw in Directory.Exists("/sys/class/hwmon") ? Directory.GetDirectories("/sys/class/hwmon") : Array.Empty<string>())
        {
            string chip = Sys.Read(hw + "/name") ?? Path.GetFileName(hw);
            foreach (var f in Directory.GetFiles(hw, "*_input").OrderBy(x => x))
            {
                string file = Path.GetFileName(f), stem = file[..^6];
                string label = Sys.Read($"{hw}/{stem}_label") ?? stem;
                (SensorType t, double k)? m = stem switch
                {
                    _ when stem.StartsWith("temp") => (SensorType.Temperature, 0.001),
                    _ when stem.StartsWith("fan") => (SensorType.Fan, 1),
                    _ when stem.StartsWith("in") => (SensorType.Voltage, 0.001),
                    _ when stem.StartsWith("power") => (SensorType.Power, 1e-6),
                    _ when stem.StartsWith("curr") => (SensorType.Current, 0.001),
                    _ => null
                };
                if (m == null) continue;
                string path = f; double scale = m.Value.k;
                Add($"hwmon/{Path.GetFileName(hw)}/{stem}", chip, label, m.Value.t, () => F(path, scale));
            }
        }

        if (Directory.Exists("/sys/class/powercap"))
            foreach (var d in Directory.GetDirectories("/sys/class/powercap", "intel-rapl:?"))
            {
                string e = d + "/energy_uj";
                if (!File.Exists(e)) continue;
                Add("rapl/" + Path.GetFileName(d), "CPU", "Consumo do pacote (RAPL)", SensorType.Power, () => _cpuPower);
                break;
            }

        if (_hasNvidia)
            foreach (var (key, name, t) in new[]
            {
                ("temperature.gpu", "Temperatura", SensorType.Temperature), ("utilization.gpu", "Carga", SensorType.Load),
                ("clocks.sm", "Clock SM", SensorType.Clock), ("clocks.mem", "Clock memória", SensorType.Clock),
                ("power.draw", "Consumo", SensorType.Power), ("fan.speed", "Ventoinha (%)", SensorType.Load),
                ("memory.used", "VRAM usada (MiB)", SensorType.Data),
            })
            {
                string k = key;
                Add("nvidia/" + k, "NVIDIA GPU", name, t, () => _nv.TryGetValue(k, out var v) ? v : null);
            }
    }

    public void Update()
    {
        UpdateCpuLoad();
        UpdateRapl();
        if (_hasNvidia) UpdateNvidia();
        foreach (var (row, read) in _readers)
            try { row.Update(read()); } catch { }
    }

    private void UpdateCpuLoad()
    {
        var line = (Sys.Read("/proc/stat") ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("cpu "));
        if (line == null) return;
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        if (_prevCpu != null)
        {
            long total = v.Sum() - _prevCpu.Sum();
            long idle = (v[3] + (v.Length > 4 ? v[4] : 0)) - (_prevCpu[3] + (_prevCpu.Length > 4 ? _prevCpu[4] : 0));
            if (total > 0) _cpuLoad = 100f * (total - idle) / total;
        }
        _prevCpu = v;
    }

    private void UpdateRapl()
    {
        var uj = Sys.ReadLong("/sys/class/powercap/intel-rapl:0/energy_uj");
        if (uj == null) return;
        var now = DateTime.UtcNow;
        if (_prevRapl is { } p && uj >= p.Uj)
        {
            double dt = (now - p.T).TotalSeconds;
            if (dt > 0.2) _cpuPower = (float)((uj.Value - p.Uj) / 1e6 / dt);
            else return;
        }
        _prevRapl = (uj.Value, now);
    }

    private void UpdateNvidia()
    {
        var keys = new[] { "temperature.gpu", "utilization.gpu", "clocks.sm", "clocks.mem", "power.draw", "fan.speed", "memory.used" };
        var o = Sys.Run("nvidia-smi", $"--query-gpu={string.Join(',', keys)} --format=csv,noheader,nounits", 3000);
        var first = o?.Split('\n').FirstOrDefault()?.Split(',');
        if (first == null || first.Length < keys.Length) return;
        var d = new Dictionary<string, float>();
        for (int i = 0; i < keys.Length; i++)
            if (float.TryParse(first[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) d[keys[i]] = f;
        _nv = d;
    }
}
