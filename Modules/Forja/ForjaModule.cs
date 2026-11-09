using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// FORJA como módulo do núcleo (Fase 3.3g em diante). Nesta etapa: Stress test (CPU, RAM e GPU).
/// Benchmark e Overclock migram nas próximas fatias. O teste é alimentado pelo ciclo de 1 s da janela principal (Tick).
/// </summary>
public sealed class ForjaModule(StressView stress) : ModuleBase
{
    public const string RouteStress = "forja/stress";

    public override string Id => "forja";
    public override string DisplayName => "FORJA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteStress, "Stress test", "", 220, "FORJA") };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin; // sensores de baixo nível

    /// <summary>Ao fechar o app: para CPU, RAM e a janela de GPU sem perguntar.</summary>
    public override ValueTask ShutdownAsync()
    {
        stress.StopAll();
        return ValueTask.CompletedTask;
    }
}
