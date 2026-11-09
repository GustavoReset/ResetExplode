using FortKnox.Core.Config;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// ARSENAL como módulo do núcleo (Fase 3.3). Otimização, Limpeza, Desinstalar, Inicialização, Instalar apps e Atualizar apps (Fases 3.3a, 3.3k, 3.4b, 3.4c e 3.4d).
/// A Otimização recebe o ciclo de 1 s (uso da RAM); ajustes e removidos da inicialização são ferrolhos (ArsenalChanges).
/// </summary>
public sealed class ArsenalModule(OptimizeView optimize, CleanupView cleanup, UninstallView uninstall, StartupView startup, AppsView apps, UpdatesView updates, Func<AppSettings> legacy, Action saveLegacy) : ModuleBase
{
    public const string RouteOptimize = "arsenal/otimizacao";
    public const string RouteCleanup = "arsenal/limpeza";
    public const string RouteUninstall = "arsenal/desinstalar";
    public const string RouteStartup = "arsenal/inicializacao";
    public const string RouteApps = "arsenal/apps";
    public const string RouteUpdates = "arsenal/atualizar";
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
        new NavEntry(RouteUpdates, "Atualizar apps", "\uE895", 525, "ARSENAL"),
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

    protected override async Task OnInitializeAsync(CancellationToken ct)
    {
        ArsenalChanges.Register(Context.Changes, optimize.Tweaks);

        // Migração única (Fase 3.4e): o TweakBackup e os removidos da inicialização (settings.json antigo e a seção
        // "arsenal" do modules.json) viram ferrolhos no Diário. Depois disso as listas antigas ficam vazias.
        var old = legacy();
        var prefs = Context.Settings.Section<ArsenalSettings>(SettingsId);
        var removed = old.StartupRemoved.Concat(prefs.Value.StartupRemoved).ToList();
        var tweaks = new TweakBackup(old.TweakBackup);
        if (tweaks.Count > 0 || removed.Count > 0)
        {
            try
            {
                int n = await ArsenalChanges.MigrateLegacyAsync(Context.Changes, tweaks, removed);
                old.TweakBackup.Clear();
                old.StartupRemoved.Clear();
                saveLegacy();
                prefs.Update(p => p.StartupRemoved.Clear());
                Context.Log.Info($"ARSENAL: {n} cópia(s) antiga(s) de ajustes e inicialização migrada(s) para o Diário de Ferrolhos");
            }
            catch (Exception ex) { Context.Log.Error("ARSENAL: migração para o Diário de Ferrolhos falhou (as listas antigas ficam)", ex); }
        }
        optimize.Attach(Context.Changes);
        startup.Attach(Context.Changes);
    }

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        if (route == RouteOptimize) optimize.Activate();
        else if (route == RouteCleanup) cleanup.Activate();
        else if (route == RouteUninstall) uninstall.Activate();
        else if (route == RouteStartup) startup.Scan();
        else if (route == RouteUpdates) updates.Activate();
        else apps.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        apps.RequestStop();
        updates.RequestStop();
        cleanup.CancelAll();
        return ValueTask.CompletedTask;
    }
}
