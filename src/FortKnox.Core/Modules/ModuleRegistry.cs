namespace FortKnox.Core.Modules;

/// <summary>
/// Registro de módulos do Shell: monta o menu a partir das páginas de cada módulo, inicializa o módulo na primeira visita,
/// avisa ativação/desativação de página e calcula o Lastro. Substitui o array do ShowPage e o switch do Nav_Checked
/// à medida que os módulos migram (Fase 3.2 em diante).
/// </summary>
public sealed class ModuleRegistry
{
    private readonly IModuleContext _ctx;
    private readonly Dictionary<string, IModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _init = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _nav = new(1, 1);
    private string? _activeRoute;

    public ModuleRegistry(IModuleContext ctx) => _ctx = ctx;

    public IReadOnlyCollection<IModule> Modules => _modules.Values;
    public string? ActiveRoute => _activeRoute;

    public void Add(IModule module)
    {
        if (!_modules.TryAdd(module.Id, module)) throw new InvalidOperationException($"Módulo \"{module.Id}\" registrado duas vezes.");
        foreach (var p in module.Pages)
            if (!p.Route.StartsWith(module.Id + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A rota \"{p.Route}\" deveria começar com \"{module.Id}/\".");
    }

    /// <summary>Menu: páginas de todos os módulos, na ordem declarada.</summary>
    public IReadOnlyList<(IModule Module, NavEntry Page)> Navigation() =>
        _modules.Values.SelectMany(m => m.Pages.Select(p => (m, p))).OrderBy(x => x.p.Order).ToList();

    public IModule? Owner(string route)
    {
        int slash = route.IndexOf('/');
        return _modules.GetValueOrDefault(slash < 0 ? route : route[..slash]);
    }

    /// <summary>Troca de página: desativa a anterior, inicializa o módulo se for a primeira vez, ativa a nova.</summary>
    public async Task NavigateAsync(string route, CancellationToken ct = default)
    {
        var target = Owner(route) ?? throw new KeyNotFoundException($"Nenhum módulo para a rota \"{route}\".");
        await _nav.WaitAsync(ct);
        try
        {
            var from = _activeRoute;
            if (from != null && Owner(from) is { } old)
            {
                try { await old.OnPageDeactivatedAsync(from); }
                catch (Exception ex) { _ctx.Log.Error($"{old.Id}: falha ao sair de {from}", ex); }
            }
            await EnsureInitializedAsync(target, ct);
            _activeRoute = route;
            await target.OnPageActivatedAsync(route, ct);
            _ctx.Bus.Publish(new PageChanged(from, route));
        }
        finally { _nav.Release(); }
    }

    private Task EnsureInitializedAsync(IModule m, CancellationToken ct)
    {
        lock (_init)
        {
            if (!_init.TryGetValue(m.Id, out var t) || t.IsFaulted || t.IsCanceled)
                _init[m.Id] = t = m.InitializeAsync(_ctx, ct); // falhou antes? tenta de novo na próxima visita
            return t;
        }
    }

    public HealthState RecalculateLastro()
    {
        var state = Lastro.Calculate(_modules.Values.SelectMany(m => m.HealthIndicators), _ctx.Log);
        _ctx.State.Update<HealthState>(_ => state);
        _ctx.Bus.Publish(new LastroCalculated(state));
        return state;
    }

    public async ValueTask ShutdownAsync()
    {
        foreach (var m in _modules.Values)
        {
            try { await m.ShutdownAsync(); }
            catch (Exception ex) { _ctx.Log.Error($"{m.Id}: falha ao encerrar", ex); }
        }
    }
}
