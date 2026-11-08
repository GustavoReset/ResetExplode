using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FortKnox;

/// <summary>
/// Copia segredos para a área de transferência e a limpa sozinha depois de 30 s — mas só se ela ainda contiver
/// exatamente o que nós copiamos (se o usuário copiou outra coisa nesse meio-tempo, não apagamos nada dele).
/// Também pede ao Windows para NÃO guardar o valor no histórico (Win+V) nem sincronizar com a nuvem.
/// Estático de propósito: o temporizador precisa sobreviver à troca de página; chame ClearNow() ao fechar o app.
/// Todos os métodos devem ser chamados na thread da interface.
/// </summary>
public static class SecureClipboard
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource? _cts;
    private static string? _value;

    /// <summary>Copia o texto e agenda a limpeza. Devolve false se outro programa estava segurando a área de transferência.</summary>
    public static async Task<bool> CopyAsync(string text, Action? onCleared = null)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        // Formatos "não registre isto" reconhecidos pelo Windows 10/11 (histórico e Área de Transferência na nuvem).
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));   // DWORD 0 = não
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));      // DWORD 0 = não

        if (!await TrySetAsync(data)) return false;

        CancelTimer();
        var cts = _cts = new CancellationTokenSource();
        _value = text;
        _ = ClearLaterAsync(text, onCleared, cts.Token);
        return true;
    }

    /// <summary>Limpa agora (se ainda for o nosso valor) e cancela o temporizador. Usar ao bloquear o cofre e ao fechar o app.</summary>
    public static void ClearNow()
    {
        CancelTimer();
        ClearIfOurs();
    }

    private static async Task ClearLaterAsync(string text, Action? onCleared, CancellationToken ct)
    {
        try { await Task.Delay(Lifetime, ct); }
        catch (OperationCanceledException) { return; } // outra cópia assumiu, ou ClearNow()
        if (ClearIfOurs()) onCleared?.Invoke();
    }

    private static void CancelTimer()
    {
        var old = _cts;
        _cts = null;
        if (old == null) return;
        old.Cancel();
        old.Dispose();
    }

    private static bool ClearIfOurs()
    {
        string? mine = _value;
        _value = null;
        if (mine == null) return false;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == mine)
            {
                Clipboard.Clear();
                return true;
            }
        }
        catch (ExternalException) { } // área de transferência ocupada por outro programa: tentamos uma vez só
        return false;
    }

    /// <summary>A área de transferência é global e outro programa pode estar com ela aberta: tenta algumas vezes.</summary>
    private static async Task<bool> TrySetAsync(IDataObject data)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException) { await Task.Delay(40); } // CLIPBRD_E_CANT_OPEN
        }
        return false;
    }
}
