using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// FORJA como módulo do núcleo (Fase 3.3g–3.3i): Benchmark (CPU, GPU, ray tracing, simulação, memória, disco),
/// Stress test (CPU, RAM e GPU) e Overclock. As três Views são ligadas na abertura pelo Shell; o Stress test e o painel ao vivo
/// do Benchmark são alimentados pelo ciclo de 1 s da janela principal (Tick).
/// </summary>
public sealed class ForjaModule(StressView stress, OcView oc, BenchView bench) : ModuleBase
{
    public const string RouteBench = "forja/benchmark";
    public const string RouteStress = "forja/stress";
    public const string RouteOc = "forja/overclock";

    public override string Id => "forja";
    public override string DisplayName => "FORJA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteBench, "Benchmark", "", 210, "FORJA"),
        new NavEntry(RouteStress, "Stress test", "", 220, "FORJA"),
        new NavEntry(RouteOc, "Overclock", "", 230, "FORJA"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin; // sensores de baixo nível e NVML

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        if (route == RouteOc) oc.Activate();
        return Task.CompletedTask;
    }

    /// <summary>Ao fechar o app: para benchmarks, Stress test e verificação de overclock sem perguntar.</summary>
    public override ValueTask ShutdownAsync()
    {
        bench.CancelAll();
        stress.StopAll();
        oc.CancelAll();
        return ValueTask.CompletedTask;
    }
}
