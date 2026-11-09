using FortKnox.Core.Changes;
using FortKnox.Core.Config;
using FortKnox.Core.Events;
using FortKnox.Core.Jobs;
using FortKnox.Core.State;

namespace FortKnox.Core.Modules;

/// <summary>Entrada do menu lateral. Route = "modulo/pagina" (ex.: "sentinela/sensores").</summary>
public sealed record NavEntry(string Route, string Title, string Glyph, int Order, string? Group = null);

[Flags]
public enum ModuleCapabilities
{
    None = 0,
    NeedsAdmin = 1,
    UsesNetwork = 2,
    RunsInGuarda = 4,
}

/// <summary>O que o núcleo entrega a cada módulo. Sem contêiner de DI de terceiros: as peças vêm prontas aqui.</summary>
public interface IModuleContext
{
    IEventBus Bus { get; }
    IStateStore State { get; }
    IJobRunner Jobs { get; }
    IChangeJournal Changes { get; }
    SettingsStore Settings { get; }
    ILog Log { get; }
    /// <summary>Pasta de dados do usuário (%AppData%\FortKnox).</summary>
    string DataFolder { get; }
}

/// <summary>Contrato de módulo (docs/ARQUITETURA.md §5.1). Módulo não conhece módulo: fala pelo barramento e pelo estado.</summary>
public interface IModule
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<NavEntry> Pages { get; }
    ModuleCapabilities Capabilities { get; }

    /// <summary>Leve. Roda na primeira visita a uma página do módulo (nada pesado na abertura do app).</summary>
    Task InitializeAsync(IModuleContext ctx, CancellationToken ct);
    Task OnPageActivatedAsync(string route, CancellationToken ct);
    /// <summary>Ao sair da página (ex.: COFRE tira as senhas da memória, RADAR para o ETW).</summary>
    Task OnPageDeactivatedAsync(string route);
    IEnumerable<IHealthIndicator> HealthIndicators { get; }
    ValueTask ShutdownAsync();
}

/// <summary>Publicado quando a página ativa muda (o Shell mostra a View; outros módulos podem reagir).</summary>
public sealed record PageChanged(string? From, string To) : IFortKnoxEvent;
