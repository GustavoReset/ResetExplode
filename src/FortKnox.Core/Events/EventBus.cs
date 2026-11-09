namespace FortKnox.Core.Events;

/// <summary>Marca os eventos do barramento. Eventos são records imutáveis com nome no passado (ScanCompleted, ChangeApplied…).</summary>
public interface IFortKnoxEvent;

public enum Delivery
{
    /// <summary>Entregue na thread da interface (o SynchronizationContext informado ao criar o barramento).</summary>
    UiThread,
    /// <summary>Entregue no pool de threads.</summary>
    Background,
    /// <summary>Entregue na hora, na thread de quem publicou (testes e serviços sem interface).</summary>
    Synchronous,
}

public interface IEventBus
{
    void Publish<T>(T evt) where T : IFortKnoxEvent;
    /// <summary>Devolve um IDisposable: descartar cancela a assinatura (o ViewModel faz isso ao sair da página).</summary>
    IDisposable Subscribe<T>(Action<T> handler, Delivery delivery = Delivery.UiThread) where T : IFortKnoxEvent;
}

/// <summary>
/// Barramento em memória. Módulos nunca chamam módulos: publicam e assinam eventos.
/// Um handler que lança exceção vai para o log e não impede os outros de receberem o evento.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly SynchronizationContext? _ui;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subs = new();

    /// <param name="uiContext">Contexto da thread da interface; null = UiThread vira Synchronous (sem interface).</param>
    public EventBus(SynchronizationContext? uiContext, ILog? log = null)
    {
        _ui = uiContext;
        _log = log ?? NullLog.Instance;
    }

    public void Publish<T>(T evt) where T : IFortKnoxEvent
    {
        ArgumentNullException.ThrowIfNull(evt);
        Subscription[] targets;
        lock (_gate)
        {
            if (!_subs.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
            targets = list.ToArray(); // cópia: um handler pode cancelar/assinar durante a entrega
        }
        foreach (var s in targets) s.Deliver(evt);
    }

    public IDisposable Subscribe<T>(Action<T> handler, Delivery delivery = Delivery.UiThread) where T : IFortKnoxEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var sub = new Subscription(this, typeof(T), o => handler((T)o), delivery);
        lock (_gate)
        {
            if (!_subs.TryGetValue(typeof(T), out var list)) _subs[typeof(T)] = list = new();
            list.Add(sub);
        }
        return sub;
    }

    internal int CountFor<T>() { lock (_gate) return _subs.TryGetValue(typeof(T), out var l) ? l.Count : 0; }

    private void Remove(Subscription s)
    {
        lock (_gate) if (_subs.TryGetValue(s.Type, out var list)) list.Remove(s);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Action<object> _handler;
        private readonly Delivery _delivery;
        private volatile bool _disposed;
        public Type Type { get; }

        public Subscription(EventBus bus, Type type, Action<object> handler, Delivery delivery)
        { _bus = bus; Type = type; _handler = handler; _delivery = delivery; }

        public void Deliver(object evt)
        {
            if (_disposed) return;
            switch (_delivery)
            {
                case Delivery.UiThread when _bus._ui != null:
                    _bus._ui.Post(_ => Invoke(evt), null);
                    break;
                case Delivery.Background:
                    ThreadPool.QueueUserWorkItem(_ => Invoke(evt));
                    break;
                default:
                    Invoke(evt);
                    break;
            }
        }

        private void Invoke(object evt)
        {
            if (_disposed) return; // cancelado entre a publicação e a entrega
            try { _handler(evt); }
            catch (Exception ex) { _bus._log.Error($"Handler de {Type.Name} falhou", ex); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Remove(this);
        }
    }
}
