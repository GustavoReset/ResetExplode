using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>PRENSA · PDF como módulo do núcleo (Fase 3.2). Substitui o antigo MainWindow.Pdf.cs.</summary>
public sealed class PrensaModule(PdfView view) : ModuleBase
{
    public const string RoutePdf = "prensa/pdf";

    public override string Id => "prensa";
    public override string DisplayName => "PRENSA";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RoutePdf, "PDF", "", 910) };

    /// <summary>Não carrega nada ao abrir: as bibliotecas de PDF só são usadas quando uma operação roda.</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        view.Activate();
        return Task.CompletedTask;
    }

    /// <summary>Ao fechar o app: cancela a operação em andamento (o serviço apaga os arquivos de saída parciais).</summary>
    public override ValueTask ShutdownAsync()
    {
        view.CancelAll();
        return ValueTask.CompletedTask;
    }
}
