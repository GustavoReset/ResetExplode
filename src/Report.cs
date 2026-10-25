using System.Text;

namespace ResetExplode;

public static class Report
{
    public static string Build(List<InfoNode> info, IEnumerable<SensorRow> sensors, IEnumerable<DiskHealthCard> extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RESET EXPLODE (Linux) — relatório de {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 60));
        void Walk(InfoNode n, int d) { sb.AppendLine(new string(' ', d * 2) + n.Display); foreach (var c in n.Children) Walk(c, d + 1); }
        foreach (var n in info) { Walk(n, 0); sb.AppendLine(); }
        sb.AppendLine("SENSORES");
        foreach (var g in sensors.GroupBy(s => s.Hardware))
        {
            sb.AppendLine(g.Key);
            foreach (var s in g) sb.AppendLine($"  {s.Category,-12} {s.Name,-28} {s.Value,12}  min {s.Min}  máx {s.Max}");
        }
        sb.AppendLine();
        foreach (var c in extra) sb.AppendLine(c.ToText());
        return sb.ToString();
    }
}
