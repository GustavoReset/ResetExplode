using FortKnox.Core.Events;

namespace FortKnox.Core.State;

/// <summary>Uma fatia do estado global (record imutável). Cada módulo é dono das suas fatias; os outros só leem.</summary>
public interface IStateSlice;

public interface IStateStore
{
    /// <summary>Fatia atual; se ninguém gravou ainda, uma instância nova (construtor sem parâmetros).</summary>
    T Get<T>() where T : class, IStateSlice, new();
    /// <summary>Troca a fatia pelo resultado do reducer (copiar e trocar). Avisa quem observa, se mudou.</summary>
    T Update<T>(Func<T, T> reducer) where T : class, IStateSlice, new();
    IDisposable Watch<T>(Action<T> onChange, Delivery delivery = Delivery.UiThread) where T : class, IStateSlice, new();
}

/// <summary>Evento interno publicado a cada mudança de fatia (é o que Watch assina).</summary>
public sealed record StateChanged<T>(T Value) : IFortKnoxEvent where T : class, IStateSlice;

/// <summary>
/// Estado de sessão em memória. Leitura sempre consistente (a fatia é imutável); atualização serializada por fatia,
/// então duas threads não perdem a mudança uma da outra. Records comparam por valor: mudança sem diferença não avisa ninguém.
/// </summary>
public sealed class StateStore : IStateStore
{
    private readonly IEventBus _bus;
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _slices = new();

    public StateStore(IEventBus bus) => _bus = bus;

    public T Get<T>() where T : class, IStateSlice, new()
    {
        lock (_gate) return _slices.TryGetValue(typeof(T), out var v) ? (T)v : new T();
    }

    public T Update<T>(Func<T, T> reducer) where T : class, IStateSlice, new()
    {
        ArgumentNullException.ThrowIfNull(reducer);
        T before, after;
        lock (_gate)
        {
            before = _slices.TryGetValue(typeof(T), out var v) ? (T)v : new T();
            after = reducer(before) ?? throw new InvalidOperationException($"O reducer de {typeof(T).Name} devolveu null.");
            _slices[typeof(T)] = after;
        }
        if (!Equals(before, after)) _bus.Publish(new StateChanged<T>(after));
        return after;
    }

    public IDisposable Watch<T>(Action<T> onChange, Delivery delivery = Delivery.UiThread) where T : class, IStateSlice, new()
        => _bus.Subscribe<StateChanged<T>>(e => onChange(e.Value), delivery);
}
