using FortKnox;
using Xunit;

namespace FortKnox.Core.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkclean-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "fkclean-fora-" + Guid.NewGuid().ToString("N"));
    public CleanupTests() { Directory.CreateDirectory(_root); Directory.CreateDirectory(_outside); }
    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside }) try { Directory.Delete(d, true); } catch { }
    }

    private string File(string rel, int bytes, DateTime when)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        System.IO.File.SetLastWriteTime(p, when);
        return p;
    }

    private CleanupCategory Cat(TimeSpan minAge, string[]? patterns = null) =>
        new("teste", "Teste", "", new[] { _root }, minAge, true, patterns);

    [Fact]
    public void Scan_CountsOldFiles_AndKeepsRecentOnes()
    {
        var now = DateTime.Now;
        File("velho.tmp", 1000, now.AddDays(-3));
        File("sub/velho2.tmp", 500, now.AddDays(-3));
        File("novo.tmp", 9999, now.AddMinutes(-5));
        var f = CleanupService.Scan(Cat(TimeSpan.FromDays(1)), now);
        Assert.Equal(2, f.Files);
        Assert.Equal(1500, f.Bytes);
    }

    [Fact]
    public void Clean_DeletesOnlyFound_AndReportsFreed()
    {
        var now = DateTime.Now;
        var old = File("velho.tmp", 1000, now.AddDays(-3));
        var recent = File("novo.tmp", 10, now);
        var f = CleanupService.Scan(Cat(TimeSpan.FromDays(1)), now);
        var r = CleanupService.Clean(new[] { f });
        Assert.Equal(1000, r.FreedBytes);
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(recent));
    }

    [Fact]
    public void Clean_SkipsFileInUse_WithoutThrowing()
    {
        var now = DateTime.Now;
        var locked = File("preso.tmp", 100, now.AddDays(-3));
        File("livre.tmp", 100, now.AddDays(-3));
        var f = CleanupService.Scan(Cat(TimeSpan.FromDays(1)), now);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = CleanupService.Clean(new[] { f });
            Assert.Equal(1, r.Deleted);
            Assert.Equal(1, r.Skipped);
        }
        Assert.True(System.IO.File.Exists(locked));
    }

    [Fact]
    public void Scan_DoesNotFollowJunctions_OutOfTheFolder()
    {
        var now = DateTime.Now;
        string precious = Path.Combine(_outside, "documento.docx");
        System.IO.File.WriteAllBytes(precious, new byte[50]);
        System.IO.File.SetLastWriteTime(precious, now.AddDays(-30));
        string link = Path.Combine(_root, "atalho");
        try { Directory.CreateSymbolicLink(link, _outside); }
        catch (Exception) { return; } // criar link pode exigir permissão: nesse caso o cenário não existe nesta máquina
        var f = CleanupService.Scan(Cat(TimeSpan.Zero), now);
        Assert.DoesNotContain(f.Paths, p => p.EndsWith("documento.docx"));
    }

    [Fact]
    public void Patterns_LimitToMatchingFiles()
    {
        var now = DateTime.Now;
        File("thumbcache_256.db", 300, now.AddDays(-3));
        File("outro.db", 300, now.AddDays(-3));
        var f = CleanupService.Scan(Cat(TimeSpan.FromDays(1), new[] { "thumbcache_*.db" }), now);
        Assert.Single(f.Paths);
    }

    [Fact]
    public void DefaultCategories_NeverPointAtUserDataOfBrowsers()
    {
        foreach (var c in CleanupService.DefaultCategories())
            foreach (var folder in c.Folders)
            {
                string name = Path.GetFileName(folder.TrimEnd('\\'));
                Assert.DoesNotContain(name, new[] { "Cookies", "History", "Login Data", "Bookmarks", "Local Storage" });
            }
    }

    [Fact]
    public void UserTempCategory_KeepsOnlyFilesModifiedWithinTheLastHour()
    {
        var category = CleanupService.DefaultCategories().Single(c => c.Id == "temp-usuario");

        Assert.Equal(TimeSpan.FromHours(1), category.MinAge);
    }
}
