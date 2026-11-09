using System.Windows;
using System.Windows.Interop;
using FortKnox.Core.Config;
using FortKnox.Core.Modules;

namespace FortKnox;

/// <summary>
/// COFRE · senhas como módulo do núcleo (Fase 3.2). Substitui o antigo MainWindow.Vault.cs:
/// trava de abertura (Windows Hello ou senha mestra) com tolerância de 60 s, cofre fora da memória ao sair da página
/// e área de transferência limpa ao fechar.
/// </summary>
public sealed class CofreModule : ModuleBase
{
    public const string RouteSenhas = "cofre/senhas";
    public const string SettingsId = "cofre";

    private readonly VaultView _view;
    private readonly Window _owner;
    private readonly PasswordVault _vault = new();
    private bool _useHello;   // decidido a cada abertura: preferência ligada E Windows Hello disponível
    private bool _devWarned;

    // Depois de verificado, o cofre abre sem pedir de novo enquanto a página estiver aberta e por mais VaultGrace depois
    // de sair dela (evita pedir de novo a cada troca rápida de aba). BLOQUEAR zera a tolerância.
    private static readonly TimeSpan VaultGrace = TimeSpan.FromSeconds(60);
    private DateTime _verifiedUntil = DateTime.MinValue; // MaxValue = verificado e página aberta

    public CofreModule(VaultView view, Window owner)
    {
        _view = view;
        _owner = owner;
    }

    public override string Id => "cofre";
    public override string DisplayName => "COFRE";
    public override IReadOnlyList<NavEntry> Pages { get; } = new[] { new NavEntry(RouteSenhas, "Senhas", "", 900) };

    private ISettings<CofreSettings> Prefs => Context.Settings.Section<CofreSettings>(SettingsId);

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _view.Gate = GateAsync;
        _view.ManualLock += () => _verifiedUntil = DateTime.MinValue;
        return Task.CompletedTask;
    }

    /// <summary>Escolhe a trava (Windows Hello ou senha mestra) e abre. Ler o arquivo e o DPAPI rodam fora da thread da interface.</summary>
    public override async Task OnPageActivatedAsync(string route, CancellationToken ct)
    {
        _useHello = Prefs.Value.UseWindowsHello && (await HelloVerifier.CheckAsync()).Outcome == HelloOutcome.Verified;
        _view.GateMessage = _useHello ? "Confirme no Windows Hello para abrir o Vault…" : "Digite a senha mestra para abrir o Vault…";
        await _view.OpenAsync(_vault);
    }

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

        HelloResult result;
        if (_useHello)
        {
            result = await HelloVerifier.VerifyAsync(new WindowInteropHelper(_owner).Handle, "Abrir o Vault de senhas do Fort Knox");
            if (result.Outcome == HelloOutcome.Unavailable) result = AskMasterPassword(); // Hello sumiu entre a checagem e o pedido
        }
        else result = AskMasterPassword();

        if (result.IsVerified) _verifiedUntil = DateTime.MaxValue;
        return result;
    }

    private HelloResult AskMasterPassword()
    {
        if (MasterPassword.IsDevPassword && !_devWarned)
        {
            _devWarned = true;
            Context.Log.Warn("COFRE: senha mestra de TESTE em uso. Trocar antes da produção (docs/PRODUCAO-PENDENCIAS.md).");
        }
        return MasterPasswordWindow.Ask(_owner)
            ? new HelloResult(HelloOutcome.Verified, "Senha mestra confirmada.")
            : new HelloResult(HelloOutcome.Canceled, "Digite a senha mestra para abrir o Vault.");
    }
}
