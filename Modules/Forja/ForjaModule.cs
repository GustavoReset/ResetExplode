using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// FORJA como módulo do núcleo (Fase 3.3g em diante). Nesta etapa: Stress test (CPU, RAM e GPU) e Overclock.
/// Benchmark migra na próxima fatia. O Stress test é alimentado pelo ciclo de 1 s da janela principal (Tick).
/// </summary>
public sealed class ForjaModule(StressView stress, OcView oc) : ModuleBase
{
    public const string RouteStress = "forja/stress";
    public const string RouteOc = "forja/overclock";

    public override string Id => "forja";
    public override string DisplayName => "FORJA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteStress, "Stress test", "", 220, "FORJA"),
        new NavEntry(RouteOc, "Overclock", "", 230, "FORJA"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin; // sensores de baixo nível e NVML

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        if (route == RouteOc) oc.Activate();
        return Task.CompletedTask;
    }

    /// <summary>Ao fechar o app: para o Stress test e a verificação de overclock sem perguntar.</summary>
    public override ValueTask ShutdownAsync()
    {
        stress.StopAll();
        oc.CancelAll();
        return ValueTask.CompletedTask;
    }
}
