using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// PRISMA (iluminação RGB: Corsair iCUE, OpenRGB, Razer Chroma) como módulo do núcleo (Fase 3.3d).
/// O efeito continua rodando ao trocar de página (é o comportamento esperado); ao fechar o app, cada fabricante reassume as luzes.
/// </summary>
public sealed class PrismaModule(PrismaView view, SensorMonitor sensors) : ModuleBase
{
    public const string RouteLuzes = "prisma/luzes";

    public override string Id => "prisma";
    public override string DisplayName => "PRISMA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteLuzes, "Iluminação", "", 830) };
    public override ModuleCapabilities Capabilities => ModuleCapabilities.UsesNetwork; // localhost: servidor do OpenRGB e API do Synapse

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        view.Attach(sensors);
        return Task.CompletedTask;
    }

    /// <summary>Primeira visita: monta as cores e tenta conectar em todos os fabricantes.</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Activate();
        return Task.CompletedTask;
    }

    public override ValueTask ShutdownAsync()
    {
        view.Shutdown();
        return ValueTask.CompletedTask;
    }
}
