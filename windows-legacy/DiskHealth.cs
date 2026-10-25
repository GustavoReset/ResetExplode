using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

public enum DiskVerdict { Good, Warning, Critical, Unknown }

/// <summary>Cartão de saúde de um disco físico (já com os textos prontos para a tela e para o relatório).</summary>
public sealed class DiskHealthCard
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Serial { get; init; } = "";
    public DiskVerdict Verdict { get; init; } = DiskVerdict.Unknown;
    /// <summary>Texto do selo; quando vazio, usa o padrão de saúde de disco. (Também usado por Energia e Rede.)</summary>
    public string? Label { get; init; }
    public string VerdictText => Label ?? Verdict switch
    {
        DiskVerdict.Good => "SAUDÁVEL",
        DiskVerdict.Warning => "ATENÇÃO",
        DiskVerdict.Critical => "CRÍTICO",
        _ => "SEM DADOS"
    };
    /// <summary>Atenção, crítico e "sem dados" ganham destaque (selo preenchido); saudável fica só com contorno.</summary>
    public bool IsAlert => Verdict != DiskVerdict.Good;
    public string Reasons { get; init; } = "";
    public List<KeyValuePair<string, string>> Metrics { get; init; } = new();
}

/// <summary>
/// Lê a saúde dos discos pelo próprio Windows (API de armazenamento + S.M.A.R.T. do WMI) e, quando faltar dado,
/// completa com os sensores do LibreHardwareMonitor. Nada é escrito no disco.
/// </summary>
public static class DiskHealth
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    private sealed class Raw
    {
        public int Index;
        public string Model = "", Serial = "", Firmware = "", Bus = "", Media = "";
        public long Size;
        public int Health = 5;           // 0 saudável, 1 atenção, 2 não saudável, 5 desconhecido
        public string Pnp = "";
        public bool? PredictFail;
        public double? Temp, TempMax, Wear;
        public long? PowerOnHours, ReadErr, WriteErr, ReadErrUncorrected, WriteErrUncorrected, StartStop;
        public double? WrittenGb, ReadGb;
        public string Letters = "";
        public string? Error;
    }

    public static List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null)
    {
        var raws = new Dictionary<int, Raw>();

        // 1) Base: Win32_DiskDrive (existe em qualquer Windows)
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Index, Model, SerialNumber, FirmwareRevision, InterfaceType, Size, PNPDeviceID, MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject d in s.Get())
            {
                int idx = Convert.ToInt32(d["Index"] ?? 0);
                string pnp = d["PNPDeviceID"]?.ToString() ?? "";
                raws[idx] = new Raw
                {
                    Index = idx,
                    Model = d["Model"]?.ToString()?.Trim() ?? $"Disco {idx}",
                    Serial = d["SerialNumber"]?.ToString()?.Trim() ?? "",
                    Firmware = d["FirmwareRevision"]?.ToString()?.Trim() ?? "",
                    Bus = pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) ? "USB" : (d["InterfaceType"]?.ToString() ?? ""),
                    Size = Convert.ToInt64(d["Size"] ?? 0L),
                    Pnp = pnp,
                };
            }
        }
        catch (Exception ex) { return new List<DiskHealthCard> { ErrorCard(ex.Message) }; }

        // 2) API de armazenamento: tipo de mídia, saúde geral e contadores de confiabilidade (temperatura, desgaste, horas...)
        try
        {
            var scope = new ManagementScope(StorageScope);
            scope.Connect();
            var idByDeviceId = new Dictionary<string, int>();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
                foreach (ManagementObject d in s.Get())
                {
                    string devId = d["DeviceId"]?.ToString() ?? "";
                    if (!int.TryParse(devId, out int idx) || !raws.TryGetValue(idx, out var r)) continue;
                    idByDeviceId[devId] = idx;
                    r.Health = Convert.ToInt32(d["HealthStatus"] ?? 5);
                    r.Media = Convert.ToInt32(d["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                    int bus = Convert.ToInt32(d["BusType"] ?? 0);
                    r.Bus = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", _ => r.Bus };
                }

            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter")))
                foreach (ManagementObject c in s.Get())
                {
                    string devId = c["DeviceId"]?.ToString() ?? "";
                    if (!idByDeviceId.TryGetValue(devId, out int idx)) continue;
                    var r = raws[idx];
                    r.Temp = Num(c["Temperature"]);
                    r.TempMax = Num(c["TemperatureMax"]);
                    r.Wear = Num(c["Wear"]);
                    r.PowerOnHours = (long?)Num(c["PowerOnHours"]);
                    r.ReadErr = (long?)Num(c["ReadErrorsTotal"]);
                    r.WriteErr = (long?)Num(c["WriteErrorsTotal"]);
                    r.ReadErrUncorrected = (long?)Num(c["ReadErrorsUncorrected"]);
                    r.WriteErrUncorrected = (long?)Num(c["WriteErrorsUncorrected"]);
                    r.StartStop = (long?)Num(c["StartStopCycleCount"]);
                }
        }
        catch { /* sem a API de armazenamento (ou sem permissão): segue só com o resto */ }

        // 3) S.M.A.R.T. "falha prevista" (o disco avisa que vai falhar)
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject p in s.Get())
            {
                string inst = p["InstanceName"]?.ToString() ?? "";
                bool fail = p["PredictFailure"] is bool b && b;
                foreach (var r in raws.Values)
                    if (r.Pnp.Length > 0 && inst.StartsWith(r.Pnp, StringComparison.OrdinalIgnoreCase))
                        r.PredictFail = (r.PredictFail ?? false) || fail;
            }
        }
        catch { }

        // 4) Letras das unidades de cada disco
        try { FillLetters(raws); } catch { }

        // 5) Completa com os sensores do LibreHardwareMonitor
        if (sensors != null)
        {
            var list = sensors.Where(x => x.HardwareType == HardwareType.Storage).ToList();
            foreach (var r in raws.Values)
            {
                var mine = list.Where(x => x.Hardware.Contains(r.Model, StringComparison.OrdinalIgnoreCase) ||
                                           r.Model.Contains(x.Hardware, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count == 0) continue;
                if (r.Temp == null)
                    r.Temp = mine.FirstOrDefault(x => x.Type == SensorType.Temperature && x.RawValue is > 0)?.RawValue;
                if (r.Wear == null)
                {
                    var used = mine.FirstOrDefault(x => x.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase))?.RawValue;
                    var left = mine.FirstOrDefault(x => x.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase))?.RawValue;
                    if (used != null) r.Wear = used;
                    else if (left != null) r.Wear = 100 - left;
                }
                r.WrittenGb = mine.FirstOrDefault(x => x.Name.Contains("Data Written", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Name.Contains("Host Writes", StringComparison.OrdinalIgnoreCase))?.RawValue;
                r.ReadGb = mine.FirstOrDefault(x => x.Name.Contains("Data Read", StringComparison.OrdinalIgnoreCase) ||
                                                    x.Name.Contains("Host Reads", StringComparison.OrdinalIgnoreCase))?.RawValue;
            }
        }

        return raws.Values.OrderBy(r => r.Index).Select(ToCard).ToList();
    }

    private static double? Num(object? v)
    {
        if (v == null) return null;
        try
        {
            double d = Convert.ToDouble(v);
            // 0xFFFF.. / valores gigantes = "não suportado" em vários drivers
            return d < 0 || d > 1e12 ? null : d;
        }
        catch { return null; }
    }

    private static DiskHealthCard ErrorCard(string msg) => new()
    {
        Title = "Não foi possível ler os discos",
        Verdict = DiskVerdict.Unknown,
        Reasons = msg,
    };

    private static void FillLetters(Dictionary<int, Raw> raws)
    {
        var diskOfPart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            foreach (ManagementObject a in s.Get())
                if (RefId(a["Antecedent"]) is string disk && RefId(a["Dependent"]) is string part) diskOfPart[part] = disk;

        var letters = new Dictionary<int, List<string>>();
        using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            foreach (ManagementObject a in s.Get())
                if (RefId(a["Antecedent"]) is string part && RefId(a["Dependent"]) is string letter &&
                    diskOfPart.TryGetValue(part, out var disk))
                {
                    var m = Regex.Match(disk, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    int idx = int.Parse(m.Groups[1].Value);
                    if (!letters.TryGetValue(idx, out var l)) letters[idx] = l = new List<string>();
                    l.Add(letter);
                }
        foreach (var kv in letters)
            if (raws.TryGetValue(kv.Key, out var r)) r.Letters = string.Join(" ", kv.Value.Distinct().OrderBy(x => x));
    }

    private static string? RefId(object? reference)
    {
        var m = Regex.Match(reference?.ToString() ?? "", "DeviceID=\"(.*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }

    private static DiskHealthCard ToCard(Raw r)
    {
        bool isSsd = r.Media == "SSD" || r.Bus == "NVMe";
        bool isUsb = r.Bus == "USB";
        var crit = new List<string>();
        var warn = new List<string>();
        bool anyData = r.Health != 5 || r.PredictFail != null || r.Wear != null || r.Temp != null || r.PowerOnHours != null;

        if (r.PredictFail == true) crit.Add("O próprio disco avisa (S.M.A.R.T.) que deve falhar em breve. Faça backup agora.");
        if (r.Health == 2) crit.Add("O Windows classifica o disco como NÃO saudável.");
        else if (r.Health == 1) warn.Add("O Windows classifica a saúde do disco como \"atenção\".");

        if (r.Wear is double w)
        {
            if (w >= 95) crit.Add($"Desgaste de {w:0}%: o SSD está no fim da vida útil. Troque e faça backup.");
            else if (w >= 80) warn.Add($"Desgaste de {w:0}%: o SSD já gastou boa parte da vida útil. Planeje a troca.");
        }
        if (r.Temp is double t)
        {
            double okMax = isSsd ? 70 : 55, critMax = isSsd ? 85 : 65;
            if (t >= critMax) crit.Add($"Temperatura de {t:0} °C é muito alta para este disco.");
            else if (t >= okMax) warn.Add($"Temperatura de {t:0} °C está alta. Melhore a ventilação.");
        }
        if (r.ReadErrUncorrected > 0 || r.WriteErrUncorrected > 0)
            warn.Add($"Erros não corrigidos: {r.ReadErrUncorrected ?? 0} de leitura e {r.WriteErrUncorrected ?? 0} de escrita.");

        DiskVerdict v = crit.Count > 0 ? DiskVerdict.Critical
                      : warn.Count > 0 ? DiskVerdict.Warning
                      : anyData ? DiskVerdict.Good : DiskVerdict.Unknown;

        var reasons = crit.Concat(warn).ToList();
        if (reasons.Count == 0)
            reasons.Add(v == DiskVerdict.Good
                ? "Nenhum problema encontrado nas leituras disponíveis."
                : "Este disco não informa dados de saúde (comum em pendrives e alguns HDs externos).");

        string kind = r.Media.Length > 0 ? r.Media : (isSsd ? "SSD" : "");
        var meta = new[] { kind, r.Bus, FmtSize(r.Size), r.Letters.Length > 0 ? "Unidades: " + r.Letters : "" }
                   .Where(x => !string.IsNullOrEmpty(x));

        var metrics = new List<KeyValuePair<string, string>>();
        void M(string k, string? val) { if (val != null) metrics.Add(new(k, val)); }
        M("Temperatura", r.Temp is double tt ? $"{tt:0} °C" + (r.TempMax is double tm && tm > 0 ? $" (máx. registrado {tm:0})" : "") : null);
        if (r.Wear is double wr) { M("Desgaste", $"{wr:0}%"); M("Vida útil restante", $"{Math.Max(0, 100 - wr):0}%"); }
        M("Horas ligado", r.PowerOnHours is long h ? $"{h:N0} h ({h / 24.0:N0} dias)" : null);
        M("Ciclos liga/desliga", r.StartStop is long ss ? $"{ss:N0}" : null);
        M("Erros de leitura", r.ReadErr is long re ? $"{re:N0}" + (r.ReadErrUncorrected is long ru && ru > 0 ? $" ({ru:N0} não corrigidos)" : "") : null);
        M("Erros de escrita", r.WriteErr is long we ? $"{we:N0}" + (r.WriteErrUncorrected is long wu && wu > 0 ? $" ({wu:N0} não corrigidos)" : "") : null);
        M("Total gravado", r.WrittenGb is double wg && wg > 0 ? $"{wg:N0} GB" : null);
        M("Total lido", r.ReadGb is double rg && rg > 0 ? $"{rg:N0} GB" : null);
        M("Falha prevista (S.M.A.R.T.)", r.PredictFail is bool pf ? (pf ? "SIM" : "Não") : null);
        M("Saúde segundo o Windows", r.Health switch { 0 => "Saudável", 1 => "Atenção", 2 => "Não saudável", _ => null });
        M("Firmware", r.Firmware.Length > 0 ? r.Firmware : null);

        return new DiskHealthCard
        {
            Index = r.Index,
            Title = r.Model,
            Meta = string.Join(" · ", meta),
            Serial = r.Serial,
            Verdict = isUsb && !anyData ? DiskVerdict.Unknown : v,
            Reasons = string.Join(" ", reasons),
            Metrics = metrics,
        };
    }

    private static string FmtSize(long b)
    {
        double v = b; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return b > 0 ? $"{v:0.#} {u[i]}" : "";
    }
}
