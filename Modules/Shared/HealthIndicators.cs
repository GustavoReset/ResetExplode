using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// Indicador de saúde simples: lê na hora (chamado na thread da interface, no máximo 1×/min pelo PAINEL).
/// Peso 0 = "sem leitura agora" (ex.: discos ainda não lidos): o indicador fica fora da nota em vez de inflá-la.
/// </summary>
public sealed class HealthIndicator(string id, string title, Func<double> weight, Func<HealthReading> read) : IHealthIndicator
{
    public HealthIndicator(string id, string title, double weight, Func<HealthReading> read) : this(id, title, () => weight, read) { }

    public string Id => id;
    public string Title => title;
    public double Weight => weight();
    public HealthReading Read() => read();

    /// <summary>Notas padrão por nível (Tom de voz: Bom, Atenção, Ruim).</summary>
    public static HealthReading Bom(string reason, string? fix = null) => new(HealthLevel.Bom, 100, reason, fix);
    public static HealthReading Atencao(string reason, string? fix = null, double score = 60) => new(HealthLevel.Atencao, score, reason, fix);
    public static HealthReading Ruim(string reason, string? fix = null, double score = 20) => new(HealthLevel.Ruim, score, reason, fix);
}
