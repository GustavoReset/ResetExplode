using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Família de chip USB-serial: como reconhecer o dispositivo e o pacote de driver, e onde baixar o oficial.</summary>
/// <summary>Bundle = pasta em Drivers com o driver oficial embutido no .exe (null = não incluído).</summary>
public sealed record SerialChip(string Name, string[] Vids, string[] Infs, string[] Providers, string OfficialUrl, string? Bundle = null);

/// <summary>Placa ou conversor serial conectado agora, com o diagnóstico.</summary>
public sealed class SerialDeviceRow
{
    public UsbDevice Device { get; init; } = new();
    public SerialChip? Chip { get; init; }
    public string Name => Device.Name;
    public string Verdict { get; init; } = "";
    public string Advice { get; init; } = "";
    public bool Ok { get; init; }
    public string Driver { get; init; } = "";       // "FTDI 2.12.28.0 · oem12.inf"
    public string? Inf { get; init; }               // oemXX.inf em uso
    public string Detail => string.Join(" · ", new[] { Device.Port, Chip?.Name ?? Device.Maker, $"VID {Device.Vid} PID {Device.Pid}" }
                                                  .Where(s => !string.IsNullOrEmpty(s)));
    public string State => Ok ? "FUNCIONANDO" : "COM PROBLEMA";
}

/// <summary>Driver oficial que vem dentro do RESET EXPLODE (pasta Drivers\ do projeto).</summary>
public sealed class BundledDriver
{
    public SerialChip Chip { get; init; } = null!;
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public string Files { get; init; } = "";
    public bool Installed { get; init; }
    public string Title => Version.EndsWith("pacotes") ? $"{Chip.Name} · {Version}" : $"{Chip.Name} · versão {Version}";
    public string Detail => $"{Files} · {Date}";
    public string State => Installed ? "JÁ INSTALADO" : "";
}

/// <summary>Pacote de driver de chip serial guardado no Windows (driver store).</summary>
public sealed class SerialDriverPackage
{
    public string PublishedName { get; init; } = "";  // oemXX.inf
    public string OriginalName { get; init; } = "";   // ch341ser.inf
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public SerialChip Chip { get; init; } = null!;
    public bool InUse { get; set; }
    public string Title => $"{Chip.Name} · {Provider} {Version}";
    public string Detail => $"{OriginalName} → {PublishedName} · {Date}" + (InUse ? " · EM USO" : "");
}

/// <summary>
/// RESET MAKER · drivers de CH340, CP210x, FTDI, Prolific e Arduino: diagnóstico pelo código de problema do Gerenciador de
/// Dispositivos, reinstalação do dispositivo, remoção de versões do driver (com backup) e instalação dos drivers oficiais embutidos no app.
/// Tudo via pnputil e SetupAPI, que já vêm no Windows. Requer administrador.
/// </summary>
public static class MakerDrivers
{
    public static readonly SerialChip[] Chips =
    {
        new("CH340/CH341", new[] { "1A86" }, new[] { "ch341ser.inf", "ch343ser.inf", "ch341s64.inf" }, new[] { "wch" },
            "https://www.wch-ic.com/downloads/CH341SER_EXE.html", "CH340"),
        new("CP210x", new[] { "10C4" }, new[] { "silabser.inf", "slabvcp.inf", "silabenm.inf" }, new[] { "silicon lab" },
            "https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers", "CP210x"),
        new("FTDI", new[] { "0403" }, new[] { "ftdibus.inf", "ftdiport.inf" }, new[] { "ftdi" },
            "https://ftdichip.com/drivers/vcp-drivers/", "FTDI"),
        new("Prolific PL2303", new[] { "067B" }, new[] { "ser2pl.inf" }, new[] { "prolific" },
            "https://www.prolific.com.tw/"),
        new("Arduino", new[] { "2341", "2A03" }, new[] { "arduino.inf", "genuino.inf", "arduino-org.inf", "linino-boards_amd64.inf" }, new[] { "arduino" },
            "https://www.arduino.cc/en/software", "Arduino"),
    };

    public static string BackupFolder => Path.Combine(AppSettings.Folder, "DriversSeriais");

