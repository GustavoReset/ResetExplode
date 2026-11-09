using FortKnox;
using FortKnox.Core.Changes;
using FortKnox.Core.Events;
using Microsoft.Win32;
using Xunit;

namespace FortKnox.Core.Tests;

/// <summary>Fase 3.4e: ajustes e removidos da inicialização como ferrolhos. Só mexe em valores de teste no HKCU.</summary>
public sealed class ArsenalChangesTests : IDisposable
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkars-" + Guid.NewGuid().ToString("N"));
    private readonly string _runName = "FortKnoxTeste-" + Guid.NewGuid().ToString("N")[..8];
    private string _state = "original";
    private readonly List<Tweak> _tweaks;
    private readonly ChangeJournal _journal;

    public ArsenalChangesTests()
    {
        Directory.CreateDirectory(_dir);
        // Ajuste falso: o "sistema" é o campo _state.
        _tweaks = new()
        {
            new Tweak
            {
                Id = "falso", Title = "Ajuste falso",
                Read = () => _state == "ligado",
                Write = (on, b) => { if (!b.ContainsKey("falso")) b["falso"] = _state; _state = on ? "ligado" : "desligado"; },
                Restore = b => { if (b.GetValueOrDefault("falso") is string v) _state = v; },
            },
        };
        _journal = new ChangeJournal(Path.Combine(_dir, "history.db"), new EventBus(null));
        ArsenalChanges.Register(_journal, _tweaks);
    }

    public void Dispose()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunPath, writable: true)) k?.DeleteValue(_runName, false);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? RunValue(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunPath);
        return k?.GetValue(name) as string;
    }

    private async Task RevertAllTweaks()
    {
        foreach (var f in ArsenalChanges.AppliedTweaks(_journal)) await _journal.RevertAsync(f.Id);
    }

    [Fact]
    public async Task Tweaks_StackAndRestoreToOriginal()
    {
        await _journal.ApplyAsync(ArsenalChanges.TweakRequest(_tweaks[0], true));
        await _journal.ApplyAsync(ArsenalChanges.TweakRequest(_tweaks[0], false));
        await _journal.ApplyAsync(ArsenalChanges.TweakRequest(_tweaks[0], true));
        Assert.Equal("ligado", _state);
        Assert.Equal(3, ArsenalChanges.AppliedTweaks(_journal).Count);

        await RevertAllTweaks();
        Assert.Equal("original", _state);
        Assert.Empty(ArsenalChanges.AppliedTweaks(_journal));
    }

    [Fact]
    public async Task LegacyTweakBackup_IsRevertedLast()
    {
        _state = "mexido pela versão antiga";
        await ArsenalChanges.MigrateLegacyAsync(_journal, new TweakBackup { ["falso"] = "original" }, Array.Empty<StartupBackup>());
        Assert.Equal("mexido pela versão antiga", _state); // migrar não mexe em nada
        await Task.Delay(20);
        await _journal.ApplyAsync(ArsenalChanges.TweakRequest(_tweaks[0], true));

        await RevertAllTweaks();
        Assert.Equal("original", _state);
    }

    [Fact]
    public async Task StartupRemoval_IsAFerrolho_AndComesBack()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunPath)) k.SetValue(_runName, "\"C:\\teste\\app.exe\" -min");
        var entry = StartupManager.List().Single(e => e.Name == _runName && !e.Machine);

        await _journal.ApplyAsync(ArsenalChanges.StartupRequest(entry));
        Assert.Null(RunValue(_runName));
        var removed = ArsenalChanges.RemovedStartup(_journal);
        Assert.Single(removed);
        Assert.DoesNotContain(ArsenalChanges.MachineSuffix, removed[0].Title);

        await _journal.RevertAsync(removed[0].Id);
        Assert.Equal("\"C:\\teste\\app.exe\" -min", RunValue(_runName));
        Assert.Empty(ArsenalChanges.RemovedStartup(_journal));
    }

    [Fact]
    public async Task LegacyStartupBackup_RestoresFromJournal()
    {
        var b = new StartupBackup { Kind = "Run", Machine = false, Name = _runName, Command = "teste.exe" };
        await ArsenalChanges.MigrateLegacyAsync(_journal, new TweakBackup(), new[] { b });
        Assert.Null(RunValue(_runName));

        var removed = ArsenalChanges.RemovedStartup(_journal);
        Assert.Single(removed);
        await _journal.RevertAsync(removed[0].Id);
        Assert.Equal("teste.exe", RunValue(_runName));
    }
}
