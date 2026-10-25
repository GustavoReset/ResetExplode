using System.Management;

namespace ResetExplode;

/// <summary>Coleta informações estáticas do sistema via WMI.</summary>
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

    private static IEnumerable<ManagementObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (ManagementObject mo in searcher.Get())
            yield return mo;
    }

    private static string S(ManagementObject mo, string prop)
        => mo[prop]?.ToString()?.Trim() ?? "—";

    private static string Bytes(object? v)
    {
        if (v == null) return "—";
        double b = Convert.ToDouble(v);
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }

    private static void CollectOs(InfoNode n)
    {
        foreach (var os in Query("SELECT * FROM Win32_OperatingSystem"))
        {
            n.Add("Nome", S(os, "Caption"));
            n.Add("Versão", S(os, "Version"));
            n.Add("Build", S(os, "BuildNumber"));
            n.Add("Arquitetura", S(os, "OSArchitecture"));
            n.Add("Nome do computador", Environment.MachineName);
        }
    }

    private static void CollectBoard(InfoNode n)
    {
        var board = n.Add("Placa-mãe");
        foreach (var b in Query("SELECT * FROM Win32_BaseBoard"))
        {
            board.Add("Fabricante", S(b, "Manufacturer"));
            board.Add("Modelo", S(b, "Product"));
            board.Add("Versão", S(b, "Version"));
            board.Add("Nº de série", S(b, "SerialNumber"));
        }
        var bios = n.Add("BIOS");
        foreach (var b in Query("SELECT * FROM Win32_BIOS"))
        {
            bios.Add("Fabricante", S(b, "Manufacturer"));
            bios.Add("Versão", S(b, "SMBIOSBIOSVersion"));
            bios.Add("Data", ManagementDateTimeConverter.ToDateTime(b["ReleaseDate"].ToString()).ToString("dd/MM/yyyy"));
        }
    }

    private static void CollectCpu(InfoNode n)
    {
        foreach (var c in Query("SELECT * FROM Win32_Processor"))
        {
            var cpu = n.Add(S(c, "Name"));
            cpu.Add("Fabricante", S(c, "Manufacturer"));
            cpu.Add("Soquete", S(c, "SocketDesignation"));
            cpu.Add("Núcleos físicos", S(c, "NumberOfCores"));
            cpu.Add("Threads (lógicos)", S(c, "NumberOfLogicalProcessors"));
            cpu.Add("Clock base/máx. declarado", S(c, "MaxClockSpeed") + " MHz");
            cpu.Add("Cache L2", S(c, "L2CacheSize") + " KB");
            cpu.Add("Cache L3", S(c, "L3CacheSize") + " KB");
            cpu.Add("Virtualização habilitada", S(c, "VirtualizationFirmwareEnabled"));
        }
    }

    private static string RamType(object? smbios, object? typeLegacy)
    {
        return Convert.ToInt32(smbios ?? 0) switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5",
            _ => "Desconhecido"
        };
    }

    private static string RamForm(object? ff) => Convert.ToInt32(ff ?? 0) switch
    {
        8 => "DIMM", 12 => "SO-DIMM", 13 => "Micro-DIMM", 15 => "FB-DIMM", 9 => "RIMM", 11 => "SRIMM", 6 => "Proprietário", 0 => "—",
        _ => "Outro"
    };

    // Velocidades que costumam aparecer no part number do módulo (ex.: CMK32GX5M2B6000C30, F5-6000J3038F16GX2).
    private static readonly System.Text.RegularExpressions.Regex SpeedInPart = new(
        @"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|5800|6000|6200|6400|6600|6800|7000|7200|7400|7600|7800|8000|8200|8400|8800)(?!\d)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ChannelInLocator = new(
        @"(?i)(?:channel|chan|ch)[\s_-]*([A-H0-9])|dimm[\s_-]*([A-H])\d|(?:^|[^A-Za-z])([A-H])[12](?:$|[^0-9])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ChannelOf(string locator)
    {
        var m = ChannelInLocator.Match(locator);
        if (!m.Success) return null;
        for (int g = 1; g <= 3; g++) if (m.Groups[g].Success) return m.Groups[g].Value.ToUpperInvariant();
        return null;
    }

    private static string Volt(object? mv)
    {
        if (mv == null) return "—";
        double v = Convert.ToDouble(mv);
        return v > 0 ? $"{v / 1000:0.00#} V" : "—";
    }

    private static void CollectRam(InfoNode n)
    {
        var mods = new List<(string Bank, string Loc, ulong Cap, string Type, string Form, int Rated, int Conf, string Maker, string Part,
                             string Serial, int DataW, int TotalW, string Rank, string VoltCfg, string VoltRange)>();
        foreach (var m in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            int attr = Convert.ToInt32(m["Attributes"] ?? 0);
            string rank = attr switch { 1 => "1 (single)", 2 => "2 (dual)", 3 => "3", 4 => "4 (quad)", _ => "—" };
            string range = m["MinVoltage"] != null && m["MaxVoltage"] != null
                ? $"{Volt(m["MinVoltage"])} – {Volt(m["MaxVoltage"])}" : "—";
            mods.Add((S(m, "BankLabel"), S(m, "DeviceLocator"), Convert.ToUInt64(m["Capacity"] ?? 0UL),
                      RamType(m["SMBIOSMemoryType"], m["MemoryType"]), RamForm(m["FormFactor"]),
                      Convert.ToInt32(m["Speed"] ?? 0), Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0),
                      S(m, "Manufacturer"), S(m, "PartNumber"), S(m, "SerialNumber"),
                      Convert.ToInt32(m["DataWidth"] ?? 0), Convert.ToInt32(m["TotalWidth"] ?? 0),
                      rank, Volt(m["ConfiguredVoltage"]), range));
        }

        ulong total = 0;
        foreach (var m in mods) total += m.Cap;

        int slots = 0; ulong maxBytes = 0;
        try
        {
            foreach (var a in Query("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                slots += Convert.ToInt32(a["MemoryDevices"] ?? 0);
                ulong kb = a["MaxCapacityEx"] != null ? Convert.ToUInt64(a["MaxCapacityEx"]) : Convert.ToUInt64(a["MaxCapacity"] ?? 0UL);
                maxBytes += kb * 1024;
            }
        }
        catch { }

        n.Add("Total instalado", Bytes(total));

        // ---- Resumo ----
        var sum = n.Add("Resumo");
        sum.Add("Módulos instalados", slots > 0 ? $"{mods.Count} de {slots} slots" : mods.Count.ToString());
        if (maxBytes > 0) sum.Add("Capacidade máxima suportada pela placa", Bytes(maxBytes));
        if (mods.Count > 0)
        {
            var types = mods.Select(m => m.Type).Distinct().ToList();
            sum.Add("Tipo", string.Join(" / ", types));

            int conf = mods.Where(m => m.Conf > 0).Select(m => m.Conf).DefaultIfEmpty(0).Min();
            int rated = mods.Select(m => m.Rated).DefaultIfEmpty(0).Max();
            sum.Add("Velocidade em uso", conf > 0 ? $"{conf} MT/s" : "—");
            sum.Add("Velocidade nominal reportada pelo módulo", rated > 0 ? $"{rated} MT/s" : "—");

            // Perfil EXPO/XMP: o Windows não diz se está ligado; estimamos pela velocidade escrita no part number.
            int label = 0;
            foreach (var m in mods)
            {
                var mt = SpeedInPart.Match(m.Part);
                if (mt.Success) label = Math.Max(label, int.Parse(mt.Value));
            }
            string profile;
            if (conf <= 0) profile = "Não foi possível ler a velocidade em uso.";
            else if (label > 0 && conf < label * 0.95)
                profile = $"Provavelmente DESLIGADO: a RAM roda a {conf} MT/s, mas o part number indica {label} MT/s. Ative EXPO/XMP na BIOS.";
            else if (label > 0)
                profile = $"Provavelmente ATIVO: roda a {conf} MT/s (part number indica {label} MT/s).";
            else if (rated > 0 && conf < rated)
                profile = $"Roda abaixo da velocidade nominal ({conf} de {rated} MT/s). Confira EXPO/XMP na BIOS.";
            else
                profile = "Sem como estimar (o part number não traz a velocidade). Confira na BIOS.";
            sum.Add("Perfil EXPO/XMP (estimado)", profile);

            // Canais: deduzidos pelos nomes dos slots (ex.: DIMM_A2 + DIMM_B2 = dois canais).
            var chans = mods.Select(m => ChannelOf(m.Loc) ?? ChannelOf(m.Bank)).ToList();
            string chText;
            if (mods.Count == 1) chText = "Canal único (1 módulo). Com 2 módulos iguais a RAM roda em dual channel e fica bem mais rápida.";
            else if (chans.All(c => c != null))
            {
                int distinct = chans.Distinct().Count();
                chText = distinct >= 2 ? $"{distinct} canais (dual channel ou mais)" : "Canal único: os módulos estão no mesmo canal. Mude para os slots recomendados no manual da placa.";
            }
            else chText = $"Não deu para identificar pelos nomes dos slots ({string.Join(", ", mods.Select(m => m.Loc))}). Veja o manual da placa.";
            sum.Add("Canais (estimado)", chText);

            bool ecc = mods.Any(m => m.TotalW > m.DataW && m.DataW > 0);
            sum.Add("ECC", ecc ? "Sim (correção de erros)" : "Não");

            var volts = mods.Select(m => m.VoltCfg).Where(v => v != "—").Distinct().ToList();
            if (volts.Count > 0) sum.Add("Tensão em uso", string.Join(" / ", volts));
        }
        sum.Add("Timings (CL, tRCD, tRP...)", "O Windows não expõe. Veja na BIOS ou com o CPU-Z (aba Memory/SPD).");

        try
        {
            foreach (var os in Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem"))
            {
                double totKb = Convert.ToDouble(os["TotalVisibleMemorySize"]), freeKb = Convert.ToDouble(os["FreePhysicalMemory"]);
                double virtTot = Convert.ToDouble(os["TotalVirtualMemorySize"]), virtFree = Convert.ToDouble(os["FreeVirtualMemory"]);
                sum.Add("Em uso (no momento da leitura)", $"{Bytes((totKb - freeKb) * 1024)} de {Bytes(totKb * 1024)} ({(totKb - freeKb) / totKb:P0})");
                sum.Add("Memória virtual em uso (RAM + arquivo de paginação)", $"{Bytes((virtTot - virtFree) * 1024)} de {Bytes(virtTot * 1024)}");
            }
        }
        catch { }

        // ---- Módulos ----
        foreach (var m in mods)
        {
            var slot = n.Add($"Módulo — {m.Bank} / {m.Loc}");
            slot.Add("Capacidade", Bytes(m.Cap));
            slot.Add("Tipo", m.Type);
            slot.Add("Formato", m.Form);
            slot.Add("Ranks", m.Rank);
            slot.Add("Velocidade em uso", m.Conf > 0 ? $"{m.Conf} MT/s" : "—");
            slot.Add("Velocidade nominal", m.Rated > 0 ? $"{m.Rated} MT/s" : "—");
            slot.Add("Tensão em uso", m.VoltCfg);
            slot.Add("Faixa de tensão do módulo", m.VoltRange);
            slot.Add("Largura de dados / total", m.DataW > 0 ? $"{m.DataW} / {m.TotalW} bits" : "—");
            slot.Add("Fabricante", m.Maker);
            slot.Add("Part number", m.Part);
            slot.Add("Nº de série", m.Serial);
        }
    }

    private static void CollectGpu(InfoNode n)
    {
        foreach (var g in Query("SELECT * FROM Win32_VideoController"))
        {
            var gpu = n.Add(S(g, "Name"));
            gpu.Add("Versão do driver", S(g, "DriverVersion"));
            gpu.Add("Memória (WMI, limitada a 4 GB)", Bytes(g["AdapterRAM"]));
            gpu.Add("Resolução atual", $"{S(g, "CurrentHorizontalResolution")}x{S(g, "CurrentVerticalResolution")} @ {S(g, "CurrentRefreshRate")} Hz");
        }
    }

    /// <summary>Relê os discos físicos (ex.: depois de conectar um pendrive ou HD externo).</summary>
    public static void RefreshDisks(InfoNode n)
    {
        n.Children.Clear();
        try { CollectDisks(n); }
        catch (Exception ex) { n.Add("Erro ao ler", ex.Message); }
    }

    private static void CollectDisks(InfoNode n)
    {
        foreach (var d in Query("SELECT * FROM Win32_DiskDrive"))
        {
            var disk = n.Add(S(d, "Model"));
            disk.Add("Capacidade", Bytes(d["Size"]));
            disk.Add("Interface", S(d, "InterfaceType"));
            disk.Add("Nº de série", S(d, "SerialNumber"));
            disk.Add("Firmware", S(d, "FirmwareRevision"));
        }
    }
}
