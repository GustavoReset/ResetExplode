using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>ALICERCE (drivers: inventário, NVIDIA/AMD verificados, Windows Update e backup) como módulo do núcleo (Fase 3.3c).</summary>
public sealed class AlicerceModule(DriversView view) : ModuleBase
{
    public const string RouteDrivers = "alicerce/drivers";

    public override string Id => "alicerce";
    public override string DisplayName => "ALICERCE";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteDrivers, "Drivers", "", 820) };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin | ModuleCapabilities.UsesNetwork;

    /// <summary>Primeira visita: varre na hora (a consulta à NVIDIA/AMD é sob demanda, só quando a página abre).</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        view.CancelAll();
        return ValueTask.CompletedTask;
    }
}
