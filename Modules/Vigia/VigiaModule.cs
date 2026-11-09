using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// VIGIA (overlay de jogo) como módulo do núcleo (Fase 3.3f). Diferente dos outros, a View é ligada já na abertura
/// (<see cref="Prepare"/>), porque o atalho Ctrl+Shift+O liga o overlay mesmo sem visitar a página.
/// </summary>
public sealed class VigiaModule(OverlayView view) : ModuleBase
{
    public const string RouteOverlay = "vigia/overlay";
    public const string SettingsId = "vigia";

    public override string Id => "vigia";
    public override string DisplayName => "VIGIA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteOverlay, "Overlay", "", 840) };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin; // FPS por ETW

    /// <summary>Migra as preferências antigas (uma vez) e liga a View. Chamado pelo Shell quando os sensores e a GPU estão prontos.</summary>
    public static void Prepare(OverlayView view, IModuleContext ctx, LiveReadings live, FrameMonitor frames, AppSettings legacy, Action saveLegacy)
    {
        if (ctx.Settings.Migrate<VigiaSettings>(SettingsId, () => new VigiaSettings
            {
                OverlayCorner = legacy.OverlayCorner,
                OverlayScale = legacy.OverlayScale,
                OverlayItems = legacy.OverlayItems?.ToList(),
            }))
        {
            legacy.OverlayItems = null;
            saveLegacy();
            ctx.Log.Info("VIGIA: preferências do overlay migradas para modules.json");
        }
        view.Attach(live, frames, ctx.Settings.Section<VigiaSettings>(SettingsId));
    }

    public override ValueTask ShutdownAsync()
    {
        view.Shutdown();
        return ValueTask.CompletedTask;
    }
}
