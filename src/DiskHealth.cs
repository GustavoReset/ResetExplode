using System.Text.Json;

namespace ResetExplode;

/// <summary>Discos via lsblk e saúde via smartctl (smartmontools; precisa de root). Nada é escrito no disco.</summary>
public static class DiskHealth
{
    public static List<DiskHealthCard> ListDisks()
    {
        var list = new List<DiskHealthCard>();
        var json = Sys.Run("lsblk", "-J -b -o NAME,MODEL,SIZE,TYPE,ROTA,TRAN,SERIAL,MOUNTPOINTS,FSTYPE");
        if (json == null) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var d in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (Str(d, "type") != "disk" || Str(d, "name").StartsWith("zram")) continue;
            string name = Str(d, "name");
            bool rota = d.TryGetProperty("rota", out var r) && (r.ValueKind == JsonValueKind.True || r.ToString() is "1" or "true");
            string tran = Str(d, "tran");
            string kind = name.StartsWith("nvme") ? "NVMe SSD" : rota ? "HDD" : "SSD";
            long size = long.TryParse(Str(d, "size"), out var sz) ? sz : 0;
            var m = new List<KeyValuePair<string, string>>
            {
                new("Dispositivo", "/dev/" + name), new("Tipo", kind), new("Interface", tran == "" ? "—" : tran),
                new("Capacidade", Sys.Bytes(size)), new("Nº de série", Str(d, "serial") is { Length: > 0 } s ? s : "—"),
            };
            if (d.TryGetProperty("children", out var ch))
                foreach (var p in Walk(ch))
                {
                    string mp = p.TryGetProperty("mountpoints", out var mps) && mps.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", mps.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : "";
                    if (mp.Length == 0) continue;
                    m.Add(new("Partição " + Str(p, "name"), $"{mp} ({Str(p, "fstype")}, {Sys.Bytes(long.TryParse(Str(p, "size"), out var ps) ? ps : 0)})"));
                }
            list.Add(new DiskHealthCard
            {
                Title = Str(d, "model") is { Length: > 0 } mo ? mo.Trim() : name,
                Meta = $"{kind} · {Sys.Bytes(size)} · /dev/{name}", Metrics = m,
            });
        }
        return list;
    }

    private static IEnumerable<JsonElement> Walk(JsonElement arr)
    {
        foreach (var e in arr.EnumerateArray())
        {
            yield return e;
            if (e.TryGetProperty("children", out var c)) foreach (var x in Walk(c)) yield return x;
        }
    }

    private static string Str(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return "";
        return v.ValueKind == JsonValueKind.Null ? "" : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    public static List<DiskHealthCard> Read()
    {
        var cards = new List<DiskHealthCard>();
        bool smart = Sys.HasCommand("smartctl");
        foreach (var d in ListDisks())
        {
            string dev = d.Metrics.First(k => k.Key == "Dispositivo").Value;
            if (!smart) { cards.Add(Copy(d, DiskVerdict.Unknown, "Instale smartmontools (sudo apt install smartmontools) para ler o S.M.A.R.T.")); continue; }
            var o = Sys.Run("smartctl", $"-j -a {dev}", 15000);
            if (string.IsNullOrWhiteSpace(o)) { cards.Add(Copy(d, DiskVerdict.Unknown, "Sem resposta do smartctl; execute o app como root (sudo) para ler o S.M.A.R.T.")); continue; }
            try { cards.Add(FromSmart(d, JsonDocument.Parse(o).RootElement)); }
            catch (Exception ex) { cards.Add(Copy(d, DiskVerdict.Unknown, ex.Message)); }
        }
        return cards;
    }

    private static DiskHealthCard Copy(DiskHealthCard d, DiskVerdict v, string reason)
        => new() { Title = d.Title, Meta = d.Meta, Verdict = v, Reasons = reason, Metrics = d.Metrics };

    private static DiskHealthCard FromSmart(DiskHealthCard d, JsonElement j)
    {
        var m = new List<KeyValuePair<string, string>>(d.Metrics);
        var warn = new List<string>(); var crit = new List<string>();
        bool? passed = j.TryGetProperty("smart_status", out var ss) && ss.TryGetProperty("passed", out var p) ? p.GetBoolean() : null;
        if (passed == false) crit.Add("O disco reporta falha no S.M.A.R.T.: faça backup imediatamente.");
        if (j.TryGetProperty("temperature", out var t) && t.TryGetProperty("current", out var tc))
        {
            m.Add(new("Temperatura", tc.GetInt32() + " °C"));
            if (tc.GetInt32() >= 70) warn.Add("Temperatura alta.");
        }
        if (j.TryGetProperty("power_on_time", out var pot) && pot.TryGetProperty("hours", out var h)) m.Add(new("Horas ligado", h.GetInt64() + " h"));
        if (j.TryGetProperty("nvme_smart_health_information_log", out var nv))
        {
            if (nv.TryGetProperty("percentage_used", out var pu))
            {
                m.Add(new("Desgaste (vida usada)", pu.GetInt32() + " %"));
                if (pu.GetInt32() >= 90) crit.Add("Vida útil do SSD quase esgotada."); else if (pu.GetInt32() >= 70) warn.Add("SSD com desgaste elevado.");
            }
            if (nv.TryGetProperty("available_spare", out var sp)) m.Add(new("Reserva disponível", sp.GetInt32() + " %"));
            if (nv.TryGetProperty("media_errors", out var me) && me.GetInt64() > 0) crit.Add($"{me.GetInt64()} erros de mídia.");
            if (nv.TryGetProperty("data_units_written", out var w)) m.Add(new("Escrito (TB)", (w.GetInt64() * 512000.0 / 1e12).ToString("0.##")));
        }
        if (j.TryGetProperty("ata_smart_attributes", out var at) && at.TryGetProperty("table", out var tb))
            foreach (var a in tb.EnumerateArray())
            {
                int id = a.GetProperty("id").GetInt32();
                long raw = a.GetProperty("raw").GetProperty("value").GetInt64();
                string nm = a.GetProperty("name").GetString() ?? id.ToString();
                if (id is 5 or 197 or 198 && raw > 0) { warn.Add($"{nm}: {raw}."); m.Add(new(nm, raw.ToString())); }
                else if (id is 5 or 187 or 197 or 198 or 9 or 12 or 177 or 231 or 233) m.Add(new(nm, raw.ToString()));
            }
        var v = crit.Count > 0 ? DiskVerdict.Critical : warn.Count > 0 ? DiskVerdict.Warning : passed == true ? DiskVerdict.Good : DiskVerdict.Unknown;
        string reasons = crit.Concat(warn).Any() ? string.Join(" ", crit.Concat(warn)) : passed == true ? "S.M.A.R.T. sem alertas." : "Sem dados suficientes.";
        return new DiskHealthCard { Title = d.Title, Meta = d.Meta, Verdict = v, Reasons = reasons, Metrics = m };
    }
}
