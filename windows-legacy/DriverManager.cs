using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ResetExplode;

/// <summary>Uma linha do inventário de drivers instalados.</summary>
public sealed class DriverRow
{
    public string Device { get; init; } = "";
    public string Class { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public bool Signed { get; init; }
    public string SignedText => Signed ? "assinado" : "sem assinatura";
}

/// <summary>Resultado da consulta de um driver de vídeo.</summary>
public sealed class GpuDriverStatus
{
    public string Vendor { get; set; } = "";       // NVIDIA / AMD / Intel / ""
    public string GpuName { get; set; } = "";
    public string Installed { get; set; } = "";    // versão amigável (ex.: 546.01)
    public string Latest { get; set; } = "";       // vazio se não foi possível consultar
    public string DownloadUrl { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string OfficialPage { get; set; } = "";
    public string Message { get; set; } = "";
    public bool CanAutoInstall => Vendor is "NVIDIA" or "AMD" && DownloadUrl.Length > 0;
    public bool UpdateAvailable =>
        Vendor == "NVIDIA" && Latest.Length > 0 && Installed.Length > 0 && CompareVersions(Latest, Installed) > 0;

    public static int CompareVersions(string a, string b)
    {
        static double V(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        return V(a).CompareTo(V(b));
    }
}

/// <summary>Placa-mãe / fabricante e as páginas oficiais de suporte.</summary>
public sealed class BoardInfo
{
    public string Maker { get; set; } = "";
    public string Model { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Brand { get; set; } = "";   // ASUS / MSI / Gigabyte / outro
    public string SupportUrl { get; set; } = "";
}

/// <summary>Um driver disponível no Windows Update.</summary>
public sealed class WuDriver
{
    public string Title { get; init; } = "";
    public long SizeBytes { get; init; }
    public object? Update { get; init; }
}

/// <summary>
/// Inventário, verificação de versão e instalação de drivers. Só NVIDIA tem API pública de versão e download direto;
/// AMD e os fabricantes de placa-mãe abrem a página oficial já com o modelo detectado. O Windows Update cobre o resto.
/// </summary>
public static class DriverManager
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ResetExplode");
        return c;
    }

    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "ResetExplode", "drivers");

    // ------------------------------------------------------------------ WMI

    private static string? Str(ManagementBaseObject o, string p) => o[p]?.ToString()?.Trim();

    private static string WmiDate(string? s)
    {
        try { if (!string.IsNullOrEmpty(s)) return ManagementDateTimeConverter.ToDateTime(s).ToString("dd/MM/yyyy"); } catch { }
        return "—";
    }

