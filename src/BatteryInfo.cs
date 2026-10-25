namespace ResetExplode;

/// <summary>Bateria e fonte de energia via /sys/class/power_supply.</summary>
public static class BatteryInfo
{
    public static List<DiskHealthCard> Read()
    {
        var cards = new List<DiskHealthCard>();
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return cards;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(x => x))
        {
            string type = Sys.Read(dir + "/type") ?? "";
            string name = Path.GetFileName(dir);
            if (type == "Mains" || type == "USB")
            {
                cards.Add(new DiskHealthCard
                {
                    Title = "Fonte: " + name, Meta = type, Verdict = DiskVerdict.Good, Label = Sys.Read(dir + "/online") == "1" ? "CONECTADA" : "DESCONECTADA",
                });
                continue;
            }
            if (type != "Battery") continue;
            double? full = Sys.ReadLong(dir + "/energy_full") ?? Sys.ReadLong(dir + "/charge_full");
            double? design = Sys.ReadLong(dir + "/energy_full_design") ?? Sys.ReadLong(dir + "/charge_full_design");
            double? wear = full != null && design is > 0 ? 100 - 100 * full / design : null;
            var m = new List<KeyValuePair<string, string>>
            {
                new("Fabricante", Sys.Read(dir + "/manufacturer") ?? "—"), new("Modelo", Sys.Read(dir + "/model_name") ?? "—"),
                new("Tecnologia", Sys.Read(dir + "/technology") ?? "—"), new("Estado", Sys.Read(dir + "/status") ?? "—"),
                new("Carga", (Sys.Read(dir + "/capacity") ?? "—") + " %"),
                new("Ciclos", Sys.Read(dir + "/cycle_count") ?? "—"),
            };
            if (wear != null) m.Add(new("Desgaste", $"{wear:0.#} %"));
            if (Sys.ReadLong(dir + "/power_now") is { } pw) m.Add(new("Consumo", $"{pw / 1e6:0.#} W"));
            var v = wear == null ? DiskVerdict.Unknown : wear >= 40 ? DiskVerdict.Critical : wear >= 20 ? DiskVerdict.Warning : DiskVerdict.Good;
            cards.Add(new DiskHealthCard
            {
                Title = "Bateria " + name, Meta = Sys.Read(dir + "/status") ?? "", Verdict = v,
                Reasons = wear == null ? "O firmware não informou a capacidade de projeto." : $"Capacidade atual em {100 - wear:0}% da original.",
                Metrics = m,
            });
        }
        return cards;
    }
}
