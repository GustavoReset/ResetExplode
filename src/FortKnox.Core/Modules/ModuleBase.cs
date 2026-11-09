namespace FortKnox.Core.Modules;

/// <summary>Base com os padrões do contrato: o módulo só sobrescreve o que usa.</summary>
public abstract class ModuleBase : IModule
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<NavEntry> Pages { get; }
    public virtual ModuleCapabilities Capabilities => ModuleCapabilities.None;
    public virtual IEnumerable<IHealthIndicator> HealthIndicators => Array.Empty<IHealthIndicator>();

    /// <summary>Contexto do núcleo; disponível depois de InitializeAsync.</summary>
    protected IModuleContext Context { get; private set; } = null!;

    public Task InitializeAsync(IModuleContext ctx, CancellationToken ct)
    {
        Context = ctx;
        return OnInitializeAsync(ct);
    }

    protected virtual Task OnInitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnPageActivatedAsync(string route, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnPageDeactivatedAsync(string route) => Task.CompletedTask;
    public virtual ValueTask ShutdownAsync() => ValueTask.CompletedTask;
}