    /// <summary>Drivers instalados (sem os genéricos da Microsoft, exceto vídeo, rede e áudio).</summary>
    public static List<DriverRow> Inventory()
    {
        var list = new List<DriverRow>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, IsSigned FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get())
            {
                string name = Str(o, "DeviceName") ?? "";
                if (name.Length == 0) continue;
                string cls = Str(o, "DeviceClass") ?? "";
                string maker = Str(o, "Manufacturer") ?? "";
                bool main = cls is "DISPLAY" or "NET" or "MEDIA" or "BLUETOOTH" or "HDC" or "SCSIADAPTER" or "USB" or "SYSTEM";
                if (maker.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && cls is not ("DISPLAY" or "NET" or "MEDIA")) continue;
                if (!main) continue;
                list.Add(new DriverRow
                {
                    Device = name, Class = cls, Vendor = maker,
                    Version = Str(o, "DriverVersion") ?? "—",
                    Date = WmiDate(Str(o, "DriverDate")),
                    Signed = o["IsSigned"] is bool b && b
                });
                o.Dispose();
            }
        }
        catch { }
        return list.OrderBy(r => r.Class).ThenBy(r => r.Device).ToList();
    }

    /// <summary>"31.0.15.4601" (NVIDIA) vira "546.01".</summary>
    public static string NvidiaFriendly(string wmiVersion)
    {
        var p = wmiVersion.Split('.');
        if (p.Length != 4) return wmiVersion;
        string digits = p[2] + p[3];
        if (digits.Length < 5) return wmiVersion;
        digits = digits[^5..];
        return digits[..3] + "." + digits[3..];
    }

    // ------------------------------------------------------------------ GPU

    public static List<GpuDriverStatus> ReadGpus()
    {
        var list = new List<GpuDriverStatus>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                string name = Str(o, "Name") ?? "";
                string maker = (Str(o, "AdapterCompatibility") ?? "") + " " + name;
                string ver = Str(o, "DriverVersion") ?? "";
                if (name.Length == 0 || name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                var g = new GpuDriverStatus { GpuName = name };
                if (maker.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    g.Vendor = "NVIDIA"; g.Installed = NvidiaFriendly(ver);
                    g.OfficialPage = "https://www.nvidia.com/Download/index.aspx";
                }
                else if (maker.Contains("AMD", StringComparison.OrdinalIgnoreCase) || maker.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || maker.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
                {
                    g.Vendor = "AMD"; g.Installed = ver;
                    g.OfficialPage = "https://www.amd.com/en/support/download/drivers.html";
                }
                else if (maker.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    g.Vendor = "Intel"; g.Installed = ver;
                    g.OfficialPage = "https://www.intel.com/content/www/us/en/download-center/home.html";
                }
                else { g.Vendor = ""; g.Installed = ver; }
                list.Add(g);
                o.Dispose();
            }
        }
        catch { }
        return list;
    }

    /// <summary>Consulta a versão mais recente na NVIDIA (mesmo serviço da página oficial de drivers).</summary>
    public static async Task CheckNvidiaAsync(GpuDriverStatus g, bool studio, CancellationToken ct)
    {
        g.Latest = ""; g.DownloadUrl = ""; g.SizeText = "";
        try
        {
            var (psid, pfid) = await ResolveNvidiaIdsAsync(g.GpuName, ct);
            string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                         $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&beta=0&isWHQL=1&dch=1" +
                         $"&upCRD={(studio ? 1 : 0)}&qnf=0&sort1=1&numberOfResults=1";
            string json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            string? ver = null, dl = null, size = null;
            Find(doc.RootElement, ref ver, ref dl, ref size);
            if (string.IsNullOrEmpty(ver) || string.IsNullOrEmpty(dl))
            {
                g.Message = "A NVIDIA não devolveu um driver para esta placa. Use a página oficial.";
                return;
            }
            g.Latest = ver!.Trim();
            g.DownloadUrl = dl!.Replace("http://", "https://");
            g.SizeText = size ?? "";
            g.Message = g.UpdateAvailable ? $"Há um driver mais novo: {g.Latest} (instalado: {g.Installed})." : $"Você já está com o mais recente ({g.Installed}).";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { g.Message = "Não foi possível consultar a NVIDIA: " + ex.Message; }
    }


    /// <summary>
    /// AMD não tem API de versão: abre a página oficial do modelo (a URL segue um padrão) e procura o link direto do instalador.
    /// Se o site mudar ou bloquear, cai para abrir a página manualmente.
    /// </summary>
    public static async Task CheckAmdAsync(GpuDriverStatus g, CancellationToken ct)
    {
        g.Latest = ""; g.DownloadUrl = ""; g.SizeText = "";
        try
        {
            var m = Regex.Match(g.GpuName, @"RX\s*(\d{4})\s*(XTX|XT|GRE|M)?", RegexOptions.IgnoreCase);
            if (!m.Success) { g.Message = "AMD: modelo não reconhecido para consulta automática. Use a página oficial."; return; }
            int n = int.Parse(m.Groups[1].Value);
            string fam = $"radeon-rx-{n / 1000}000-series";
            string suffix = m.Groups[2].Success ? "-" + m.Groups[2].Value.ToLowerInvariant() : "";
            string page = $"https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/{fam}/amd-radeon-rx-{n}{suffix}.html";
            g.OfficialPage = page;
            using var req = new HttpRequestMessage(HttpMethod.Get, page);
            req.Headers.Referrer = new Uri("https://www.amd.com/");
            using var resp = await Http.SendAsync(req, ct);
            string html = await resp.Content.ReadAsStringAsync(ct);
            var hit = Regex.Match(html, @"https://drivers\.amd\.com/drivers/[^""'\s<>\\]*win11[^""'\s<>\\]*\.exe", RegexOptions.IgnoreCase);
            if (!hit.Success) hit = Regex.Match(html, @"https://drivers\.amd\.com/drivers/[^""'\s<>\\]*\.exe", RegexOptions.IgnoreCase);
            if (!hit.Success) { g.Message = "AMD: o site não devolveu o link do instalador (pode estar protegido). Abra a página oficial do seu modelo."; return; }
            g.DownloadUrl = hit.Value;
            var v = Regex.Match(hit.Value, @"edition-([\d.]+)", RegexOptions.IgnoreCase);
            g.Latest = v.Success ? v.Groups[1].Value : Path.GetFileNameWithoutExtension(hit.Value);
            g.Message = $"AMD Software Adrenalin {g.Latest} disponível para o seu modelo. Compare com a versão mostrada no AMD Software (o Windows mostra {g.Installed}).";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { g.Message = "Não foi possível consultar a AMD: " + ex.Message; }
    }

    private static void Find(JsonElement e, ref string? ver, ref string? dl, ref string? size)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    if (p.NameEquals("Version") && ver == null) ver = p.Value.GetString();
                    else if (p.NameEquals("DownloadURL") && dl == null) dl = p.Value.GetString();
                    else if (p.NameEquals("DownloadURLFileSize") && size == null) size = p.Value.GetString();
                }
                else Find(p.Value, ref ver, ref dl, ref size);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) Find(i, ref ver, ref dl, ref size);
    }

    /// <summary>Descobre os códigos de série (psid) e produto (pfid) da placa na base de drivers da NVIDIA.</summary>
    private static async Task<(int psid, int pfid)> ResolveNvidiaIdsAsync(string gpuName, CancellationToken ct)
    {
        bool mobile = Regex.IsMatch(gpuName, @"Laptop|Max-Q|Mobile|Notebook", RegexOptions.IgnoreCase);
        var m = Regex.Match(gpuName, @"(RTX|GTX|GT)\s*(\d{3,4})", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException("Modelo de placa não reconhecido: " + gpuName);
        string prefix = m.Groups[1].Value.ToUpperInvariant();
        int n = int.Parse(m.Groups[2].Value);

        string series;
        if (prefix == "RTX") series = $"RTX {n / 100} Series";
        else if (n >= 1600 && n < 1700) series = "16 Series";
        else if (n >= 1000 && n < 1100) series = "10 Series";
        else if (n >= 900 && n < 1000) series = "900 Series";
        else if (n >= 700 && n < 800) series = "700 Series";
        else series = $"{n / 100 * 100} Series";
        if (prefix == "RTX" && n >= 2000) series = $"RTX {n / 100} Series";

        string xml = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2&ParentID=1", ct);
        var seriesList = XDocument.Parse(xml).Descendants("LookupValue")
            .Select(x => (Name: (string?)x.Element("Name") ?? "", Value: (string?)x.Element("Value") ?? "")).ToList();
        var cand = seriesList.Where(s => s.Name.Contains(series, StringComparison.OrdinalIgnoreCase)
                                         && s.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == mobile).OrderBy(s => s.Name.Length).ToList();
        if (cand.Count == 0) throw new InvalidOperationException("Série da placa não encontrada na NVIDIA (" + series + ").");
        int psid = int.Parse(cand[0].Value);

        xml = await Http.GetStringAsync($"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID={psid}", ct);
        var prods = XDocument.Parse(xml).Descendants("LookupValue")
            .Select(x => (Name: (string?)x.Element("Name") ?? "", Value: (string?)x.Element("Value") ?? "")).ToList();
        if (prods.Count == 0) throw new InvalidOperationException("Nenhum produto listado para a série.");
        string key = Regex.Replace(gpuName, @"^NVIDIA\s+", "", RegexOptions.IgnoreCase).Replace("Laptop GPU", "").Trim();
        var exact = prods.FirstOrDefault(p => string.Equals(p.Name.Trim(), key, StringComparison.OrdinalIgnoreCase));
        var pick = exact.Value != null ? exact : prods.FirstOrDefault(p => p.Name.Contains(m.Value, StringComparison.OrdinalIgnoreCase));
        if (pick.Value == null) pick = prods[0]; // o driver é unificado por série: qualquer produto da série devolve o mesmo
        return (psid, int.Parse(pick.Value));
    }

    /// <summary>Baixa o instalador, confere a assinatura da NVIDIA e abre o instalador oficial.</summary>
    public static async Task<string> DownloadAndLaunchAsync(GpuDriverStatus g, Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadFolder);
        string file = Path.Combine(DownloadFolder, Path.GetFileName(new Uri(g.DownloadUrl).AbsolutePath));
        progress("Baixando o driver…", 0);
        using var dreq = new HttpRequestMessage(HttpMethod.Get, g.DownloadUrl);
        if (g.Vendor == "AMD") dreq.Headers.Referrer = new Uri("https://www.amd.com/");
        using (var resp = await Http.SendAsync(dreq, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[1 << 20]; long done = 0; int r;
            var sw = Stopwatch.StartNew();
            while ((r = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, r), ct);
                done += r;
                double mb = done / 1048576.0, speed = mb / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                progress($"Baixando… {mb:F0} de {(total > 0 ? (total / 1048576.0).ToString("F0") : "?")} MB · {speed:F1} MB/s",
                         total > 0 ? (double)done / total : 0);
            }
        }

        progress("Conferindo a assinatura digital…", 1);
        VerifyPublisher(file, g.Vendor == "AMD" ? "Advanced Micro Devices" : "NVIDIA");

        progress("Criando ponto de restauração (se o Windows permitir)…", 1);
        string restore = await TryRestorePointAsync();

        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = DownloadFolder });
        return $"Instalador {g.Latest} aberto (assinatura {g.Vendor} válida). {restore} Siga o instalador; a tela pode piscar durante a instalação.";
    }

    private static void VerifyPublisher(string path, string publisher)
    {
        int trust = CorsairSdkInstaller.WinVerifyTrustFile(path);
        if (trust != 0)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"Assinatura digital inválida (0x{trust:X8}). O arquivo foi descartado.");
        }
        string signer = "";
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
            signer = new X509Certificate2(cert).Subject;
