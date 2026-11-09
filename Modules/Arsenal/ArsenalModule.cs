using FortKnox.Core.Config;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// ARSENAL como módulo do núcleo (Fase 3.3). Otimização, Limpeza, Desinstalar, Inicialização e Instalar apps (Fases 3.3a, 3.3k, 3.4b e 3.4c).
/// A Otimização recebe o ciclo de 1 s (uso da RAM) e guarda o backup dos ajustes em AppSettings.TweakBackup.
/// </summary>
public sealed class ArsenalModule(OptimizeView optimize, CleanupView cleanup, UninstallView uninstall, StartupView startup, AppsView apps, Func<List<StartupBackup>> legacyRemoved, Action saveLegacy) : ModuleBase
{
    public const string RouteOptimize = "arsenal/otimizacao";
    public const string RouteCleanup = "arsenal/limpeza";
    public const string RouteUninstall = "arsenal/desinstalar";
    public const string RouteStartup = "arsenal/inicializacao";
    public const string RouteApps = "arsenal/apps";
    public const string SettingsId = "arsenal";

    public override string Id => "arsenal";
    public override string DisplayName => "ARSENAL";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteOptimize, "Otimização", "\uE945", 500, "ARSENAL"),
        new NavEntry(RouteCleanup, "Limpeza", "\uE74D", 505, "ARSENAL"),
        new NavEntry(RouteUninstall, "Desinstalar", "\uE7A6", 507, "ARSENAL"),
        new NavEntry(RouteStartup, "Inicialização", "\uE7E8", 510, "ARSENAL"),
        new NavEntry(RouteApps, "Instalar apps", "\uE896", 520, "ARSENAL"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin | ModuleCapabilities.UsesNetwork;

    /// <summary>Lastro: espaço livre no disco do Windows, Modo de Jogo e gravação em segundo plano (Game DVR).</summary>
    public override IEnumerable<IHealthIndicator> HealthIndicators => new IHealthIndicator[]
    {
        new HealthIndicator("arsenal.espaco-c", "Espaço no disco do Windows", 2, () =>
        {
            try
            {
                var c = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                double free = (double)c.AvailableFreeSpace / c.TotalSize;
                string gb = $"{c.AvailableFreeSpace / 1073741824.0:0.#} GB livres ({free:P0})";
                return free < 0.05 ? HealthIndicator.Ruim($"Só {gb} em {c.Name.TrimEnd('\\')}. O Windows fica lento e pode falhar ao atualizar.", RouteOptimize, 25)
                     : free < 0.15 ? HealthIndicator.Atencao($"{gb} em {c.Name.TrimEnd('\\')}.", RouteOptimize, 70)
                     : HealthIndicator.Bom($"{gb} em {c.Name.TrimEnd('\\')}.");
            }
            catch (Exception) { return HealthIndicator.Bom("Sem leitura do espaço livre."); }
        }),
        Tweak("arsenal.modo-jogo", "Modo de Jogo ligado", "gamemode", on: true,
              "O Windows prioriza o jogo.", "Desligado: ligue em Arsenal › Otimização."),
        Tweak("arsenal.game-dvr", "Gravação em segundo plano desligada", "gamedvr", on: true,
              "O Game Bar não grava o tempo todo.", "O Game DVR está gravando o tempo todo: custa FPS."),
    };

    private IHealthIndicator Tweak(string id, string title, string tweakId, bool on, string okText, string badText) =>
        new HealthIndicator(id, title, () => optimize.Tweaks.Any(t => t.Id == tweakId) ? 0.5 : 0, () =>
        {
            var t = optimize.Tweaks.First(x => x.Id == tweakId);
            try { t.Refresh(); } catch (Exception) { }
            return t.IsOn == on ? HealthIndicator.Bom(okText) : HealthIndicator.Atencao(badText, RouteOptimize, 70);
        });

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        // Migração única: "Restaurar removidos" saía do settings.json antigo (AppSettings.StartupRemoved).
        var legacy = legacyRemoved();
        if (legacy.Count > 0 && Context.Settings.Migrate<ArsenalSettings>(SettingsId, () => new ArsenalSettings { StartupRemoved = legacy.ToList() }))
        {
            legacy.Clear();
            saveLegacy();
            Context.Log.Info("ARSENAL: lista de removidos da inicialização migrada para modules.json");
        }
        startup.Attach(Context.Settings.Section<ArsenalSettings>(SettingsId));
        return Task.CompletedTask;
    }

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        if (route == RouteOptimize) optimize.Activate();
        else if (route == RouteCleanup) cleanup.Activate();
        else if (route == RouteUninstall) uninstall.Activate();
        else if (route == RouteStartup) startup.Scan();
        else apps.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        apps.RequestStop();
        cleanup.CancelAll();
        return ValueTask.CompletedTask;
    }
}
