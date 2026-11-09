using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// RADAR · rede como módulo do núcleo (Fase 3.2): ferramentas de traceroute e consumo por processo (ETW).
/// O teste de conexão clássico da mesma página continua em MainWindow.Net.cs até a Fase 3.3.
/// </summary>
public sealed class RadarModule(NetToolsView tools) : ModuleBase
{
    public const string RouteConexao = "radar/conexao";

    public override string Id => "radar";
    public override string DisplayName => "RADAR";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteConexao, "Rede", "", 800) };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin | ModuleCapabilities.UsesNetwork;

    /// <summary>Sair da página encerra o rastreio e a sessão ETW do monitor de consumo (nada de ETW rodando escondido).</summary>
    public override Task OnPageDeactivatedAsync(string route)
    {
        tools.StopAll();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        tools.StopAll();
        return ValueTask.CompletedTask;
    }
}
