using FortKnox.Core.Events;
using FortKnox.Core.State;

namespace FortKnox.Core.Modules;

/// <summary>As três palavras do Tom de voz. (Na interface, FortKnox.UI.HealthStatus acrescenta Info e Neutro.)</summary>
public enum HealthLevel { Bom, Atencao, Ruim }

/// <summary>Uma leitura de saúde: nota 0..100, motivo em linguagem clara e a rota da tela que resolve.</summary>
public sealed record HealthReading(HealthLevel Level, double Score, string Reason, string? FixRoute = null);

public interface IHealthIndicator
{
    string Id { get; }
    string Title { get; }
    /// <summary>Peso na nota Lastro (ex.: tempo real desligado pesa mais que inicialização lenta).</summary>
    double Weight { get; }
    HealthReading Read();
}

public sealed record IndicatorResult(string Id, string Title, double Weight, HealthReading Reading);

/// <summary>Fatia de estado com a nota Lastro (PAINEL lê; ninguém mais grava).</summary>
public sealed record HealthState : IStateSlice
{
    public int Lastro { get; init; } = -1; // -1 = ainda não calculado
    public HealthLevel Level { get; init; } = HealthLevel.Bom;
    public IReadOnlyList<IndicatorResult> Worst { get; init; } = Array.Empty<IndicatorResult>();
    public DateTime? CalculatedAt { get; init; }
}

public sealed record LastroCalculated(HealthState State) : IFortKnoxEvent;

/// <summary>
/// Lastro (0 a 100): média ponderada das notas dos indicadores. Qualquer indicador Ruim limita a nota a 59
/// (um problema sério não some na média) e define o nível geral. Indicador que lança exceção é ignorado e logado.
/// </summary>
public static class Lastro
{
    public const int RuimCap = 59;

    public static HealthState Calculate(IEnumerable<IHealthIndicator> indicators, ILog? log = null, DateTime? now = null)
    {
        var results = new List<IndicatorResult>();
        foreach (var i in indicators)
        {
            try
            {
                if (i.Weight <= 0) continue;
                var r = i.Read();
                results.Add(new IndicatorResult(i.Id, i.Title, i.Weight, r with { Score = Math.Clamp(r.Score, 0, 100) }));
            }
            catch (Exception ex) { (log ?? NullLog.Instance).Warn($"Indicador {i.Id} falhou", ex); }
        }
        if (results.Count == 0) return new HealthState { Lastro = -1, CalculatedAt = now ?? DateTime.Now };

        double avg = results.Sum(r => r.Reading.Score * r.Weight) / results.Sum(r => r.Weight);
        var level = results.Max(r => r.Reading.Level);  // Bom < Atencao < Ruim
        int score = (int)Math.Round(avg);
        if (level == HealthLevel.Ruim) score = Math.Min(score, RuimCap);
        return new HealthState
        {
            Lastro = score,
            Level = level,
            Worst = results.OrderByDescending(r => r.Reading.Level).ThenBy(r => r.Reading.Score).Take(3).ToList(),
            CalculatedAt = now ?? DateTime.Now,
        };
    }
}
