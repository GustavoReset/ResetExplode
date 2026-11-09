using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// SENTINELA como módulo do núcleo (Fases 3.3l e 3.3m): Sistema, Sensores, Placa de vídeo, Discos e Energia.
/// As Views são ligadas na abertura pelo Shell (Sistema lê o WMI em segundo plano assim que o app abre).
/// </summary>
public sealed class SentinelaModule(DisksView disks, EnergyView energy) : ModuleBase
{
    public const string RouteSistema = "sentinela/sistema";
    public const string RouteSensores = "sentinela/sensores";
    public const string RouteGpu = "sentinela/gpu";
    public const string RouteDiscos = "sentinela/discos";
    public const string RouteEnergia = "sentinela/energia";

    public override string Id => "sentinela";
    public override string DisplayName => "SENTINELA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteSistema, "Sistema", "", 110, "SENTINELA"),
        new NavEntry(RouteSensores, "Sensores", "", 120, "SENTINELA"),
        new NavEntry(RouteGpu, "Placa de vídeo", "", 130, "SENTINELA"),
        new NavEntry(RouteDiscos, "Discos", "", 140, "SENTINELA"),
        new NavEntry(RouteEnergia, "Energia", "", 150, "SENTINELA"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin; // sensores de baixo nível (PawnIO)

    /// <summary>Discos e Energia releem ao abrir; Sensores e Placa de vídeo já estão ao vivo (ciclo de 1 s).</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        if (route == RouteDiscos) disks.Refresh();
        else if (route == RouteEnergia) energy.Refresh();
        return Task.CompletedTask;
    }
}
