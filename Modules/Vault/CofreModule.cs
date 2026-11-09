using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// COFRE · senhas como módulo do núcleo (Fase 3.2). Substitui o antigo MainWindow.Vault.cs:
/// Windows Hello com tolerância de 60 s, cofre fora da memória ao sair da página e área de transferência limpa ao fechar.
/// </summary>
public sealed class CofreModule : ModuleBase
{
    public const string RouteSenhas = "cofre/senhas";

    private readonly VaultView _view;
    private readonly Func<IntPtr> _ownerHandle;
    private readonly PasswordVault _vault = new();

    // Depois de verificado, o cofre abre sem pedir de novo enquanto a página estiver aberta e por mais VaultGrace depois
    // de sair dela (evita pedir PIN a cada troca rápida de aba). BLOQUEAR zera a tolerância.
    private static readonly TimeSpan VaultGrace = TimeSpan.FromSeconds(60);
    private DateTime _verifiedUntil = DateTime.MinValue; // MaxValue = verificado e página aberta

    public CofreModule(VaultView view, Func<IntPtr> ownerHandle)
    {
        _view = view;
        _ownerHandle = ownerHandle;
    }

    public override string Id => "cofre";
    public override string DisplayName => "COFRE";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteSenhas, "Senhas", "", 900) };

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _view.Gate = GateAsync;
        _view.ManualLock += () => _verifiedUntil = DateTime.MinValue;
        return Task.CompletedTask;
    }

    /// <summary>A leitura do arquivo e o DPAPI rodam fora da thread da interface (dentro de OpenAsync).</summary>
    public override Task OnPageActivatedAsync(string route, CancellationToken ct) => _view.OpenAsync(_vault);

    /// <summary>
    /// Sair da página tira o cofre (e as senhas) da memória. A senha já copiada continua na área de transferência até o
    /// temporizador de 30 s limpá-la: trocar de página não deve estragar o "copiar e colar".
    /// </summary>
    public override Task OnPageDeactivatedAsync(string route)
    {
        Lock();
        return Task.CompletedTask;
    }

    /// <summary>Ao fechar o app: além de bloquear, esvazia a área de transferência se ainda tiver uma senha nossa.</summary>
    public override ValueTask ShutdownAsync()
    {
        SecureClipboard.ClearNow();
        Lock();
        return ValueTask.CompletedTask;
    }

    private void Lock()
    {
        if (_verifiedUntil == DateTime.MaxValue) _verifiedUntil = DateTime.UtcNow + VaultGrace;
        _view.Lock();
    }

    private async Task<HelloResult> GateAsync()
    {
        if (DateTime.UtcNow < _verifiedUntil)
        {
            _verifiedUntil = DateTime.MaxValue;
            return new HelloResult(HelloOutcome.Verified, "Dentro do período de tolerância.");
        }
        var result = await HelloVerifier.VerifyAsync(_ownerHandle(), "Abrir o Vault de senhas do Fort Knox");
        if (result.IsVerified) _verifiedUntil = DateTime.MaxValue;
        return result;
    }
}
