using FortKnox;
using Xunit;

namespace FortKnox.Core.Tests;

public sealed class UninstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkunin-" + Guid.NewGuid().ToString("N"));
    private string ProgramFiles => Path.Combine(_root, "Program Files");
    private string AppData => Path.Combine(_root, "AppData");
    public UninstallTests() { Directory.CreateDirectory(ProgramFiles); Directory.CreateDirectory(AppData); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static InstalledProgram Prog(string name, string publisher = "", string location = "") =>
        new(name, publisher, "1.0", null, 0, location, "uninstall.exe", true, $@"HKLM\X\{name}");

    private string Dir(params string[] parts)
    {
        string d = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "x.dat"), "dados");
        return d;
    }

    private UninstallService.SearchRoots Roots => new(new[] { ProgramFiles, AppData });

    [Theory]
    [InlineData("Foo App 2.3.1 (64-bit)", "foo app")]
    [InlineData("Foo-App", "foo app")]
    [InlineData("Steam", "steam")]
    [InlineData("7-Zip 24.08 (x64)", "7 zip")]
    public void Normalize_RemovesVersionAndArch(string input, string expected) => Assert.Equal(expected, UninstallService.Normalize(input));

    [Fact]
    public void SplitCommand_HandlesQuotesAndMsi()
    {
        Assert.Equal(("C:\\Program Files\\Foo\\unins000.exe", "/SILENT"), UninstallService.SplitCommand("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT"));
        Assert.Equal(("MsiExec.exe", "/I{ABC}"), UninstallService.SplitCommand("MsiExec.exe /I{ABC}"));
    }

    [Fact]
    public void Finds_InstallLocation_AndExactNameFolders()
    {
        string loc = Dir("Program Files", "Foo App");
        string data = Dir("AppData", "Foo App");
        var found = UninstallService.FindFolderLeftovers(Prog("Foo App 2.0", location: loc), Roots, Array.Empty<InstalledProgram>());
        Assert.Contains(found, l => l.Path == loc);
        Assert.Contains(found, l => l.Path == data);
        Assert.All(found, l => Assert.True(l.Bytes > 0));
    }

    [Fact]
    public void Finds_PublisherSubfolder_ButNotPublisherItself()
    {
        string pub = Dir("AppData", "Acme Corp");
        string sub = Dir("AppData", "Acme Corp", "Foo App");
        Dir("AppData", "Acme Corp", "Outro Produto");
        var found = UninstallService.FindFolderLeftovers(Prog("Foo App", "Acme Corp"), Roots, Array.Empty<InstalledProgram>());
        Assert.Contains(found, l => l.Path == sub);
        Assert.DoesNotContain(found, l => l.Path == pub);
    }

    [Fact]
    public void Ignores_PartialNameMatches()
    {
        Dir("AppData", "Foo App Helper");
        Dir("AppData", "Foo");
        var found = UninstallService.FindFolderLeftovers(Prog("Foo App"), Roots, Array.Empty<InstalledProgram>());
        Assert.Empty(found);
    }

    [Fact]
    public void Never_TouchesFoldersOfOtherInstalledPrograms()
    {
        string shared = Dir("Program Files", "Foo App");
        var other = Prog("Foo App Pro", location: shared); // outro programa instalado na mesma pasta
        var found = UninstallService.FindFolderLeftovers(Prog("Foo App", location: shared), Roots, new[] { other });
        Assert.DoesNotContain(found, l => l.Path == shared);
    }

    [Fact]
    public void Never_SuggestsProtectedOrRootFolders()
    {
        Dir("Program Files", "Microsoft");
        var found = UninstallService.FindFolderLeftovers(Prog("Microsoft"), Roots, Array.Empty<InstalledProgram>());
        Assert.Empty(found);
        var root = UninstallService.FindFolderLeftovers(Prog("Foo App", location: ProgramFiles), Roots, Array.Empty<InstalledProgram>());
        Assert.DoesNotContain(root, l => l.Path == ProgramFiles);
    }

    [Fact]
    public void ShortNames_AreNotMatched()
    {
        Dir("AppData", "Go");
        Assert.Empty(UninstallService.FindFolderLeftovers(Prog("Go"), Roots, Array.Empty<InstalledProgram>()));
    }

    [Fact]
    public void Removing_FolderLeftover_SendsToRecycleBin()
    {
        string d = Dir("AppData", "Foo App");
        var (removed, failed, _) = UninstallService.RemoveLeftovers(
            new[] { new Leftover(LeftoverKind.Folder, d, 5, "teste", true) }, Path.Combine(_root, "backup"));
        Assert.Equal(1, removed);
        Assert.Empty(failed);
        Assert.False(Directory.Exists(d));
    }
}
