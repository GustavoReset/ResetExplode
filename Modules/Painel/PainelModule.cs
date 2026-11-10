using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>DASHBOARD (visão geral) como módulo do núcleo (Fase 3.3n). Recebe uma amostra por segundo pelo SensorPump.</summary>
public sealed class PainelModule(DashView view) : ModuleBase
{
    public const string RouteVisao = "painel/visao";

    public override string Id => "painel";
    public override string DisplayName => "DASHBOARD";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteVisao, "Dashboard", "", 0) };

    /// <summary>Ao abrir: redesenha tudo (gráfico, notas, eventos e saúde do PC).</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Refresh();
        return Task.CompletedTask;
    }
}
