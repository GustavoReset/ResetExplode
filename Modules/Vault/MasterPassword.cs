using System.Security.Cryptography;
using System.Text;

namespace FortKnox;

/// <summary>Preferências do COFRE (seção "cofre" do modules.json).</summary>
public sealed class CofreSettings
{
    /// <summary>Abrir o cofre com o Windows Hello. Só vale se o Windows Hello estiver disponível; senão, senha mestra.</summary>
    public bool UseWindowsHello { get; set; } = true;
}

/// <summary>
/// Senha mestra do COFRE: usada quando o Windows Hello está desligado nas Configurações ou não existe neste PC.
/// É uma trava de acesso à tela, igual ao Windows Hello; o arquivo continua cifrado com DPAPI (PasswordVault).
///
/// ┌──────────────────────────────────────────────────────────────────────────────────────────────┐
/// │ PRODUÇÃO: trocar antes de publicar. A senha de TESTE abaixo ("1234") é provisória.           │
/// │ Antes da versão de produção: a pessoa define a própria senha mestra no primeiro uso, guardada │
/// │ só como hash PBKDF2-SHA256 (600.000 iterações, sal aleatório) em %AppData%\FortKnox. Ver     │
/// │ docs/PRODUCAO-PENDENCIAS.md.                                                                  │
/// └──────────────────────────────────────────────────────────────────────────────────────────────┘
/// </summary>
public static class MasterPassword
{
    // PRODUÇÃO: remover. Senha de teste combinada para a fase de desenvolvimento.
    private const string DevPassword = "1234";

    /// <summary>true enquanto a senha de teste estiver em uso (o app registra um aviso no log ao abrir o cofre).</summary>
    public static bool IsDevPassword => true; // PRODUÇÃO: false quando houver senha definida pela pessoa

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("FortKnox.Cofre.dev-salt"); // PRODUÇÃO: sal aleatório por instalação
    private const int Iterations = 100_000;
    private static readonly byte[] DevHash = Hash(DevPassword);

    /// <summary>Compara em tempo constante (não revela quantos caracteres acertou).</summary>
    public static bool Verify(string? attempt)
        => !string.IsNullOrEmpty(attempt) && CryptographicOperations.FixedTimeEquals(Hash(attempt), DevHash);

    private static byte[] Hash(string s) => Rfc2898DeriveBytes.Pbkdf2(s, Salt, Iterations, HashAlgorithmName.SHA256, 32);
}
