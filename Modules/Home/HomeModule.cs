using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>INÍCIO (Home) como módulo do núcleo: a página que abre o app e hospeda a mandala.</summary>
public sealed class HomeModule(HomeView view) : ModuleBase
{
    public const string RouteHome = "home/inicio";

    public override string Id => "home";
    public override string DisplayName => "INÍCIO";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteHome, "Início", "\uE80F", 0) };

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Open();
        return Task.CompletedTask;
    }
}
