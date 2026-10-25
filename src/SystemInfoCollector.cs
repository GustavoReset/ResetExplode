using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>Coleta informações estáticas do sistema lendo /proc, /sys e utilitários padrão do Linux.</summary>
public static class SystemInfoCollector
{
    public static List<InfoNode> Collect()
    {
        var roots = new List<InfoNode>();
        Safe(roots, "Sistema operacional", CollectOs);
        Safe(roots, "Placa-mãe e BIOS", CollectBoard);
        Safe(roots, "Processador", CollectCpu);
        Safe(roots, "Memória RAM", CollectRam);
        Safe(roots, "Placa de vídeo", CollectGpu);
        Safe(roots, "Armazenamento", CollectDisks);
        return roots;
    }

    private static void Safe(List<InfoNode> roots, string title, Action<InfoNode> fill)
    {
        var node = new InfoNode { Header = title };
        try { fill(node); }
        catch (Exception ex) { node.Add("Erro ao ler", ex.Message); }
        roots.Add(node);
    }

    private static string Dmi(string f) => Sys.Read("/sys/class/dmi/id/" + f) is { Length: > 0 } s ? s : "—";

    private static void CollectOs(InfoNode n)
    {
        var os = Sys.KeyValues(Sys.Read("/etc/os-release"), '=');
        string V(string k) => os.TryGetValue(k, out var v) ? v.Trim('"') : "—";
        n.Add("Nome", V("PRETTY_NAME"));
        n.Add("Versão", V("VERSION"));
        n.Add("Kernel", Sys.Read("/proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription);
        n.Add("Arquitetura", RuntimeInformation.OSArchitecture.ToString());
        n.Add("Nome do computador", Environment.MachineName);
        n.Add("Sessão gráfica", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "—");
        n.Add("Ambiente", Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "—");
        if (Sys.Read("/proc/uptime") is { } up && double.TryParse(up.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var s))
            n.Add("Ligado há", TimeSpan.FromSeconds(s).ToString(@"d\.hh\:mm\:ss"));
    }

    private static void CollectBoard(InfoNode n)
    {
        var board = n.Add("Placa-mãe");
        board.Add("Fabricante", Dmi("board_vendor"));
        board.Add("Modelo", Dmi("board_name"));
        board.Add("Versão", Dmi("board_version"));
        var sys = n.Add("Sistema");
        sys.Add("Fabricante", Dmi("sys_vendor"));
        sys.Add("Produto", Dmi("product_name"));
        var bios = n.Add("BIOS");
        bios.Add("Fabricante", Dmi("bios_vendor"));
        bios.Add("Versão", Dmi("bios_version"));
        bios.Add("Data", Dmi("bios_date"));
        bios.Add("Modo de boot", Directory.Exists("/sys/firmware/efi") ? "UEFI" : "BIOS legado");
        n.Add("Nº de série", "requer root (dmidecode)");
    }

    private static void CollectCpu(InfoNode n)
    {
        var lines = (Sys.Read("/proc/cpuinfo") ?? "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var first = Sys.KeyValues(lines.FirstOrDefault());
        string G(string k) => first.TryGetValue(k, out var v) ? v : "—";
        n.Add("Modelo", G("model name"));
        n.Add("Fabricante", G("vendor_id"));
        var lscpu = Sys.KeyValues(Sys.Run("lscpu"));
        string L(string k) => lscpu.TryGetValue(k, out var v) ? v : "—";
        n.Add("Núcleos físicos", $"{L("Core(s) per socket")} × {L("Socket(s)")}");
        n.Add("Threads lógicos", Environment.ProcessorCount.ToString());
        n.Add("Clock máximo", L("CPU max MHz") is { } m && double.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, out var mhz) ? $"{mhz:0} MHz" : "—");
        n.Add("Cache L2", L("L2 cache"));
        n.Add("Cache L3", L("L3 cache"));
        n.Add("Virtualização", L("Virtualization"));
        var gov = Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor");
        if (gov != null) n.Add("Governor", gov);
        n.Add("Recursos", G("flags").Length > 80 ? G("flags")[..80] + "…" : G("flags"));
    }

    private static void CollectRam(InfoNode n)
    {
        var mi = Sys.KeyValues(Sys.Read("/proc/meminfo"));
        long Kb(string k) => mi.TryGetValue(k, out var v) && long.TryParse(v.Split(' ')[0], out var x) ? x * 1024 : 0;
        n.Add("Total", Sys.Bytes(Kb("MemTotal")));
        n.Add("Disponível", Sys.Bytes(Kb("MemAvailable")));
        n.Add("Swap", Sys.Bytes(Kb("SwapTotal")));
        var dm = Sys.Run("dmidecode", "-t memory", 3000);
        if (string.IsNullOrWhiteSpace(dm) || dm.Contains("Permission"))
            n.Add("Módulos", "execute como root para ver detalhes dos pentes (dmidecode)");
        else
            foreach (var blk in dm.Split("\n\n").Where(b => b.Contains("Memory Device") && !b.Contains("No Module")))
            {
                var d = Sys.KeyValues(blk);
                var m = n.Add(d.GetValueOrDefault("Locator", "Módulo"));
                m.Add("Tamanho", d.GetValueOrDefault("Size", "—"));
                m.Add("Tipo", d.GetValueOrDefault("Type", "—"));
                m.Add("Velocidade", d.GetValueOrDefault("Configured Memory Speed", d.GetValueOrDefault("Speed", "—")));
                m.Add("Fabricante", d.GetValueOrDefault("Manufacturer", "—"));
                m.Add("Part number", d.GetValueOrDefault("Part Number", "—"));
            }
    }

    private static void CollectGpu(InfoNode n)
    {
        foreach (var g in GpuInfo.Collect())
        {
            var node = n.Add(g.Name);
            foreach (var kv in g.Details) node.Add(kv.Key, kv.Value);
        }
        if (n.Children.Count == 0) n.Add("Nenhuma GPU detectada", "instale pciutils (lspci)");
    }

    private static void CollectDisks(InfoNode n)
    {
        foreach (var d in DiskHealth.ListDisks())
        {
            var node = n.Add(d.Title, d.Meta);
            foreach (var kv in d.Metrics) node.Add(kv.Key, kv.Value);
        }
    }
}
