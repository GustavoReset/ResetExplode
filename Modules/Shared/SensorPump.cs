using System.Windows.Threading;
using FortKnox.Core.Events;

namespace FortKnox;

/// <summary>Publicado no barramento uma vez por segundo, logo depois de os sensores serem relidos.</summary>
public sealed record SensorsRefreshed(DateTime At) : IFortKnoxEvent;

/// <summary>
/// Ciclo de 1 s dos sensores (Fase 3.3n; antes era o DispatcherTimer do MainWindow chamando cada página).
/// Relê o LibreHardwareMonitor e publica <see cref="SensorsRefreshed"/> no barramento do núcleo; cada módulo assina o que precisa.
/// Pausa enquanto <paramref name="paused"/> for verdadeiro (cena de GPU, ray tracing, disco: cada consulta ao driver gera engasgo).
/// </summary>
public sealed class SensorPump(SensorMonitor monitor, IEventBus bus, Func<bool> paused) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Tick += (_, _) =>
        {
            if (paused()) return;
            monitor.Refresh();
            bus.Publish(new SensorsRefreshed(DateTime.Now));
        };
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
