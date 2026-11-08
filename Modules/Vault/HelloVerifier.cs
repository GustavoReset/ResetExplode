using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Security.Credentials.UI;

namespace FortKnox;

public enum HelloOutcome
{
    /// <summary>O usuário se autenticou (PIN, digital ou rosto).</summary>
    Verified,
    /// <summary>O usuário fechou a janela ou errou o limite de tentativas.</summary>
    Canceled,
    /// <summary>Windows Hello não está configurado/disponível neste PC. Não é uma falha do usuário.</summary>
    Unavailable,
    /// <summary>Erro inesperado ao chamar o Windows.</summary>
    Failed,
}

public sealed record HelloResult(HelloOutcome Outcome, string Message)
{
    public bool IsVerified => Outcome == HelloOutcome.Verified;
}

/// <summary>
/// Pede a verificação do Windows Hello (PIN, impressão digital ou rosto) antes de abrir o Vault.
/// Usa a janela do app como dona do diálogo (IUserConsentVerifierInterop): num programa de área de trabalho, a chamada
/// simples RequestVerificationAsync pode abrir o diálogo escondido atrás da janela ou sem foco.
/// Quem decide o que fazer quando o Hello não existe (Unavailable) é a tela: aqui só se reporta o fato.
/// </summary>
public static class HelloVerifier
{
    private static readonly Guid InteropFactoryIid = new("39E050C3-4E74-441A-8DC0-B81104DF949C"); // IUserConsentVerifierInterop
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";

    /// <summary>Verifica só a disponibilidade (sem abrir diálogo).</summary>
    public static async Task<HelloResult> CheckAsync()
    {
        try
        {
            var a = await UserConsentVerifier.CheckAvailabilityAsync().AsTask().ConfigureAwait(true);
            return a == UserConsentVerifierAvailability.Available
                ? new HelloResult(HelloOutcome.Verified, "Windows Hello disponível.")
                : new HelloResult(HelloOutcome.Unavailable, Explain(a));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException or TypeLoadException)
        {
            return new HelloResult(HelloOutcome.Unavailable, "Este Windows não oferece o Windows Hello (" + ex.Message + ").");
        }
    }

    /// <summary>Mostra o diálogo do Windows Hello sobre <paramref name="owner"/> e devolve o resultado. Chamar na thread da interface.</summary>
    public static async Task<HelloResult> VerifyAsync(IntPtr owner, string message)
    {
        var avail = await CheckAsync().ConfigureAwait(true);
        if (avail.Outcome != HelloOutcome.Verified) return avail; // aqui "Verified" só significa "disponível"

        try
        {
            var op = Request(owner, message);
            var result = await op.AsTask().ConfigureAwait(true);
            return result switch
            {
                UserConsentVerificationResult.Verified => new HelloResult(HelloOutcome.Verified, "Verificado."),
                UserConsentVerificationResult.Canceled => new HelloResult(HelloOutcome.Canceled, "Verificação cancelada."),
                UserConsentVerificationResult.RetriesExhausted => new HelloResult(HelloOutcome.Canceled, "Muitas tentativas erradas. Tente de novo mais tarde."),
                UserConsentVerificationResult.DeviceBusy => new HelloResult(HelloOutcome.Failed, "O leitor biométrico está ocupado. Tente de novo."),
                _ => new HelloResult(HelloOutcome.Unavailable, "Windows Hello não está pronto neste PC (" + result + ")."),
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException)
        {
            return new HelloResult(HelloOutcome.Failed, "Não foi possível abrir o Windows Hello: " + ex.Message);
        }
    }

    private static string Explain(UserConsentVerifierAvailability a) => a switch
    {
        UserConsentVerifierAvailability.DeviceNotPresent => "Este PC não tem leitor biométrico nem PIN do Windows Hello configurado.",
        UserConsentVerifierAvailability.NotConfiguredForUser => "O Windows Hello não está configurado para a sua conta. Configure um PIN em Configurações > Contas > Opções de entrada.",
        UserConsentVerifierAvailability.DisabledByPolicy => "O Windows Hello está desativado por uma política do sistema.",
        UserConsentVerifierAvailability.DeviceBusy => "O Windows Hello está ocupado. Tente de novo.",
        _ => "O Windows Hello não está disponível neste PC.",
    };

    // ---- IUserConsentVerifierInterop, chamado pela vtable (o .NET moderno não faz esse COM de WinRT sozinho) ----

    private static IAsyncOperation<UserConsentVerificationResult> Request(IntPtr owner, string message)
    {
        IntPtr hClass = IntPtr.Zero, hMsg = IntPtr.Zero, factory = IntPtr.Zero, operation = IntPtr.Zero;
        try
        {
            Check(WindowsCreateString(ClassName, ClassName.Length, out hClass));
            var iid = InteropFactoryIid;
            Check(RoGetActivationFactory(hClass, ref iid, out factory));
            Check(WindowsCreateString(message, message.Length, out hMsg));

            // vtable: 0-2 IUnknown, 3-5 IInspectable, 6 = RequestVerificationForWindowAsync(HWND, HSTRING, REFIID, void**)
            var vtbl = Marshal.ReadIntPtr(factory);
            var fn = Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size);
            var call = Marshal.GetDelegateForFunctionPointer<RequestDelegate>(fn);
            var asyncIid = AsyncOperationIid;
            Check(call(factory, owner, hMsg, ref asyncIid, out operation));
            return WinRT.MarshalInterface<IAsyncOperation<UserConsentVerificationResult>>.FromAbi(operation);
        }
        finally
        {
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (hMsg != IntPtr.Zero) WindowsDeleteString(hMsg);
            if (hClass != IntPtr.Zero) WindowsDeleteString(hClass);
        }
    }

    /// <summary>
    /// IID de IAsyncOperation&lt;UserConsentVerificationResult&gt;. Interfaces genéricas do WinRT têm IID calculado: UUID v5 (SHA-1)
    /// sobre a assinatura do tipo, no namespace fixo do WinRT. (GuidGenerator.GetIID do CsWinRT não resolve este tipo.)
    /// </summary>
    internal static readonly Guid AsyncOperationIid = ParameterizedIid(
        "pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))");

    internal static Guid ParameterizedIid(string signature)
    {
        byte[] ns = { 0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee }; // {11f47ad5-7b73-42c0-abae-878b1e16adee}, ordem de rede
        var data = ns.Concat(System.Text.Encoding.UTF8.GetBytes(signature)).ToArray();
        var h = System.Security.Cryptography.SHA1.HashData(data);
        h[6] = (byte)((h[6] & 0x0F) | 0x50); // versão 5
        h[8] = (byte)((h[8] & 0x3F) | 0x80); // variante RFC 4122
        return new Guid(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(0, 4)),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(h.AsSpan(4, 2)),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(h.AsSpan(6, 2)),
                        h[8], h[9], h[10], h[11], h[12], h[13], h[14], h[15]);
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestDelegate(IntPtr self, IntPtr hwnd, IntPtr message, ref Guid riid, out IntPtr operation);

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
}
