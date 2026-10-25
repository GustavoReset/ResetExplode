using System.Text.RegularExpressions;

namespace ResetExplode;

public sealed record GpuEntry(string Name, List<KeyValuePair<string, string>> Details);

/// <summary>GPUs via lspci + sysfs; NVIDIA via nvidia-smi; AMD via sysfs (amdgpu).</summary>
public static class GpuInfo
{
    public static List<GpuEntry> Collect()
    {
        var list = new List<GpuEntry>();
        var lspci = Sys.Run("lspci", "-mm -nn -d ::03xx") ?? "";
        foreach (var line in lspci.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = Regex.Matches(line, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
            if (parts.Count < 4) continue;
            string slot = parts[0], vendor = Clean(parts[2]), device = Clean(parts[3]);
            var d = new List<KeyValuePair<string, string>>
            {
                new("Fabricante", vendor), new("Barramento PCI", slot),
            };
            var drv = Sys.Run("lspci", $"-k -s {slot}") ?? "";
            var m = Regex.Match(drv, @"Kernel driver in use:\s*(\S+)");
            if (m.Success) d.Add(new("Driver do kernel", m.Groups[1].Value));
            AddSysfs(slot, d);
            list.Add(new GpuEntry($"{vendor} {device}".Trim(), d));
        }
        AddNvidia(list);
        return list;
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s*\[[0-9a-f]{4}\]$", "").Trim();

    private static void AddSysfs(string slot, List<KeyValuePair<string, string>> d)
    {
        var dev = $"/sys/bus/pci/devices/0000:{slot}";
        if (!Directory.Exists(dev)) return;
        var spd = Sys.Read(dev + "/current_link_speed"); var w = Sys.Read(dev + "/current_link_width");
        var maxS = Sys.Read(dev + "/max_link_speed"); var maxW = Sys.Read(dev + "/max_link_width");
        if (spd != null) d.Add(new("PCIe em uso", $"{spd} x{w}"));
        if (maxS != null) d.Add(new("PCIe máximo", $"{maxS} x{maxW}"));
        if (Sys.ReadLong(dev + "/mem_info_vram_total") is { } vram) d.Add(new("VRAM", Sys.Bytes(vram)));
        if (Sys.ReadLong(dev + "/mem_info_vram_used") is { } used) d.Add(new("VRAM em uso", Sys.Bytes(used)));
        if (Sys.ReadLong(dev + "/gpu_busy_percent") is { } busy) d.Add(new("Carga", busy + " %"));
    }

    private static void AddNvidia(List<GpuEntry> list)
    {
        if (!Sys.HasCommand("nvidia-smi")) return;
        var o = Sys.Run("nvidia-smi", "--query-gpu=name,driver_version,memory.total,vbios_version,power.limit,clocks.max.sm,pcie.link.gen.max --format=csv,noheader");
        if (string.IsNullOrWhiteSpace(o)) return;
        int i = 0;
        var nv = list.Where(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 7 || i >= nv.Count) { i++; continue; }
            var d = nv[i++].Details;
            d.Add(new("Driver NVIDIA", f[1])); d.Add(new("VRAM", f[2])); d.Add(new("VBIOS", f[3]));
            d.Add(new("Limite de energia", f[4])); d.Add(new("Clock máx. SM", f[5])); d.Add(new("PCIe Gen máx.", f[6]));
        }
    }
}
