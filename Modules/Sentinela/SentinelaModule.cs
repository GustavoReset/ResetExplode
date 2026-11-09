using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// SENTINELA como módulo do núcleo (Fases 3.3l e 3.3m): Sistema, Sensores, Placa de vídeo, Discos e Energia.
/// As Views são ligadas na abertura pelo Shell (Sistema lê o WMI em segundo plano assim que o app abre).
/// </summary>
public sealed class SentinelaModule(DisksView disks, EnergyView energy, SensorMonitor sensors) : ModuleBase
{
    /// <summary>Lastro: sensores da CPU respondendo e saúde S.M.A.R.T. dos discos (quando já lida).</summary>
    public override IEnumerable<IHealthIndicator> HealthIndicators { get; } = new IHealthIndicator[]
    {
        new HealthIndicator("sentinela.cpu-sensores", "Sensores da CPU", () => sensors.IsOpen ? 1 : 0, () =>
        {
            var p = sensors.DiagnoseCpuSensors();
            return p == SensorMonitor.CpuSensorProblem.None
                ? HealthIndicator.Bom("Temperatura e consumo disponíveis.")
                : HealthIndicator.Atencao(SensorMonitor.Explain(p).Split('.')[0] + ".", RouteSensores);
        }),
        new HealthIndicator("sentinela.discos", "Saúde dos discos", () => disks.LastCards.Count > 0 ? 3 : 0, () =>
        {
            var cards = disks.LastCards;
            int critical = cards.Count(c => c.Verdict == DiskVerdict.Critical);
            int warning = cards.Count(c => c.Verdict == DiskVerdict.Warning);
            if (critical > 0) return HealthIndicator.Ruim($"{critical} disco(s) com sinal de falha. Faça backup agora.", RouteDiscos, 10);
            if (warning > 0) return HealthIndicator.Atencao($"{warning} disco(s) pedem atenção.", RouteDiscos);
            return HealthIndicator.Bom($"{cards.Count} disco(s) sem problemas nas leituras disponíveis.");
        }),
    };

    /// <summary>Lê a saúde dos discos uma vez em segundo plano, para o Lastro ter o dado sem a pessoa abrir a página.</summary>
    public void PrimeDiskHealth() { if (disks.LastCards.Count == 0) disks.Refresh(); }

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
