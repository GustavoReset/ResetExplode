using FortKnox.Core.Events;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>Publicado pelo Shell quando um dispositivo USB é conectado ou removido (WM_DEVICECHANGE, já filtrado).</summary>
public sealed record UsbDevicesChanged(int Added, int Removed) : IFortKnoxEvent;

/// <summary>OFICINA (Arduino, ESP32 e conversores USB-serial) como módulo do núcleo (Fase 3.3b).</summary>
public sealed class OficinaModule(PortsView ports, SerialDriversView drivers, Func<byte[]?> legacyComDb, Action clearLegacyComDb) : ModuleBase
{
    public const string RoutePorts = "oficina/portas";
    public const string RouteDrivers = "oficina/drivers";
    public const string SettingsId = "oficina";

    private IDisposable? _sub;
    private string? _active;

    public override string Id => "oficina";
    public override string DisplayName => "OFICINA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[]
    {
        new NavEntry(RoutePorts, "Portas COM", "", 610, "OFICINA"),
        new NavEntry(RouteDrivers, "Drivers seriais", "", 620, "OFICINA"),
    };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.NeedsAdmin;

    /// <summary>Lastro: números COM presos por dispositivos que não estão conectados.</summary>
    public override IEnumerable<IHealthIndicator> HealthIndicators { get; } = new IHealthIndicator[]
    {
        new HealthIndicator("oficina.portas-com", "Portas COM livres", 0.5, () =>
        {
            try
            {
                var (reserved, inUse) = UsbCleaner.ComStatus();
                int stuck = reserved.Except(inUse).Count();
                return stuck == 0 ? HealthIndicator.Bom("Nenhum número COM preso.")
                     : stuck < 10 ? HealthIndicator.Atencao($"{stuck} números COM presos por dispositivos antigos.", RoutePorts, 80)
                     : HealthIndicator.Atencao($"{stuck} números COM presos: novas placas podem cair em COM20 ou mais.", RoutePorts);
            }
            catch (Exception) { return HealthIndicator.Bom("Sem leitura das portas COM."); }
        }),
    };

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        // Migração única: o backup do ComDB ficava no settings.json antigo (AppSettings.ComDbBackup).
        if (legacyComDb() is { } old
            && Context.Settings.Migrate<OficinaSettings>(SettingsId, () => new OficinaSettings { ComDbBackup = old }))
        {
            clearLegacyComDb();
            Context.Log.Info("OFICINA: backup do ComDB migrado para modules.json");
        }
        ports.Attach(Context.Settings.Section<OficinaSettings>(SettingsId));

        // Placa plugada/removida com a página aberta: atualiza na hora (antes isso ficava no MainWindow).
        _sub = Context.Bus.Subscribe<UsbDevicesChanged>(_ =>
        {
            if (_active == RoutePorts) ports.Scan();
            else if (_active == RouteDrivers) drivers.OnDevicesChanged();
        });
        return Task.CompletedTask;
    }

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        _active = route;
        if (route == RoutePorts) ports.Scan();
        else drivers.Activate();
        return Task.CompletedTask;
    }

    public override Task OnPageDeactivatedAsync(string route)
    {
        _active = null;
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        _sub?.Dispose();
        return ValueTask.CompletedTask;
    }
}
