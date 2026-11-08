namespace FortKnox;

/// <summary>RESET FERRAMENTAS · PDF: liga a página PdfView à janela. Mesmo padrão dos outros MainWindow.*.cs.</summary>
public partial class MainWindow
{
    /// <summary>Chamado por Nav_Checked quando a página abre. Não carrega nada: as bibliotecas de PDF só são usadas quando uma operação roda.</summary>
    private void InitPdf() => PagePdf.Activate();

    /// <summary>Ao fechar o app: cancela a operação em andamento (os arquivos de saída parciais são apagados pelo serviço).</summary>
    private void ShutdownPdf() => PagePdf.CancelAll();
}
