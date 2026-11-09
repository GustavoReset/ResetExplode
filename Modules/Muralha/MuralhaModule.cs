using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// MURALHA (Fase 3.5a): antivírus offline complementar ao Microsoft Defender. Varredura sob demanda (rápida ou de uma pasta)
/// com o motor em camadas e quarentena cifrada. Tempo real fica para o serviço Guarda (Fase 3.6).
/// </summary>
public sealed class MuralhaModule(MuralhaView view, Action<string, string, string, string> addEvent) : ModuleBase
{
    public const string RouteScan = "muralha/varredura";
    public const string SettingsId = "muralha";
    private Core.Config.ISettings<MuralhaSettings>? _prefs;

    public override string Id => "muralha";
    public override string DisplayName => "MURALHA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteScan, "Muralha", "", 920) };

    /// <summary>Lastro: varredura recente.</summary>
    public override IEnumerable<IHealthIndicator> HealthIndicators => new IHealthIndicator[]
    {
        new HealthIndicator("muralha.varredura", "Varredura recente da MURALHA", 1, () =>
        {
            if (_prefs == null) return HealthIndicator.Bom("Sem leitura ainda.");
            var last = _prefs.Value.LastScan;
            if (last == null) return HealthIndicator.Atencao("Nenhuma varredura feita ainda.", RouteScan, 70);
            int days = (int)(DateTime.Now - last.Value).TotalDays;
            return days > 30 ? HealthIndicator.Atencao($"Última varredura há {days} dias.", RouteScan, 70)
                 : HealthIndicator.Bom(days == 0 ? "Verificado hoje." : $"Verificado há {days} dia(s).");
        }),
    };

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _prefs = Context.Settings.Section<MuralhaSettings>(SettingsId);
        view.Attach(_prefs, addEvent);
        return Task.CompletedTask;
    }

    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        view.CancelAll();
        return ValueTask.CompletedTask;
    }
}
