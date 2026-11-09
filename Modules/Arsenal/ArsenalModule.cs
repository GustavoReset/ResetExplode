using FortKnox.Core.Config;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// ARSENAL como módulo do núcleo (Fase 3.3). Nesta etapa: Inicialização e Instalar apps.
/// Otimização continua em MainWindow (depende do ciclo de 1 s dos sensores) e migra junto com a SENTINELA.
/// </summary>
public sealed class ArsenalModule(StartupView startup, AppsView apps, Func<List<StartupBackup>> legacyRemoved, Action saveLegacy) : ModuleBase
{
    public const string RouteStartup = "arsenal/inicializacao";
    public const string RouteApps = "arsenal/apps";
    public const string SettingsId = "arsenal";

    public override string Id => "arsenal";
    public override string DisplayName => "ARSENAL";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RouteStartup, "Inicialização", "", 510, "ARSENAL"),
        new NavEntry(RouteApps, "Instalar apps", "", 520, "ARSENAL"),
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
        if (route == RouteStartup) startup.Scan();
        else apps.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        apps.RequestStop();
        return ValueTask.CompletedTask;
    }
}
