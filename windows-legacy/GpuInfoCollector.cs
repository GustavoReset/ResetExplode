using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Uma placa de vídeo com os dados no estilo da aba "Graphics Card" do GPU-Z.</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Subtitle { get; set; } = "";
    public string Vendor { get; init; } = "";
    public List<InfoNode> Sections { get; } = new();
    internal IntPtr NvmlHandle { get; set; }
    internal ulong VramBytes { get; set; }

    /// <summary>Leituras dinâmicas que o LibreHardwareMonitor não traz (P-state, PerfCap...).</summary>
    public List<KeyValuePair<string, string>> LiveExtras()
    {
        var list = new List<KeyValuePair<string, string>>();
        var h = NvmlHandle;
        if (h == IntPtr.Zero) return list;

        if (Nvml.PState(h) is int p) list.Add(new("Estado de desempenho", $"P{p}"));
        if (Nvml.ThrottleReasons(h) is ulong r) list.Add(new("PerfCap (limitador)", PerfCap(r)));
        if (Nvml.PowerUsageMw(h) is uint pw && Nvml.PowerLimitMw(h) is uint lim && lim > 0)
            list.Add(new("Consumo / limite", $"{pw / 1000.0:0.0} W · {100.0 * pw / lim:0} %"));
        if (Nvml.Util(h) is Nvml.Utilization u) list.Add(new("Controlador de memória", $"{u.Memory} %"));
        if (Nvml.EncoderUtil(h) is uint enc) list.Add(new("Encoder (NVENC)", $"{enc} %"));
        if (Nvml.DecoderUtil(h) is uint dec) list.Add(new("Decoder (NVDEC)", $"{dec} %"));
        if (Nvml.CurPcieGen(h) is uint g && Nvml.CurPcieWidth(h) is uint w) list.Add(new("PCIe em uso", $"{PcieName(g)} x{w}"));
        return list;
    }

    private static string PerfCap(ulong r)
    {
        var parts = new List<string>();
        if ((r & 0x4) != 0) parts.Add("Energia");
        if ((r & 0x60) != 0) parts.Add("Térmico");
        if ((r & 0x8) != 0) parts.Add("HW slowdown");
        if ((r & 0x80) != 0) parts.Add("Power brake");
        if (parts.Count == 0) return (r & 0x1) != 0 ? "Ociosa" : "Nenhum";
        return string.Join(" + ", parts);
    }

    internal static string PcieName(uint gen) => gen switch
    {
        1 => "PCIe 1.1", 2 => "PCIe 2.0", 3 => "PCIe 3.0", 4 => "PCIe 4.0", 5 => "PCIe 5.0", 6 => "PCIe 6.0",
        _ => $"PCIe gen {gen}"
    };
}

public static class GpuInfoCollector
{
    public static List<GpuInfo> Collect()
    {
        var result = new List<GpuInfo>();
        var adapters = Dxgi.Enumerate();
        var nvDevices = Nvml.Devices();

        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
        foreach (ManagementObject vc in searcher.Get())
        {
            try { result.Add(Build(vc, adapters, nvDevices)); }
            catch (Exception ex)
            {
                var g = new GpuInfo { Name = vc["Name"]?.ToString() ?? "GPU" };
                g.Sections.Add(new InfoNode { Header = "ERRO" }.Also(n => n.Add("Falha ao ler", ex.Message)));
                result.Add(g);
            }
        }
        // Placa dedicada primeiro (mais VRAM).
        return result.OrderByDescending(g => g.VramBytes).ToList();
    }

    private static InfoNode Also(this InfoNode n, Action<InfoNode> a) { a(n); return n; }