    public static SerialChip? ChipByVid(string? vid) => vid == null ? null : Chips.FirstOrDefault(c => c.Vids.Contains(vid, StringComparer.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ dispositivos conectados

    public static List<SerialDeviceRow> ConnectedDevices()
    {
        var rows = new List<SerialDeviceRow>();
        foreach (var d in UsbCleaner.List().Where(d => d.Present && ChipByVid(d.Vid) != null))
        {
            var chip = ChipByVid(d.Vid)!;
            // FTDI tem dois nós (USB\ barramento + FTDIBUS\ porta): mostra só o da porta, a não ser que o de barramento tenha problema.
            if (chip.Name == "FTDI" && d.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && d.Problem == null
                && d.Pid != "0000") continue;
            var (provider, version, inf) = DriverInfo(d.DriverKey);
            var (ok, verdict, advice) = Diagnose(d, chip);
            rows.Add(new SerialDeviceRow
            {
                Device = d, Chip = chip, Ok = ok, Verdict = verdict, Advice = advice, Inf = inf,
                Driver = inf == null ? "sem driver" : $"{provider} {version} · {inf}".Trim(),
            });
        }
        return rows;
    }

    private static (string Provider, string Version, string? Inf) DriverInfo(string? driverKey)
    {
        if (string.IsNullOrEmpty(driverKey)) return ("", "", null);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
            return (k?.GetValue("ProviderName") as string ?? "", k?.GetValue("DriverVersion") as string ?? "", k?.GetValue("InfPath") as string);
        }
        catch { return ("", "", null); }
    }

    private static (bool Ok, string Verdict, string Advice) Diagnose(UsbDevice d, SerialChip chip)
    {
        if (chip.Name == "FTDI" && d.Pid == "0000")
            return (false, "Chip FTDI falsificado travado (PID 0000)",
                    "Um driver FTDI de 2014 zerou o identificador de chips FTDI falsificados. O Windows não reconhece mais a placa. " +
                    "Dá para recuperar regravando o PID 6001 com o FT_Prog (programa gratuito da FTDI) ou trocar o conversor.");
        return d.Problem switch
        {
            null => (true, d.Port != null ? $"Funcionando na {d.Port}" : "Funcionando", ""),
            28 => (false, "Sem driver (código 28)",
                   "Instale o driver em DRIVERS INCLUÍDOS, logo abaixo. Se o chip não estiver lá, use PÁGINA OFICIAL."),
            10 => (false, "O dispositivo não inicia (código 10)", chip.Name switch
            {
                "CH340/CH341" => "Comum em clones do CH340 com a versão mais nova do driver. Tente REINSTALAR. Se continuar, remova a " +
                                 "versão mais nova do driver CH340 na lista abaixo para o Windows usar a anterior, ou instale a da página oficial.",
                "FTDI" => "Pode ser um chip FTDI falsificado: os drivers FTDI recentes não funcionam com cópias. Remova a versão mais nova " +
                          "do driver FTDI na lista abaixo para voltar a uma anterior, ou troque por um conversor original/CH340.",
                "Prolific PL2303" => "Os drivers Prolific atuais recusam chips PL2303 antigos ou falsificados. Remova a versão mais nova " +
                                     "do driver na lista abaixo para voltar a uma anterior.",
                _ => "Tente REINSTALAR. Se continuar, troque o cabo e a porta USB (prefira portas traseiras, sem hub).",
            }),
            43 => (false, "O Windows parou o dispositivo (código 43)",
                   "Geralmente cabo ruim, só de carga, ou porta USB com pouca energia. Troque o cabo e a porta e clique em REINSTALAR."),
            22 => (false, "Dispositivo desativado (código 22)", "Clique em REINSTALAR para ativar de novo."),
            var p => (false, $"Problema no driver (código {p})", "Clique em REINSTALAR. Se não resolver, instale o driver incluído ou o da página oficial."),
        };
    }

    /// <summary>Remove o dispositivo e pede ao Windows para detectar de novo: ele reinstala com o melhor driver disponível.</summary>
    public static async Task<string> ReinstallAsync(UsbDevice d)
    {
        var (c1, o1) = await RunAsync("pnputil.exe", $"/remove-device \"{d.InstanceId}\"");
        await Task.Delay(800);
        var (_, _) = await RunAsync("pnputil.exe", "/scan-devices");
        return c1 == 0 ? "Reinstalado: o Windows detectou o dispositivo de novo." : $"pnputil não conseguiu remover: {LastLine(o1)}";
    }

    // ------------------------------------------------------------------ pacotes no driver store

    public static async Task<List<SerialDriverPackage>> DriverPackagesAsync(IEnumerable<SerialDeviceRow> connected)
    {
        var (code, xml) = await RunAsync("pnputil.exe", "/enum-drivers /format xml");
        var list = new List<SerialDriverPackage>();
        if (code != 0 || !xml.TrimStart().StartsWith("<")) return list; // Windows sem /format xml (10 antigo): só não lista
        var used = new HashSet<string>(connected.Select(r => r.Inf ?? ""), StringComparer.OrdinalIgnoreCase);
        foreach (var d in XDocument.Parse(xml).Root?.Elements("Driver") ?? Enumerable.Empty<XElement>())
        {
            string orig = d.Element("OriginalName")?.Value ?? "", prov = d.Element("ProviderName")?.Value ?? "";
            var chip = Chips.FirstOrDefault(c => c.Infs.Contains(orig, StringComparer.OrdinalIgnoreCase)
                                              || c.Providers.Any(p => prov.Contains(p, StringComparison.OrdinalIgnoreCase)) && d.Element("ClassName")?.Value is "Ports" or "USB");
            if (chip == null) continue;
            string[] dv = (d.Element("DriverVersion")?.Value ?? "").Split(' ', 2);
            string name = d.Attribute("DriverName")?.Value ?? "";
            list.Add(new SerialDriverPackage
            {
                PublishedName = name, OriginalName = orig, Provider = prov, Chip = chip,
                Date = dv.Length > 0 ? dv[0] : "", Version = dv.Length > 1 ? dv[1] : "", InUse = used.Contains(name),
            });
        }
        return list.OrderBy(p => p.Chip.Name).ThenByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version()).ToList();
    }

