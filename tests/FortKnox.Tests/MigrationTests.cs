using Xunit;

namespace FortKnox.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkmig-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "ResetExplode");
    private string Target => Path.Combine(_root, "FortKnox");
    public MigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void MakeLegacy()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "sub", "fundo"));
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(Legacy, "sub", "fundo", "x.txt"), "x");
    }

    [Fact]
    public void Copies_EverythingIncludingSubfolders_AndKeepsLegacy()
    {
        MakeLegacy();
        Assert.True(LegacyMigration.Migrate(Legacy, Target));
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(Target, "settings.json")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(Target, "sub", "fundo", "x.txt")));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));   // não apaga a antiga
        Assert.False(Directory.Exists(Target + ".migrando"));
    }

    [Fact]
    public void NeverOverwritesExistingTarget()
    {
        MakeLegacy();
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "settings.json"), "novo");
        Assert.False(LegacyMigration.Migrate(Legacy, Target));
        Assert.Equal("novo", File.ReadAllText(Path.Combine(Target, "settings.json")));
    }

    [Fact]
    public void NoLegacy_DoesNothing()
    {
        Assert.False(LegacyMigration.Migrate(Legacy, Target));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void LeftoverFromInterruptedAttempt_IsReplaced_AndRunsOnlyOnce()
    {
        MakeLegacy();
        Directory.CreateDirectory(Target + ".migrando");
        File.WriteAllText(Path.Combine(Target + ".migrando", "lixo.txt"), "meio copiado");
        Assert.True(LegacyMigration.Migrate(Legacy, Target));
        Assert.False(File.Exists(Path.Combine(Target, "lixo.txt")));
        Assert.False(LegacyMigration.Migrate(Legacy, Target)); // segunda vez: já existe
    }

    [Fact]
    public void LockedFile_FailsQuietly_AndLeavesNoTarget()
    {
        MakeLegacy();
        using var lockHandle = new FileStream(Path.Combine(Legacy, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(LegacyMigration.Migrate(Legacy, Target));
        Assert.False(Directory.Exists(Target)); // nada pela metade com o nome final
    }
}