    private static GpuInfo Build(ManagementObject vc, List<Dxgi.Adapter> adapters, List<(IntPtr Handle, Nvml.PciInfo Pci)> nvDevices)
    {
        string name = vc["Name"]?.ToString()?.Trim() ?? "GPU";
        string pnp = vc["PNPDeviceID"]?.ToString() ?? "";
        var m = Regex.Match(pnp, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{4})([0-9A-F]{4}))?(?:&REV_([0-9A-F]{2}))?",
                            RegexOptions.IgnoreCase);
        string ven = m.Groups[1].Value.ToUpperInvariant(), dev = m.Groups[2].Value.ToUpperInvariant();
        string subDev = m.Groups[3].Value.ToUpperInvariant(), subVen = m.Groups[4].Value.ToUpperInvariant();
        string rev = m.Groups[5].Value.ToUpperInvariant();

        string vendor = ven switch { "10DE" => "NVIDIA", "1002" => "AMD", "8086" => "Intel", _ => ven };
        var spec = GpuSpecDb.Find(ven, dev, name);
        var dx = adapters.FirstOrDefault(a => a.VendorId.ToString("X4") == ven && a.DeviceId.ToString("X4") == dev);

        IntPtr h = IntPtr.Zero;
        if (ven == "10DE")
        {
            uint pciId = (Convert.ToUInt32(dev, 16) << 16) | 0x10DE;
            h = nvDevices.FirstOrDefault(d => d.Pci.PciDeviceId == pciId).Handle;
        }

        var pnpProps = PnpProperties(pnp);
        var reg = DriverRegistry(pnpProps.GetValueOrDefault("DEVPKEY_Device_Driver") as string);

        var gpu = new GpuInfo { Name = name, Vendor = vendor, NvmlHandle = h };

        // ---------- Identificação ----------
        var id = Section(gpu, "IDENTIFICAÇÃO");
        id.Add("Nome", name);
        id.Add("Chip", spec?.Chip ?? "—");
        id.Add("Arquitetura", spec?.Arch ?? "—");
        id.Add("Revisão", rev.Length > 0 ? rev : "—");
        id.Add("Fabricante da placa", subVen.Length > 0 ? GpuSpecDb.SubvendorName(subVen) : "—");
        id.Add("Device ID", $"{ven} {dev} · {subVen} {subDev}".Trim(' ', '·'));
        id.Add("Processo", spec?.Process ?? "—");
        id.Add("Área do die", spec != null ? $"{spec.DieMm2} mm²" : "—");
        id.Add("Transistores", spec != null ? $"{spec.TransistorsBi:0.#} bilhões" : "—");
        id.Add("Lançamento", spec?.Release ?? "—");

        // ---------- Unidades e desempenho teórico ----------
        uint? cores = h != IntPtr.Zero ? Nvml.Cores(h) : null;
        int shaders = (int)(cores ?? (uint)(spec?.Shaders ?? 0));
        var units = Section(gpu, "UNIDADES DE PROCESSAMENTO");
        if (shaders > 0)
        {
            bool nv = ven == "10DE";
            int perUnit = nv ? 128 : 64;
            units.Add(nv ? "CUDA cores" : "Stream processors", $"{shaders:N0}");
            units.Add(nv ? "SMs" : "Compute units", $"{shaders / perUnit}");
            if (nv && spec != null && spec.Arch is "Blackwell" or "Ada Lovelace" or "Ampere")
            {
                units.Add("Tensor cores", $"{shaders / perUnit * 4}");
                units.Add("RT cores", $"{shaders / perUnit}");
            }
            else if (!nv && spec != null && spec.Arch.StartsWith("RDNA"))
                units.Add("Ray accelerators", $"{shaders / perUnit}");
        }
        if (spec != null)
        {
            units.Add("ROPs / TMUs", $"{spec.Rops} / {spec.Tmus}");
            units.Add("Pixel fillrate", $"{spec.Rops * spec.BoostMHz / 1000.0:0.0} GPixel/s");
            units.Add("Texture fillrate", $"{spec.Tmus * spec.BoostMHz / 1000.0:0.0} GTexel/s");
            units.Add("FP32 (teórico)", $"{(shaders > 0 ? shaders : spec.Shaders) * 2.0 * spec.BoostMHz / 1e6:0.00} TFLOPS");
        }
        if (units.Children.Count == 0) units.Add("Sem dados", "GPU fora do banco de especificações");

        // ---------- Memória ----------
        var mem = Section(gpu, "MEMÓRIA");
        ulong vram = 0;
        if (h != IntPtr.Zero && Nvml.MemoryInfo(h) is Nvml.Memory nm) vram = nm.Total;
        if (vram == 0 && dx != null) vram = dx.DedicatedVideo;
        if (vram == 0 && reg.TryGetValue("HardwareInformation.qwMemorySize", out var q)) vram = Convert.ToUInt64(q);
        gpu.VramBytes = vram;
        bool igpu = spec?.MemType.StartsWith("Memória do sistema") == true;
        if (igpu || vram < 1UL << 30) mem.Add(igpu ? "Reservada (dedicada)" : "Tamanho", Bytes(vram));
        else
        {
            // Driver reserva parte da VRAM: 12227 MiB reportados = placa de 12 GB (como mostra o GPU-Z).
            double gib = vram / (double)(1UL << 30);
            mem.Add("Tamanho", $"{Math.Ceiling(gib - 0.15):0} GB ({vram >> 20:N0} MB utilizáveis)");
        }
        if (dx != null && dx.SharedSystem > 0) mem.Add("Compartilhada do sistema", Bytes(dx.SharedSystem));
        mem.Add("Tipo", spec?.MemType ?? "—");

        uint? bus = h != IntPtr.Zero ? Nvml.BusWidth(h) : null;
        uint? memClock = h != IntPtr.Zero ? Nvml.MaxClock(h, Nvml.ClockMem) : null;
        if (bus is uint b) mem.Add("Barramento", $"{b} bits");
        if (memClock is uint mc)
        {
            // A NVML reporta metade da taxa efetiva (GDDR6/6X/7): 14001 MHz → 28 Gbps.
            double gbps = mc * 2 / 1000.0;
            mem.Add("Clock da memória", $"{mc} MHz ({gbps:0.0} Gbps efetivos)");
            if (bus is uint bw) mem.Add("Largura de banda", $"{gbps * bw / 8:0.0} GB/s");
        }
        if (h != IntPtr.Zero && Nvml.Bar1(h) is Nvml.Memory bar)
            mem.Add("Resizable BAR", bar.Total > 256UL * 1024 * 1024 ? $"Ativado (BAR1 {Bytes(bar.Total)})" : "Desativado");

        // ---------- Clocks ----------
        var clk = Section(gpu, "CLOCKS");
        if (spec != null)
        {
            clk.Add("Base (referência)", $"{spec.BaseMHz} MHz");
            clk.Add("Boost (referência)", $"{spec.BoostMHz} MHz");
        }
        if (h != IntPtr.Zero)
        {
            if (Nvml.MaxClock(h, Nvml.ClockGraphics) is uint g) clk.Add("Máximo (curva V/F)", $"{g} MHz");
            if (Nvml.MaxClock(h, Nvml.ClockVideo) is uint v) clk.Add("Video engine máx.", $"{v} MHz");
        }
        if (clk.Children.Count == 0) clk.Add("Sem dados", "—");

        // ---------- Barramento ----------
        var pcie = Section(gpu, "BARRAMENTO");
        uint? maxGen = h != IntPtr.Zero ? Nvml.MaxPcieGen(h) : null;
        uint? maxW = h != IntPtr.Zero ? Nvml.MaxPcieWidth(h) : null;
        maxGen ??= AsUInt(pnpProps.GetValueOrDefault("DEVPKEY_PciDevice_MaxLinkSpeed"));
        maxW ??= AsUInt(pnpProps.GetValueOrDefault("DEVPKEY_PciDevice_MaxLinkWidth"));
        uint? curGen = h != IntPtr.Zero ? Nvml.CurPcieGen(h) : AsUInt(pnpProps.GetValueOrDefault("DEVPKEY_PciDevice_CurrentLinkSpeed"));
        uint? curW = h != IntPtr.Zero ? Nvml.CurPcieWidth(h) : AsUInt(pnpProps.GetValueOrDefault("DEVPKEY_PciDevice_CurrentLinkWidth"));
        if (maxGen is uint mg && maxW is uint mw) pcie.Add("Interface", $"{GpuInfo.PcieName(mg)} x{mw}");
        if (curGen is uint cg && curW is uint cw)
            pcie.Add("Em uso agora", $"{GpuInfo.PcieName(cg)} x{cw}" + (maxGen > curGen ? " (economia de energia em repouso)" : ""));
        if (pnpProps.GetValueOrDefault("DEVPKEY_Device_LocationInfo") is string loc) pcie.Add("Localização", loc);

        // ---------- Energia (NVIDIA) ----------
        if (h != IntPtr.Zero || spec?.TdpW > 0)
        {
            var pwr = Section(gpu, "ENERGIA");
            if (spec?.TdpW > 0) pwr.Add("TDP (referência)", $"{spec.TdpW} W");
            if (h != IntPtr.Zero)
            {
                if (Nvml.PowerLimitMw(h) is uint lim) pwr.Add("Limite atual", $"{lim / 1000.0:0} W");
                if (Nvml.PowerDefaultMw(h) is uint def) pwr.Add("Limite padrão da placa", $"{def / 1000.0:0} W");
                if (Nvml.PowerConstraintsMw(h) is { } pc) pwr.Add("Faixa ajustável", $"{pc.Min / 1000.0:0} – {pc.Max / 1000.0:0} W");
            }
        }

        // ---------- Driver e BIOS ----------
        var drv = Section(gpu, "DRIVER E BIOS");
        string? vbios = h != IntPtr.Zero ? Nvml.Vbios(h) : null;
        vbios ??= RegString(reg, "HardwareInformation.BiosString");
        drv.Add("BIOS (VBIOS)", vbios ?? "—");
        string wmiVer = vc["DriverVersion"]?.ToString() ?? "—";
        string? nvVer = h != IntPtr.Zero ? Nvml.DriverVersion() : null;
        drv.Add("Versão do driver", nvVer != null ? $"{nvVer} ({wmiVer})" : wmiVer);
        if (vc["DriverDate"] is string dd && dd.Length >= 8)
            drv.Add("Data do driver", ManagementDateTimeConverter.ToDateTime(dd).ToString("dd/MM/yyyy"));
        drv.Add("Assinatura digital", DriverSigner(pnp));
        if (h != IntPtr.Zero)
        {
            if (Nvml.CudaVersion() is string cuda) drv.Add("Versão CUDA", cuda);
            if (Nvml.ComputeCapability(h) is string cc) drv.Add("Compute capability", cc);
        }

        // ---------- Tecnologias ----------
        var tech = Section(gpu, "TECNOLOGIAS");
        string? fl = dx?.FeatureLevel;
        tech.Add("DirectX", fl switch
        {
            "12_2" => "DirectX 12 Ultimate (12_2)",
            null => "—",
            _ => $"DirectX 12 ({fl.Replace('_', '.')})"
        });
        var apis = new List<string>();
        if (ven == "10DE" && File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"))) apis.Add("CUDA");
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "OpenCL.dll"))) apis.Add("OpenCL");
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"))) apis.Add("Vulkan");
        if (fl != null) { apis.Add("DirectCompute"); apis.Add("DirectML"); }
        if (ven == "10DE") apis.Add("PhysX");
        tech.Add("APIs", apis.Count > 0 ? string.Join(" · ", apis) : "—");
        if (spec != null && GpuSpecDb.ArchFeatures.TryGetValue(spec.Arch, out var feats)) tech.Add("Recursos da arquitetura", feats);

        string memLabel = spec?.MemType.StartsWith("GDDR") == true ? spec.MemType : "";
        gpu.Subtitle = string.Join("  ·  ", new[]
        {
            spec?.Chip, spec?.Arch, spec?.Process,
            vram >= 1UL << 30 && !igpu ? $"{Math.Ceiling(vram / (double)(1UL << 30) - 0.15):0} GB {memLabel}".Trim() : null,
            subVen.Length > 0 ? GpuSpecDb.SubvendorName(subVen) : null
        }.Where(s => !string.IsNullOrEmpty(s)));
        return gpu;
    }

    private static InfoNode Section(GpuInfo g, string title)
    {
        var n = new InfoNode { Header = title };
        g.Sections.Add(n);
        return n;
    }

    /// <summary>Propriedades de dispositivo (link PCIe, chave do driver...) via Win32_PnPEntity.GetDeviceProperties.</summary>
    private static Dictionary<string, object?> PnpProperties(string pnpId)
    {
        var dict = new Dictionary<string, object?>();
        if (pnpId.Length == 0) return dict;
        try
        {
            // Instanciar pelo caminho do objeto falha ("Não encontrado"); via consulta funciona.
            using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE DeviceID='{pnpId.Replace("\\", "\\\\")}'");
            var mo = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (mo == null) return dict;
            var input = mo.GetMethodParameters("GetDeviceProperties");
            input["devicePropertyKeys"] = new[]
            {
                "DEVPKEY_PciDevice_CurrentLinkSpeed", "DEVPKEY_PciDevice_CurrentLinkWidth",
                "DEVPKEY_PciDevice_MaxLinkSpeed", "DEVPKEY_PciDevice_MaxLinkWidth",
                "DEVPKEY_Device_Driver", "DEVPKEY_Device_LocationInfo"
            };
            var output = mo.InvokeMethod("GetDeviceProperties", input, null);
            if (output?["deviceProperties"] is ManagementBaseObject[] props)
                foreach (var p in props)
                    if (p["KeyName"] is string k) dict[k] = p["Data"];
        }
        catch { }
        return dict;
    }

    /// <summary>Valores "HardwareInformation.*" da chave de classe do driver de vídeo.</summary>
    private static Dictionary<string, object> DriverRegistry(string? driverKey)
    {
        var dict = new Dictionary<string, object>();
        if (string.IsNullOrEmpty(driverKey)) return dict;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
            if (key == null) return dict;
            foreach (var n in key.GetValueNames())
                if (n.StartsWith("HardwareInformation.") && key.GetValue(n) is object v) dict[n] = v;
        }
        catch { }
        return dict;
    }

    /// <summary>Alguns drivers gravam strings como REG_BINARY UTF-16.</summary>
    private static string? RegString(Dictionary<string, object> reg, string name)
    {
        if (!reg.TryGetValue(name, out var v)) return null;
        string s = v is byte[] bytes ? System.Text.Encoding.Unicode.GetString(bytes) : v.ToString() ?? "";
        s = s.TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }

    private static string DriverSigner(string pnpId)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                $"SELECT IsSigned, Signer FROM Win32_PnPSignedDriver WHERE DeviceID='{pnpId.Replace("\\", "\\\\")}'");
            foreach (ManagementObject d in s.Get())
            {
                bool signed = d["IsSigned"] is bool b && b;
                string signer = d["Signer"]?.ToString() ?? "";
                if (!signed) return "Não assinado";
                return signer.Contains("Windows Hardware Compatibility", StringComparison.OrdinalIgnoreCase)
                    ? "WHQL (Microsoft)" : $"Assinado · {signer}";
            }
        }
        catch { }
        return "—";
    }

    private static uint? AsUInt(object? v) => v == null ? null : Convert.ToUInt32(v);

    private static string Bytes(ulong b)
    {
        if (b == 0) return "—";
        double v = b;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
