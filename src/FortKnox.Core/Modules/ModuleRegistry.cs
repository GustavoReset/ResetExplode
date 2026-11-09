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
    private int _shutDown;

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

    /// <summary>
    /// Troca de página: desativa a anterior, inicializa o módulo se for a primeira vez, ativa a nova.
    /// Rota sem módulo (página que ainda não migrou, ex.: "legado/PageDash") só desativa a anterior.
    /// Roda na thread de quem chamou (a da interface, no Shell): os módulos podem mexer na tela aqui.
    /// </summary>
    public async Task NavigateAsync(string route, CancellationToken ct = default)
    {
        var target = Owner(route);
        await _nav.WaitAsync(ct);
        try
        {
            var from = _activeRoute;
            if (string.Equals(from, route, StringComparison.OrdinalIgnoreCase)) return;
            if (from != null && Owner(from) is { } old)
            {
                try { await old.OnPageDeactivatedAsync(from); }
                catch (Exception ex) { _ctx.Log.Error($"{old.Id}: falha ao sair de {from}", ex); }
            }
            _activeRoute = route;
            if (target != null)
            {
                await EnsureInitializedAsync(target, ct);
                await target.OnPageActivatedAsync(route, ct);
            }
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

    /// <summary>Encerra todos os módulos uma única vez (o Shell chama ao fechar a janela; o host chama de novo no fim, sem efeito).</summary>
    public async ValueTask ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutDown, 1) == 1) return;
        foreach (var m in _modules.Values)
        {
            try { await m.ShutdownAsync(); }
            catch (Exception ex) { _ctx.Log.Error($"{m.Id}: falha ao encerrar", ex); }
        }
    }
}
