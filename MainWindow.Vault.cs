using System.Windows.Interop;

namespace FortKnox;

/// <summary>COFRE · senhas: liga a página VaultView à janela. Mesmo padrão dos outros MainWindow.*.cs.</summary>
public partial class MainWindow
{
    private readonly PasswordVault _vault = new();
    private bool _vaultOpened;

    // Windows Hello: depois de verificado, o cofre abre sem pedir de novo enquanto a página estiver aberta e por mais
    // VaultGrace depois de sair dela (evita pedir PIN a cada troca rápida de aba). BLOQUEAR zera a tolerância.
    private static readonly TimeSpan VaultGrace = TimeSpan.FromSeconds(60);
    private DateTime _vaultVerifiedUntil = DateTime.MinValue; // MaxValue = verificado e página aberta

    /// <summary>Chamado por Nav_Checked quando a página abre. A leitura do arquivo e o DPAPI rodam fora da thread da interface.</summary>
    private void InitVault()
    {
        if (!_vaultOpened)
        {
            PageVault.Gate = VaultGateAsync;
            PageVault.ManualLock += () => _vaultVerifiedUntil = DateTime.MinValue;
        }
        _vaultOpened = true;
        _ = PageVault.OpenAsync(_vault);
    }

    private async Task<HelloResult> VaultGateAsync()
    {
        if (DateTime.UtcNow < _vaultVerifiedUntil)
        {
            _vaultVerifiedUntil = DateTime.MaxValue;
            return new HelloResult(HelloOutcome.Verified, "Dentro do período de tolerância.");
        }
        var result = await HelloVerifier.VerifyAsync(new WindowInteropHelper(this).Handle, "Abrir o Vault de senhas do Fort Knox");
        if (result.IsVerified) _vaultVerifiedUntil = DateTime.MaxValue;
        return result;
    }

    /// <summary>
    /// Ao sair da página: tira o cofre (e as senhas) da memória. A senha que já foi copiada continua na área de transferência
    /// até o temporizador de 30 s limpá-la — trocar de página não deve estragar o "copiar e colar".
    /// </summary>
    private void LockVault()
    {
        if (!_vaultOpened) return;
        if (_vaultVerifiedUntil == DateTime.MaxValue) _vaultVerifiedUntil = DateTime.UtcNow + VaultGrace;
        PageVault.Lock();
    }

    /// <summary>Ao fechar o app: além de bloquear, esvazia a área de transferência se ainda tiver uma senha nossa.</summary>
    private void ShutdownVault()
    {
        SecureClipboard.ClearNow();
        LockVault();
    }
}