    /// <summary>Exporta o pacote para a pasta de backup e o remove do Windows (desinstalando dos dispositivos que o usam).</summary>
    public static async Task<string> RemovePackageAsync(SerialDriverPackage p)
    {
        string dir = Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(p.OriginalName)}-{p.Version}");
        Directory.CreateDirectory(dir);
        var (ce, oe) = await RunAsync("pnputil.exe", $"/export-driver {p.PublishedName} \"{dir}\"");
        if (ce != 0) return $"Backup falhou, nada foi removido: {LastLine(oe)}";
        var (cd, od) = await RunAsync("pnputil.exe", $"/delete-driver {p.PublishedName} /uninstall /force");
        await RunAsync("pnputil.exe", "/scan-devices");
        return cd == 0 ? $"{p.Title} removido (backup em {dir})." : $"Não foi possível remover: {LastLine(od)}";
    }

    public static bool HasBackups => Directory.Exists(BackupFolder) && Directory.EnumerateFiles(BackupFolder, "*.inf", SearchOption.AllDirectories).Any();

    /// <summary>Devolve ao Windows todos os drivers removidos por aqui.</summary>
    public static async Task<string> RestoreBackupsAsync()
    {
        var (c, o) = await RunAsync("pnputil.exe", $"/add-driver \"{Path.Combine(BackupFolder, "*.inf")}\" /subdirs /install");
        return c is 0 or 3010 ? "Drivers restaurados." + (c == 3010 ? " Reinicie o PC para finalizar." : "") : $"Falhou: {LastLine(o)}";
    }

    // ------------------------------------------------------------------ drivers oficiais embutidos no .exe

    private const string ResPrefix = "Drivers/";

    /// <summary>Recursos embutidos de um chip (nomes "Drivers/CH340/CH341SER.INF"; RecursiveDir pode vir com "\").</summary>
    private static List<string> BundleResources(SerialChip c)
    {
        if (c.Bundle == null) return new();
        string prefix = $"{ResPrefix}{c.Bundle}/";
        return typeof(MakerDrivers).Assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static List<BundledDriver> Bundled(IReadOnlyList<SerialDriverPackage> store)
    {
        var list = new List<BundledDriver>();
        foreach (var c in Chips)
        {
            var res = BundleResources(c);
            var infs = res.Where(n => n.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)).ToList();
            if (infs.Count == 0) continue;
            var versions = infs.Select(n => (Inf: Path.GetFileName(n.Replace('\\', '/')), Ver: InfVersion(n))).ToList();
            // Já instalado = o driver store tem o mesmo .inf original na mesma versão.
            bool installed = versions.All(v => store.Any(p => p.OriginalName.Equals(v.Inf, StringComparison.OrdinalIgnoreCase)
                                                            && p.Version == v.Ver.Version));
            list.Add(new BundledDriver
            {
                Chip = c, Installed = installed,
                Version = versions.Select(v => v.Ver.Version).Distinct().Count() == 1 ? versions[0].Ver.Version : $"{versions.Count} pacotes",
                Date = versions.Select(v => v.Ver.Date).Max() ?? "",
                Files = string.Join(", ", versions.Select(v => v.Inf)),
            });
        }
        return list;
    }

    /// <summary>Lê "DriverVer = 02/11/2026, 4.0.2026.02" do .inf embutido.</summary>
    private static (string Date, string Version) InfVersion(string resource)
    {
        using var s = typeof(MakerDrivers).Assembly.GetManifestResourceStream(resource);
        if (s == null) return ("", "");
        using var r = new StreamReader(s, Encoding.Default, detectEncodingFromByteOrderMarks: true);
        for (string? line; (line = r.ReadLine()) != null;)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*DriverVer\s*=\s*([^,]+),\s*([\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        }
        return ("", "");
    }

    /// <summary>
    /// Extrai o driver embutido para uma pasta temporária e instala com pnputil (caminho oficial do Windows). Funciona com a placa
    /// desconectada: o driver fica no Windows e é usado quando ela for plugada.
    /// </summary>
    public static async Task<string> InstallBundledAsync(SerialChip c)
    {
        var res = BundleResources(c);
        if (res.Count == 0) return $"O driver do {c.Name} não está incluído nesta versão.";
        string dir = Path.Combine(Path.GetTempPath(), "ResetExplode", "drivers-incluidos", c.Bundle!);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var n in res)
        {
            string rel = n.Replace('\\', '/')[($"{ResPrefix}{c.Bundle}/").Length..];
            string file = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var src = typeof(MakerDrivers).Assembly.GetManifestResourceStream(n)!;
            using var dst = File.Create(file);
            await src.CopyToAsync(dst);
        }
        var (code, output) = await RunAsync("pnputil.exe", $"/add-driver \"{Path.Combine(dir, "*.inf")}\" /subdirs /install");
        try { Directory.Delete(dir, true); } catch { }
        return code switch
        {
            0 => $"Driver do {c.Name} instalado.",
            3010 => $"Driver do {c.Name} instalado. Reinicie o PC para finalizar.",
            259 => $"Driver do {c.Name} adicionado ao Windows: será usado quando a placa for plugada.",
            _ => $"O Windows não aceitou o driver do {c.Name}: {LastLine(output)}",
        };
    }

    // ------------------------------------------------------------------ Arduino IDE

    /// <summary>Programas que costumam prender a porta COM (só auxiliares e terminais: a IDE em si não é fechada).</summary>
    public static readonly string[] PortHolders =
        { "serial-monitor", "serial-discovery", "arduino-cli", "putty", "CoolTerm", "ttermpro", "Cura", "UltiMaker-Cura",
          "PrusaSlicer", "Repetier-Host", "pronterface", "MobaXterm", "RealTerm", "Termite" };

    public static List<Process> RunningPortHolders() =>
        PortHolders.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return Array.Empty<Process>(); } }).ToList();

    public static bool IdeRunning => Process.GetProcessesByName("Arduino IDE").Length > 0;

    public static string? IdeVersion()
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Default),
                                             (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var sub in root?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var k = root!.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && n.StartsWith("Arduino IDE", StringComparison.OrdinalIgnoreCase))
                        return k.GetValue("DisplayVersion") as string ?? n;
                }
            }
            catch { }
        }
        return null;
    }

    private static string Arduino15 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arduino15");

    /// <summary>
    /// Apaga os índices baixados (package_index/library_index) e a pasta staging do Arduino15. Resolve a IDE travada em
    /// "Downloading index" ou erro ao instalar placas/bibliotecas. As placas e bibliotecas instaladas ficam.
    /// </summary>
    public static string ClearIdeCache()
    {
        if (!Directory.Exists(Arduino15)) return "Pasta Arduino15 não encontrada (a IDE ainda não foi aberta neste usuário).";
        int n = 0;
        foreach (var f in Directory.GetFiles(Arduino15, "*_index*.json*"))
            try { File.Delete(f); n++; } catch { }
        string staging = Path.Combine(Arduino15, "staging");
        long freed = 0;
        if (Directory.Exists(staging))
        {
            try { freed = new DirectoryInfo(staging).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            try { Directory.Delete(staging, true); } catch { }
        }
        return $"{n} índices apagados e {freed / 1048576.0:0} MB de downloads temporários liberados. A IDE baixa os índices de novo ao abrir.";
    }

    // ------------------------------------------------------------------ utilidades

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o + await e);
    }

    private static string LastLine(string s) =>
        s.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}
