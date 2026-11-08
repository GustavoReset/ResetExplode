using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FortKnox.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkvault-" + Guid.NewGuid().ToString("N"));
    private string VaultFile => Path.Combine(_dir, "vault.dat");

    public VaultTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<PasswordVault> OpenAsync(string? path = null)
    {
        var v = new PasswordVault(path ?? VaultFile);
        await v.LoadAsync();
        return v;
    }

    private static VaultEntry E(string title, string pass = "S3nha!", string user = "joao", string url = "https://exemplo.com") =>
        new() { Title = title, Username = user, Password = pass, Url = url, Category = "Geral", Notes = "nota secreta" };

    private static byte[] Junk(int n) => Encoding.ASCII.GetBytes("FKV1").Concat(RandomNumberGenerator.GetBytes(n)).ToArray();

    [Fact]
    public async Task NewVault_IsReadyAndEmpty()
    {
        var v = await OpenAsync();
        Assert.Equal(VaultState.Ready, v.State);
        Assert.Equal(0, v.Count);
    }

    [Fact]
    public async Task Add_Persists_AcrossInstances()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("Gmail", "abc123"));
        var v2 = await OpenAsync();
        Assert.Equal(1, v2.Count);
        Assert.Equal("abc123", v2.GetPassword(id));
        Assert.Equal("joao", v2.GetUsername(id));
    }

    [Fact]
    public async Task File_IsNotPlaintext()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("TituloMuitoEspecifico", "SenhaMuitoEspecifica", "usuarioEspecifico"));
        var bytes = File.ReadAllBytes(VaultFile);
        foreach (var s in new[] { "TituloMuitoEspecifico", "SenhaMuitoEspecifica", "usuarioEspecifico", "exemplo.com", "nota secreta" })
        {
            Assert.DoesNotContain(s, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(s, Encoding.Unicode.GetString(bytes));
            Assert.DoesNotContain(s, Encoding.Latin1.GetString(bytes));
        }
        Assert.StartsWith("FKV1", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public async Task Update_ChangesFields_KeepsCreated()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("A", "p1"));
        var created = v.Get(id)!.Created;
        var e = v.Get(id)!; e.Title = "B"; e.Password = "p2";
        Assert.True(await v.UpdateAsync(e));
        var after = (await OpenAsync()).Get(id)!;
        Assert.Equal("B", after.Title);
        Assert.Equal("p2", after.Password);
        Assert.Equal(created, after.Created);
        Assert.True(after.Modified >= created);
    }

    [Fact]
    public async Task Update_Unknown_ReturnsFalse()
    {
        var v = await OpenAsync();
        var e = E("X"); e.Id = Guid.NewGuid();
        Assert.False(await v.UpdateAsync(e));
    }

    [Fact]
    public async Task Delete_Removes()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("A"));
        await v.AddAsync(E("B"));
        Assert.True(await v.DeleteAsync(id));
        Assert.False(await v.DeleteAsync(id));
        Assert.Equal(1, (await OpenAsync()).Count);
    }

    [Fact]
    public async Task EmptyTitle_Throws_AndDoesNotWrite()
    {
        var v = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => v.AddAsync(E("   ")));
        Assert.Equal(0, v.Count);
        Assert.False(File.Exists(VaultFile));
    }

    [Fact]
    public async Task Get_ReturnsCopy()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("A", "orig"));
        var c = v.Get(id)!; c.Password = "mudou";
        Assert.Equal("orig", v.GetPassword(id));
    }

    [Fact]
    public async Task Search_MatchesAllTerms_AcrossFields_CaseInsensitive()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("Gmail pessoal", user: "maria@x.com", url: "https://mail.google.com"));
        await v.AddAsync(E("Banco", user: "maria", url: "https://banco.com"));
        await v.AddAsync(E("Netflix", user: "joao", url: "https://netflix.com"));
        Assert.Equal(3, v.Search("").Count);
        Assert.Equal(2, v.Search("MARIA").Count);
        Assert.Single(v.Search("maria google"));
        Assert.Empty(v.Search("maria netflix"));
        Assert.Equal(new[] { "Banco", "Gmail pessoal", "Netflix" }, v.Search(null).Select(i => i.Title));
    }

    [Fact]
    public async Task VaultItem_NeverExposesPassword()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("A", "SEGREDO"));
        var item = v.Search("")[0];
        Assert.DoesNotContain("SEGREDO", item.ToString());
        Assert.Equal("••••••••", item.PasswordMask);
    }

    [Fact]
    public async Task Concurrent_Adds_AreAllPersisted()
    {
        var v = await OpenAsync();
        await Task.WhenAll(Enumerable.Range(0, 25).Select(i => v.AddAsync(E("item" + i))));
        Assert.Equal(25, v.Count);
        Assert.Equal(25, (await OpenAsync()).Count);
    }

    [Fact]
    public async Task Lock_ClearsMemory_AndBlocksWrites()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("A"));
        v.Lock();
        Assert.Equal(VaultState.NotLoaded, v.State);
        Assert.Equal(0, v.Count);
        Assert.Empty(v.Search(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => v.AddAsync(E("B")));
        await v.LoadAsync();
        Assert.Equal(1, v.Count);
    }

    [Fact]
    public async Task GarbageAfterMagic_IsUnreadable_FileUntouched_WritesBlocked()
    {
        // Simula "DPAPI não abre" (outro usuário / senha redefinida): cabeçalho certo, blob que o DPAPI rejeita.
        var junk = Junk(200);
        File.WriteAllBytes(VaultFile, junk);
        var v = await OpenAsync();
        Assert.Equal(VaultState.Unreadable, v.State);
        Assert.Contains("Windows", v.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => v.AddAsync(E("A")));
        Assert.Equal(junk, File.ReadAllBytes(VaultFile));
    }

    [Fact]
    public async Task CorruptMain_WithoutBak_IsUnreadable()
    {
        await (await OpenAsync()).AddAsync(E("A"));
        File.WriteAllBytes(VaultFile, new byte[] { 1, 2, 3, 4, 5, 6 });
        File.Delete(VaultFile + ".bak");
        var v = await OpenAsync();
        Assert.Equal(VaultState.Unreadable, v.State);
        Assert.Contains("corrompido", v.Problem);
    }

    [Fact]
    public async Task CorruptMain_WithGoodBak_RecoversPreviousVersion()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("Primeiro"));
        await v.AddAsync(E("Segundo")); // .bak = versão só com "Primeiro"
        Assert.True(File.Exists(VaultFile + ".bak"));
        File.WriteAllBytes(VaultFile, Encoding.ASCII.GetBytes("FKV1lixo"));
        var v2 = await OpenAsync();
        Assert.Equal(VaultState.Ready, v2.State);
        Assert.Equal(1, v2.Count);
        Assert.Contains(".bak", v2.Problem);
    }

    [Fact]
    public async Task Backup_RoundTrip_ToFreshVault()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("Gmail", "abc123"));
        string bk = Path.Combine(_dir, "b.fkvault");
        await v.ExportBackupAsync(bk, "senha-do-backup");
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(bk));
        Assert.DoesNotContain("Gmail", raw);
        Assert.StartsWith("FKB1", raw);

        string otherPath = Path.Combine(_dir, "outro", "vault.dat");
        var other = await OpenAsync(otherPath);
        Assert.Equal(1, await other.ImportBackupAsync(bk, "senha-do-backup"));
        Assert.Equal("abc123", other.GetPassword(id));
        Assert.Equal(1, (await OpenAsync(otherPath)).Count);
    }

    [Fact]
    public async Task Backup_WrongPassphrase_Fails_NothingChanges()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("A"));
        string bk = Path.Combine(_dir, "b.fkvault");
        await v.ExportBackupAsync(bk, "senha-correta");
        await Assert.ThrowsAsync<InvalidDataException>(() => v.ImportBackupAsync(bk, "senha-errada"));
        Assert.Equal(1, v.Count);
    }

    [Fact]
    public async Task Backup_TamperedFile_Fails()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("A"));
        string bk = Path.Combine(_dir, "b.fkvault");
        await v.ExportBackupAsync(bk, "senha-correta");
        var bytes = File.ReadAllBytes(bk); bytes[^1] ^= 0xFF; File.WriteAllBytes(bk, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => v.ImportBackupAsync(bk, "senha-correta"));
    }

    [Fact]
    public async Task Backup_NotAVaultFile_Fails()
    {
        var v = await OpenAsync();
        string f = Path.Combine(_dir, "x.fkvault");
        File.WriteAllText(f, "isto nao e um backup, de jeito nenhum, mesmo, ok?");
        await Assert.ThrowsAsync<InvalidDataException>(() => v.ImportBackupAsync(f, "qualquer-senha"));
    }

    [Fact]
    public async Task Backup_ShortPassphrase_Rejected()
    {
        var v = await OpenAsync();
        await v.AddAsync(E("A"));
        await Assert.ThrowsAsync<ArgumentException>(() => v.ExportBackupAsync(Path.Combine(_dir, "b.fkvault"), "curta"));
    }

    [Fact]
    public async Task Import_IntoUnreadable_PreservesOldFile_AndRecovers()
    {
        var good = await OpenAsync();
        var id = await good.AddAsync(E("Gmail", "abc123"));
        string bk = Path.Combine(_dir, "b.fkvault");
        await good.ExportBackupAsync(bk, "senha-do-backup");

        var junk = Junk(100);
        File.WriteAllBytes(VaultFile, junk);
        File.Delete(VaultFile + ".bak");
        var v = await OpenAsync();
        Assert.Equal(VaultState.Unreadable, v.State);

        Assert.Equal(1, await v.ImportBackupAsync(bk, "senha-do-backup"));
        Assert.Equal(VaultState.Ready, v.State);
        Assert.Equal("abc123", v.GetPassword(id));
        var kept = Directory.GetFiles(_dir, "vault.dat.ilegivel-*");
        Assert.Single(kept);
        Assert.Equal(junk, File.ReadAllBytes(kept[0]));
        Assert.Equal(1, (await OpenAsync()).Count);
    }

    [Fact]
    public async Task Import_Merge_NewerWins()
    {
        var v = await OpenAsync();
        var id = await v.AddAsync(E("Velho", "p"));
        string bk = Path.Combine(_dir, "b.fkvault");
        await v.ExportBackupAsync(bk, "senha-do-backup");
        var e = v.Get(id)!; e.Title = "Novo"; await v.UpdateAsync(e); // cofre mais recente que o backup
        await v.ImportBackupAsync(bk, "senha-do-backup");
        Assert.Equal("Novo", v.Get(id)!.Title);
        Assert.Equal(1, v.Count);
    }

    [Fact]
    public void Generator_RespectsLengthAndClasses_AndIsRandom()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate(20);
            Assert.Equal(20, p.Length);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => "!@#$%&*?-_=+".Contains(c));
            Assert.DoesNotContain(p, c => "lIO01".Contains(c));
            seen.Add(p);
        }
        Assert.Equal(200, seen.Count);
        Assert.Equal(8, PasswordGenerator.Generate(1).Length);
        Assert.Equal(128, PasswordGenerator.Generate(9999).Length);
    }
}
