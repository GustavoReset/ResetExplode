using FortKnox.Core.Config;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// ARSENAL como módulo do núcleo (Fase 3.3). Otimização, Inicialização e Instalar apps (Fases 3.3a e 3.3k).
/// A Otimização recebe o ciclo de 1 s (uso da RAM) e guarda o backup dos ajustes em AppSettings.TweakBackup.
/// </summary>
public sealed class ArsenalModule(OptimizeView optimize, StartupView startup, AppsView apps, Func<List<StartupBackup>> legacyRemoved, Action saveLegacy) : ModuleBase
{
    public const string RouteOptimize = "arsenal/otimizacao";
    public const string RouteStartup = "arsenal/inicializacao";
    public const string RouteApps = "arsenal/apps";
    public const string SettingsId = "arsenal";

    public override string Id => "arsenal";
    public override string DisplayName => "ARSENAL";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteOptimize, "Otimização", "\uE945", 500, "ARSENAL"),
        new NavEntry(RouteStartup, "Inicialização", "\uE7E8", 510, "ARSENAL"),
        new NavEntry(RouteApps, "Instalar apps", "\uE896", 520, "ARSENAL"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin | ModuleCapabilities.UsesNetwork;

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
        else if (route == RouteStartup) startup.Scan();
        else apps.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        apps.RequestStop();
        return ValueTask.CompletedTask;
    }
}
