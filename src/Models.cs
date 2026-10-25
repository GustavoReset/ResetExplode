namespace ResetExplode;

public enum DiskVerdict { Good, Warning, Critical, Unknown }

/// <summary>Cartão de diagnóstico (disco, bateria, rede) com textos prontos para a tela e o relatório.</summary>
public sealed class DiskHealthCard
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Serial { get; init; } = "";
    public DiskVerdict Verdict { get; init; } = DiskVerdict.Unknown;
    public string? Label { get; init; }
    public string VerdictText => Label ?? Verdict switch
    {
        DiskVerdict.Good => "SAUDÁVEL",
        DiskVerdict.Warning => "ATENÇÃO",
        DiskVerdict.Critical => "CRÍTICO",
        _ => "SEM DADOS"
    };
    public string Reasons { get; init; } = "";
    public List<KeyValuePair<string, string>> Metrics { get; init; } = new();

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{VerdictText}] {Title}  ({Meta})");
        if (Reasons.Length > 0) sb.AppendLine("  " + Reasons);
        foreach (var m in Metrics) sb.AppendLine($"  {m.Key}: {m.Value}");
        return sb.ToString();
    }
}
