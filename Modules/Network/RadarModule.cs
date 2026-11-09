using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// RADAR · rede como módulo do núcleo (Fases 3.2 e 3.3m): teste de conexão (latência, jitter, perda, velocidade, bufferbloat),
/// traceroute e consumo por processo (ETW), todos na página <see cref="RadarView"/>.
/// </summary>
public sealed class RadarModule(RadarView view) : ModuleBase
{
    public const string RouteConexao = "radar/conexao";

    public override string Id => "radar";
    public override string DisplayName => "RADAR";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteConexao, "Rede", "", 800) };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin | ModuleCapabilities.UsesNetwork;

    /// <summary>Sair da página encerra o rastreio e a sessão ETW do monitor de consumo (nada de ETW rodando escondido).</summary>
    public override Task OnPageDeactivatedAsync(string route)
    {
        view.Tools.StopAll();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        view.CancelAll();
        view.Tools.StopAll();
        return ValueTask.CompletedTask;
    }
}
