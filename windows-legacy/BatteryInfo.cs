using System.Management;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// Bateria (desgaste, ciclos, carga) e energia (fonte, plano, consumo dos sensores). Lê o WMI do Windows; nada é alterado.
/// Em PC de mesa não há bateria: aparece só o cartão de energia.
/// </summary>
public static class BatteryInfo
{
    private static List<Dictionary<string, object?>> Q(string scope, string cls)
    {
        var list = new List<Dictionary<string, object?>>();
        try
        {
            using var s = new ManagementObjectSearcher(scope, "SELECT * FROM " + cls);
            foreach (ManagementObject o in s.Get())
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData p in o.Properties) d[p.Name] = p.Value;
                list.Add(d);
                o.Dispose();
            }
        }
        catch { }
        return list;
    }

    private static Dictionary<string, object?>? At(List<Dictionary<string, object?>> l, int i) => i < l.Count ? l[i] : null;

    private static long? L(Dictionary<string, object?>? d, string k)
    {
        if (d != null && d.TryGetValue(k, out var v) && v != null)
            try { return Convert.ToInt64(v); } catch { }
        return null;
    }

    private static bool? B(Dictionary<string, object?>? d, string k)
    {
        if (d != null && d.TryGetValue(k, out var v) && v is bool b) return b;
        return null;
    }

    private static string? S(Dictionary<string, object?>? d, string k)
        => d != null && d.TryGetValue(k, out var v) && v is string s && s.Trim().Length > 0 ? s.Trim() : null;

    private static string Chemistry(long? c) => c switch
    {
        3 => "Chumbo-ácido", 4 => "NiCd", 5 => "NiMH", 6 => "Íon-lítio", 7 => "Zinco-ar", 8 => "Polímero de lítio", _ => "—"
    };

    private static string Status(long? s) => s switch
    {
        1 => "Descarregando", 2 => "Na tomada", 3 => "Totalmente carregada", 4 => "Carga baixa", 5 => "Carga crítica",
        6 or 7 or 8 or 9 => "Carregando", 11 => "Parcialmente carregada", _ => "—"
    };

    public static List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null)
    {
        var cards = new List<DiskHealthCard>();
        var win = Q(@"root\cimv2", "Win32_Battery");
        var stat = Q(@"root\WMI", "BatteryStaticData");
        var full = Q(@"root\WMI", "BatteryFullChargedCapacity");
        var cyc = Q(@"root\WMI", "BatteryCycleCount");
        var live = Q(@"root\WMI", "BatteryStatus");
        int count = Math.Max(win.Count, stat.Count);
        bool onAc = false;

        for (int i = 0; i < count; i++)
        {
            var w = At(win, i); var st = At(stat, i); var fu = At(full, i); var cy = At(cyc, i); var lv = At(live, i);
            long? design = L(st, "DesignedCapacity");
            long? fullCap = L(fu, "FullChargedCapacity");
            long? cycles = L(cy, "CycleCount");
            if (cycles == 0) cycles = null; // muitos fabricantes não informam (vem 0)
            long? pct = L(w, "EstimatedChargeRemaining");
            long? bstat = L(w, "BatteryStatus");
            bool? ac = B(lv, "PowerOnline");
            bool charging = B(lv, "Charging") == true;
            bool discharging = B(lv, "Discharging") == true;
            if (ac == true) onAc = true;
            else if (ac == null && bstat is 2 or 3 or 6 or 7 or 8 or 9) onAc = true;

            double? wear = design is > 0 && fullCap is > 0 ? Math.Max(0, 100.0 * (1 - (double)fullCap.Value / design.Value)) : null;
            var reasons = new List<string>();
            var verdict = DiskVerdict.Good;
            string label = "SAUDÁVEL";
            if (wear == null) { verdict = DiskVerdict.Unknown; label = "SEM DADOS"; reasons.Add("O Windows não informou a capacidade de projeto ou a atual, então o desgaste não pôde ser calculado."); }
            else if (wear >= 50) { verdict = DiskVerdict.Critical; label = "DESGASTADA"; reasons.Add($"Perdeu {wear:0}% da capacidade original: a autonomia é metade do que era. Vale trocar a bateria."); }
            else if (wear >= 30) { verdict = DiskVerdict.Warning; label = "ATENÇÃO"; reasons.Add($"Perdeu {wear:0}% da capacidade original. Ainda funciona, mas a autonomia já caiu bastante."); }
            else if (wear >= 15) reasons.Add($"Desgaste de {wear:0}%, normal para uma bateria com uso.");
            else reasons.Add($"Desgaste de apenas {wear:0}%: bateria em ótimo estado.");
            if (cycles is >= 1000 && verdict == DiskVerdict.Good) { verdict = DiskVerdict.Warning; label = "ATENÇÃO"; }
            if (cycles is >= 800) reasons.Add($"{cycles} ciclos de carga: perto do fim da vida útil típica das baterias de íon-lítio (500 a 1000).");
            if (pct is < 20 && !onAc) reasons.Add("Carga baixa e fora da tomada.");

            var m = new List<KeyValuePair<string, string>>();
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) m.Add(new(k, v)); }
            Add("Carga atual", pct != null ? $"{pct}%" : null);
            Add("Estado", Status(bstat) is var s1 && s1 != "—" ? s1 : (charging ? "Carregando" : discharging ? "Descarregando" : null));
            long? run = L(w, "EstimatedRunTime");
            if (run is > 0 and < 71582788 && !onAc) Add("Autonomia estimada", $"{run / 60}h {run % 60:00}min");
            Add("Capacidade de projeto", design != null ? $"{design / 1000.0:0.0} Wh" : null);
            Add("Capacidade máxima atual", fullCap != null ? $"{fullCap / 1000.0:0.0} Wh" : null);
            Add("Desgaste", wear != null ? $"{wear:0.0}%" : null);
            Add("Ciclos de carga", cycles?.ToString());
            long? volt = L(lv, "Voltage");
            if (volt is > 0) Add("Tensão", $"{volt / 1000.0:0.00} V");
            long? cr = L(lv, "ChargeRate"), dr = L(lv, "DischargeRate");
            if (charging && cr is > 0) Add("Potência de carga", $"{cr / 1000.0:0.0} W");
            if (discharging && dr is > 0) Add("Consumo atual", $"{dr / 1000.0:0.0} W");
            Add("Fabricante", S(st, "ManufactureName"));
            Add("Modelo", S(st, "DeviceName") ?? S(w, "Name"));
            Add("Química", Chemistry(L(w, "Chemistry")) is var ch && ch != "—" ? ch : null);

            cards.Add(new DiskHealthCard
            {
                Title = count > 1 ? $"Bateria {i + 1}" : "Bateria",
                Meta = (S(st, "ManufactureName") ?? "") + (pct != null ? $" · {pct}% de carga" : ""),
                Verdict = verdict, Label = label,
                Reasons = string.Join(" ", reasons),
                Metrics = m,
            });
        }

        // Cartão de energia (existe em qualquer PC)
        string? plan = null;
        foreach (var p in Q(@"root\cimv2\power", "Win32_PowerPlan"))
            if (B(p, "IsActive") == true) { plan = S(p, "ElementName"); break; }
        var em = new List<KeyValuePair<string, string>>
        {
            new("Fonte de energia", count == 0 ? "Tomada (PC de mesa)" : onAc ? "Tomada" : "Bateria"),
        };
        if (plan != null) em.Add(new("Plano de energia", plan));
        if (sensors != null)
            foreach (var s in sensors.Where(x => x.Type == SensorType.Power && x.RawValue.GetValueOrDefault() > 0).Take(6))
                em.Add(new($"{s.Hardware} · {s.Name}", s.Value));
        string advice = plan != null && !plan.Contains("alto", StringComparison.OrdinalIgnoreCase) && !plan.Contains("high", StringComparison.OrdinalIgnoreCase)
            && !plan.Contains("máximo", StringComparison.OrdinalIgnoreCase) && !plan.Contains("ultimate", StringComparison.OrdinalIgnoreCase)
            ? " Para jogos e benchmarks, o plano Alto desempenho (aba RESET OTIMIZA) evita que o PC diminua o clock." : "";
        cards.Add(new DiskHealthCard
        {
            Title = "Energia",
            Meta = count == 0 ? "Nenhuma bateria detectada" : "Fonte, plano e consumo",
            Verdict = DiskVerdict.Good, Label = "EM USO",
            Reasons = (plan != null ? $"Plano de energia ativo: {plan}." : "Plano de energia não identificado.") + advice
                      + (count == 0 ? " O consumo da fonte (PSU) não é medido pelo Windows; os valores abaixo são dos sensores de CPU e GPU." : ""),
            Metrics = em,
        });
        return cards;
    }
}
