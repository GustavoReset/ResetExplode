namespace FortKnox;

/// <summary>RESET FERRAMENTAS · Vault de senhas: liga a página VaultView à janela. Mesmo padrão dos outros MainWindow.*.cs.</summary>
public partial class MainWindow
{
    private readonly PasswordVault _vault = new();
    private bool _vaultOpened;

    /// <summary>Chamado por Nav_Checked quando a página abre. A leitura do arquivo e o DPAPI rodam fora da thread da interface.</summary>
    private void InitVault()
    {
        _vaultOpened = true;
        _ = PageVault.OpenAsync(_vault);
    }

    /// <summary>
    /// Ao sair da página: tira o cofre (e as senhas) da memória. A senha que já foi copiada continua na área de transferência
    /// até o temporizador de 30 s limpá-la — trocar de página não deve estragar o "copiar e colar".
    /// </summary>
    private void LockVault()
    {
        if (!_vaultOpened) return;
        PageVault.Lock();
    }

    /// <summary>Ao fechar o app: além de bloquear, esvazia a área de transferência se ainda tiver uma senha nossa.</summary>
    private void ShutdownVault()
    {
        SecureClipboard.ClearNow();
        LockVault();
    }
}