#pragma warning restore SYSLIB0057
        }
        catch { }
        if (!signer.Contains(publisher, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"O arquivo está assinado por outro fabricante ({signer}). Foi descartado.");
        }
    }

    private static async Task<string> TryRestorePointAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'RESET EXPLODE - antes do driver' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? "Ponto de restauração criado." : "Ponto de restauração não criado (proteção do sistema desligada ou já criado hoje).";
        }
        catch { return "Ponto de restauração não criado."; }
    }

    // ------------------------------------------------------------------ Placa-mãe

    public static BoardInfo ReadBoard()
    {
        var b = new BoardInfo();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                foreach (ManagementObject o in s.Get()) { b.Maker = Str(o, "Manufacturer") ?? ""; b.Model = Str(o, "Product") ?? ""; break; }
            using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                foreach (ManagementObject o in s.Get()) { b.Bios = $"{Str(o, "SMBIOSBIOSVersion")} ({WmiDate(Str(o, "ReleaseDate"))})"; break; }
            string sys = "", sysModel = "";
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s.Get()) { sys = Str(o, "Manufacturer") ?? ""; sysModel = Str(o, "Model") ?? ""; break; }

            string all = b.Maker + " " + sys;
            string q = Uri.EscapeDataString(b.Model.Length > 0 && !b.Model.Contains("Default", StringComparison.OrdinalIgnoreCase) ? b.Model : sysModel);
            if (all.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            { b.Brand = "ASUS"; b.SupportUrl = "https://www.asus.com/support/download-center/"; }
            else if (all.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || all.Contains("MSI", StringComparison.OrdinalIgnoreCase))
            { b.Brand = "MSI"; b.SupportUrl = "https://www.msi.com/search/" + q; }
            else if (all.Contains("GIGABYTE", StringComparison.OrdinalIgnoreCase))
            { b.Brand = "Gigabyte"; b.SupportUrl = "https://www.gigabyte.com/Support"; }
            else if (all.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
            { b.Brand = "ASRock"; b.SupportUrl = "https://www.asrock.com/support/index.asp"; }
            else if (all.Contains("Dell", StringComparison.OrdinalIgnoreCase)) { b.Brand = "Dell"; b.SupportUrl = "https://www.dell.com/support/home/"; }
            else if (all.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)) { b.Brand = "Lenovo"; b.SupportUrl = "https://support.lenovo.com/"; }
            else if (all.Contains("HP", StringComparison.OrdinalIgnoreCase) || all.Contains("Hewlett", StringComparison.OrdinalIgnoreCase)) { b.Brand = "HP"; b.SupportUrl = "https://support.hp.com/"; }
            else if (all.Contains("Acer", StringComparison.OrdinalIgnoreCase)) { b.Brand = "Acer"; b.SupportUrl = "https://www.acer.com/support"; }
            if (b.Maker.Length == 0) b.Maker = sys;
            if (b.Model.Length == 0) b.Model = sysModel;
        }
        catch { }
        return b;
    }

    // ------------------------------------------------------------------ Windows Update

    private static dynamic NewCom(string progId)
        => Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("Serviço do Windows Update indisponível."))!;

    public static Task<List<WuDriver>> SearchWindowsUpdateAsync() => Task.Run(() =>
    {
        var list = new List<WuDriver>();
        dynamic session = NewCom("Microsoft.Update.Session");
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        foreach (dynamic u in result.Updates)
            list.Add(new WuDriver { Title = (string)u.Title, SizeBytes = (long)Convert.ToInt64(u.MaxDownloadSize), Update = u });
        return list;
    });

    public static Task<string> InstallWindowsUpdateAsync(List<WuDriver> items, Action<string> progress) => Task.Run(() =>
    {
        dynamic session = NewCom("Microsoft.Update.Session");
        dynamic coll = NewCom("Microsoft.Update.UpdateColl");
        foreach (var d in items) { dynamic u = d.Update!; try { u.AcceptEula(); } catch { } coll.Add(u); }
        progress("Baixando drivers do Windows Update…");
        dynamic dl = session.CreateUpdateDownloader();
        dl.Updates = coll;
        dl.Download();
        progress("Instalando…");
        dynamic inst = session.CreateUpdateInstaller();
        inst.Updates = coll;
        dynamic res = inst.Install();
        int code = (int)res.ResultCode; // 2 = sucesso, 3 = sucesso com erros
        bool reboot = (bool)res.RebootRequired;
        string r = code is 2 or 3 ? "Instalação concluída." : $"Instalação terminou com código {code}.";
        return r + (reboot ? " Reinicie o PC para finalizar." : "");
    });

    // ------------------------------------------------------------------ Backup

    /// <summary>Exporta todos os drivers de terceiros para uma pasta (pnputil). Serve para reinstalar depois de formatar.</summary>
    public static async Task<string> BackupAsync(Action<string> progress, CancellationToken ct)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                  "ResetExplode-Drivers", DateTime.Now.ToString("yyyyMMdd-HHmm"));
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("pnputil.exe", $"/export-driver * \"{dir}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        int n = 0;
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            if (line.Contains(".inf", StringComparison.OrdinalIgnoreCase)) progress($"Exportando drivers… {++n}");
        await p.WaitForExitAsync(ct);
        int folders = Directory.GetDirectories(dir).Length;
        return folders > 0 ? $"{folders} drivers salvos em {dir}" : "Nada foi exportado (execute o programa como administrador).";
    }
}
