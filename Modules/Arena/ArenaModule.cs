using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>Dados da ARENA (seção "arena" do modules.json): perfil por jogo e jogos adicionados à mão.</summary>
public sealed class ArenaSettings
{
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();
}

/// <summary>
/// ARENA como módulo do núcleo (Fase 3.3e). Nesta etapa: Biblioteca (inicializador com perfil por jogo).
/// FPS estimado e Gargalo dependem do ciclo de 1 s dos sensores e migram junto com a SENTINELA.
/// </summary>
public sealed class ArenaModule(LauncherView launcher, Func<AppSettings> legacy, Action saveLegacy) : ModuleBase
{
    public const string RouteBiblioteca = "arena/biblioteca";
    public const string SettingsId = "arena";

    public override string Id => "arena";
    public override string DisplayName => "ARENA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteBiblioteca, "Biblioteca", "", 310, "ARENA") };

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        // Migração única: perfis e jogos manuais ficavam no settings.json antigo.
        var old = legacy();
        if ((old.GameProfiles.Count > 0 || old.CustomGames.Count > 0)
            && Context.Settings.Migrate<ArenaSettings>(SettingsId, () => new ArenaSettings
            {
                GameProfiles = new(old.GameProfiles),
                CustomGames = old.CustomGames.ToList(),
            }))
        {
            old.GameProfiles.Clear();
            old.CustomGames.Clear();
            saveLegacy();
            Context.Log.Info("ARENA: perfis de jogo migrados para modules.json");
        }
        launcher.Attach(Context.Settings.Section<ArenaSettings>(SettingsId));
        return Task.CompletedTask;
    }

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        launcher.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        launcher.CancelLaunch();
        return ValueTask.CompletedTask;
    }
}
