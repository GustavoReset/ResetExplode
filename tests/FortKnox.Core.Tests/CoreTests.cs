using FortKnox.Core;
using FortKnox.Core.Changes;
using FortKnox.Core.Config;
using FortKnox.Core.Events;
using FortKnox.Core.Jobs;
using FortKnox.Core.Modules;
using FortKnox.Core.State;
using Microsoft.Win32;
using Xunit;

namespace FortKnox.Core.Tests;

public sealed record Ping(int N) : IFortKnoxEvent;
public sealed record CounterState(int Count = 0) : IStateSlice { public CounterState() : this(0) { } }

/// <summary>Captura logs para os testes conferirem erros.</summary>
public sealed class ListLog : ILog
{
    public readonly List<(LogLevel Level, string Msg)> Lines = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { lock (Lines) Lines.Add((level, message)); }
}

public sealed class EventBusTests
{
    [Fact]
    public void Delivers_ToAllSubscribers_InOrder()
    {
        var bus = new EventBus(null);
        var got = new List<string>();
        bus.Subscribe<Ping>(p => got.Add("a" + p.N), Delivery.Synchronous);
        bus.Subscribe<Ping>(p => got.Add("b" + p.N), Delivery.Synchronous);
        bus.Publish(new Ping(1));
        Assert.Equal(new[] { "a1", "b1" }, got);
    }

    [Fact]
    public void Dispose_StopsDelivery_AndRemovesSubscription()
    {
        var bus = new EventBus(null);
        int n = 0;
        var sub = bus.Subscribe<Ping>(_ => n++, Delivery.Synchronous);
        bus.Publish(new Ping(1));
        sub.Dispose();
        bus.Publish(new Ping(2));
        Assert.Equal(1, n);
        Assert.Equal(0, bus.CountFor<Ping>());
    }

    [Fact]
    public void FailingHandler_IsLogged_AndOthersStillReceive()
    {
        var log = new ListLog();
        var bus = new EventBus(null, log);
        bool second = false;
        bus.Subscribe<Ping>(_ => throw new InvalidOperationException("x"), Delivery.Synchronous);
        bus.Subscribe<Ping>(_ => second = true, Delivery.Synchronous);
        bus.Publish(new Ping(1));
        Assert.True(second);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error);
    }

    [Fact]
    public void UiThread_GoesThroughSynchronizationContext()
    {
        var ctx = new QueueContext();
        var bus = new EventBus(ctx);
        int n = 0;
        bus.Subscribe<Ping>(_ => n++); // padrão = UiThread
        bus.Publish(new Ping(1));
        Assert.Equal(0, n);           // ainda na fila da "interface"
        ctx.RunAll();
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Background_RunsOffThePublishingThread()
    {
        var bus = new EventBus(null);
        // Publish não espera o handler: ele só roda depois que liberamos o portão.
        using var gate = new ManualResetEventSlim(false);
        var done = new TaskCompletionSource();
        bus.Subscribe<Ping>(_ => { gate.Wait(); done.SetResult(); }, Delivery.Background);
        bus.Publish(new Ping(1));
        Assert.False(done.Task.IsCompleted);
        gate.Set();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _q = new();
        public override void Post(SendOrPostCallback d, object? state) => _q.Enqueue((d, state));
        public void RunAll() { while (_q.Count > 0) { var (d, s) = _q.Dequeue(); d(s); } }
    }
}

public sealed class StateStoreTests
{
    [Fact]
    public void Get_WithoutWrites_ReturnsDefault()
        => Assert.Equal(0, new StateStore(new EventBus(null)).Get<CounterState>().Count);

    [Fact]
    public void Update_NotifiesWatchers_OnlyWhenValueChanges()
    {
        var bus = new EventBus(null);
        var store = new StateStore(bus);
        var seen = new List<int>();
        store.Watch<CounterState>(s => seen.Add(s.Count), Delivery.Synchronous);
        store.Update<CounterState>(s => s with { Count = 1 });
        store.Update<CounterState>(s => s with { Count = 1 }); // igual: não avisa
        store.Update<CounterState>(s => s with { Count = 2 });
        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(2, store.Get<CounterState>().Count);
    }

    [Fact]
    public async Task ConcurrentUpdates_DoNotLoseIncrements()
    {
        var store = new StateStore(new EventBus(null));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++) store.Update<CounterState>(s => s with { Count = s.Count + 1 });
        })));
        Assert.Equal(4000, store.Get<CounterState>().Count);
    }
}

public sealed class JobRunnerTests
{
    [Fact]
    public async Task Success_ReportsProgress_AndPublishesStartAndFinish()
    {
        var bus = new EventBus(null);
        var events = new List<string>();
        bus.Subscribe<JobStarted>(e => events.Add("start"), Delivery.Synchronous);
        bus.Subscribe<JobFinished>(e => events.Add("end:" + e.Outcome), Delivery.Synchronous);
        var jobs = new JobRunner(bus);
        var h = jobs.Start("teste", "Contar", async (p, ct) => { p.Report(new JobProgress(0.5, "metade")); await Task.Delay(10, ct); });
        await h.Completion;
        Assert.Equal(JobOutcome.Succeeded, h.Outcome);
        Assert.Equal("metade", h.LastProgress?.Status);
        Assert.Equal(new[] { "start", "end:Succeeded" }, events);
        Assert.Empty(jobs.Running);
    }

    [Fact]
    public async Task Cancel_EndsAsCancelled()
    {
        var jobs = new JobRunner(new EventBus(null));
        var h = jobs.Start("teste", "Esperar", (p, ct) => Task.Delay(Timeout.Infinite, ct));
        h.Cancel();
        await h.Completion;
        Assert.Equal(JobOutcome.Cancelled, h.Outcome);
        h.Cancel(); // depois de terminado: não lança
    }

    [Fact]
    public async Task Failure_IsCaptured_NotThrown()
    {
        var log = new ListLog();
        var jobs = new JobRunner(new EventBus(null), log);
        var h = jobs.Start("teste", "Quebrar", (p, ct) => throw new IOException("disco sumiu"));
        await h.Completion;
        Assert.Equal(JobOutcome.Failed, h.Outcome);
        Assert.Equal("disco sumiu", h.Error);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Exclusive_RejectsSecondExclusive_ButAllowsNormal()
    {
        var jobs = new JobRunner(new EventBus(null));
        var a = jobs.Start("forja", "Benchmark", (p, ct) => Task.Delay(Timeout.Infinite, ct), new JobOptions(Exclusive: true));
        var ex = Assert.Throws<JobRejectedException>(() =>
            jobs.Start("muralha", "Varredura completa", (p, ct) => Task.CompletedTask, new JobOptions(Exclusive: true)));
        Assert.Contains("Benchmark", ex.Message);
        var normal = jobs.Start("radar", "Ping", (p, ct) => Task.CompletedTask);
        await normal.Completion;
        await jobs.CancelAllAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobOutcome.Cancelled, a.Outcome);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkcfg-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "modules.json");
    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    public sealed class OverlayPrefs { public string Corner { get; set; } = "TL"; public double Scale { get; set; } = 1.0; }

    [Fact]
    public void Section_DefaultsThenPersists_AcrossInstances()
    {
        var s = new SettingsStore(FilePath).Section<OverlayPrefs>("vigia");
        Assert.Equal("TL", s.Value.Corner);
        s.Update(v => { v.Corner = "BR"; v.Scale = 1.25; });

        var again = new SettingsStore(FilePath).Section<OverlayPrefs>("vigia");
        Assert.Equal("BR", again.Value.Corner);
        Assert.Equal(1.25, again.Value.Scale);
        Assert.Contains("\"schema\": 2", File.ReadAllText(FilePath));
    }

    [Fact]
    public void SecondWrite_KeepsBackup()
    {
        var s = new SettingsStore(FilePath).Section<OverlayPrefs>("vigia");
        s.Update(v => v.Corner = "TR");
        s.Update(v => v.Corner = "BL");
        Assert.True(File.Exists(FilePath + ".bak"));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Migrate_OnlyWhenSectionMissing()
    {
        var store = new SettingsStore(FilePath);
        Assert.True(store.Migrate<OverlayPrefs>("vigia", () => new OverlayPrefs { Corner = "BR" }));
        Assert.False(store.Migrate<OverlayPrefs>("vigia", () => new OverlayPrefs { Corner = "TL" }));
        Assert.Equal("BR", store.Section<OverlayPrefs>("vigia").Value.Corner);
    }

    [Fact]
    public void CorruptFile_IsSetAside_NotOverwritten()
    {
        File.WriteAllText(FilePath, "{ isto não é json");
        var log = new ListLog();
        var s = new SettingsStore(FilePath, log).Section<OverlayPrefs>("vigia");
        Assert.Equal("TL", s.Value.Corner);
        Assert.Single(Directory.GetFiles(_dir, "modules.json.corrompido-*"));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warn);
    }

    [Fact]
    public void UnknownSections_ArePreserved()
    {
        File.WriteAllText(FilePath, "{\"schema\":2,\"modules\":{\"futuro\":{\"x\":1}}}");
        new SettingsStore(FilePath).Section<OverlayPrefs>("vigia").Update(v => v.Corner = "BR");
        Assert.Contains("futuro", File.ReadAllText(FilePath));
    }
}

public sealed class ChangeJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkjr-" + Guid.NewGuid().ToString("N"));
    private readonly string _key = @"Software\FortKnoxTests\" + Guid.NewGuid().ToString("N");
    public ChangeJournalTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
    }

    private ChangeJournal NewJournal(IEventBus? bus = null)
    {
        var j = new ChangeJournal(Path.Combine(_dir, "history.db"), bus ?? new EventBus(null));
        j.Register(new RegistryValueChange());
        return j;
    }

    private object? Read(string name) { using var k = Registry.CurrentUser.OpenSubKey(_key); return k?.GetValue(name); }

    [Fact]
    public async Task Registry_ApplyAndRevert_RestoresPreviousValue()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("Modo", 1, RegistryValueKind.DWord);
        var j = NewJournal();
        var f = await j.ApplyAsync(RegistryValueChange.Request("arsenal", "Modo de Jogo", new("HKCU", _key, "Modo", "DWord", "0")));
        Assert.Equal(0, Read("Modo"));
        Assert.Equal(FerrolhoState.Applied, f.State);

        await j.RevertAsync(f.Id);
        Assert.Equal(1, Read("Modo"));
        Assert.Empty(j.List("arsenal"));
        Assert.Single(j.List("arsenal", includeReverted: true));
    }

    [Fact]
    public async Task Registry_ValueThatDidNotExist_IsDeletedOnRevert()
    {
        var j = NewJournal();
        var f = await j.ApplyAsync(RegistryValueChange.Request("arsenal", "Novo", new("HKCU", _key, "Novo", "String", "abc")));
        Assert.Equal("abc", Read("Novo"));
        await j.RevertAsync(f.Id);
        Assert.Null(Read("Novo"));
    }

    [Fact]
    public async Task Undo_SurvivesRestart()
    {
        var f = await NewJournal().ApplyAsync(RegistryValueChange.Request("arsenal", "Persistente", new("HKCU", _key, "P", "DWord", "7")));
        var reopened = NewJournal(); // como se o app tivesse fechado e aberto
        Assert.Single(reopened.List());
        await reopened.RevertAsync(f.Id);
        Assert.Null(Read("P"));
    }

    [Fact]
    public async Task RevertTwice_IsRefusedWithClearMessage()
    {
        var j = NewJournal();
        var f = await j.ApplyAsync(RegistryValueChange.Request("arsenal", "X", new("HKCU", _key, "X", "DWord", "1")));
        await j.RevertAsync(f.Id);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => j.RevertAsync(f.Id));
        Assert.Contains("já foi desfeito", ex.Message);
    }

    [Fact]
    public async Task FailingApply_IsRecordedAsFailed()
    {
        var j = NewJournal();
        j.Register(new Boom());
        await Assert.ThrowsAsync<IOException>(() => j.ApplyAsync(new ChangeRequest("t", "Quebra", "boom", "{}")));
        var f = Assert.Single(j.List("t"));
        Assert.Equal(FerrolhoState.Failed, f.State);
        Assert.Equal("falhou de propósito", f.Error);
    }

    [Fact]
    public async Task Events_ArePublished()
    {
        var bus = new EventBus(null);
        int applied = 0, reverted = 0;
        bus.Subscribe<ChangeApplied>(_ => applied++, Delivery.Synchronous);
        bus.Subscribe<ChangeReverted>(_ => reverted++, Delivery.Synchronous);
        var j = NewJournal(bus);
        var f = await j.ApplyAsync(RegistryValueChange.Request("arsenal", "E", new("HKCU", _key, "E", "DWord", "1")));
        await j.RevertAsync(f.Id);
        Assert.Equal((1, 1), (applied, reverted));
    }

    private sealed class Boom : IChangeHandler
    {
        public string Kind => "boom";
        public Task<string> ApplyAsync(string payload, CancellationToken ct) => throw new IOException("falhou de propósito");
        public Task RevertAsync(string undoData, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class LastroAndRegistryTests
{
    private sealed class Fixed(string id, HealthLevel level, double score, double weight = 1) : IHealthIndicator
    {
        public string Id => id; public string Title => id; public double Weight => weight;
        public HealthReading Read() => new(level, score, "motivo");
    }

    [Fact]
    public void Lastro_IsWeightedAverage()
    {
        var s = Lastro.Calculate(new IHealthIndicator[] { new Fixed("a", HealthLevel.Bom, 100, 3), new Fixed("b", HealthLevel.Atencao, 60, 1) });
        Assert.Equal(90, s.Lastro);
        Assert.Equal(HealthLevel.Atencao, s.Level);
        Assert.Equal("b", s.Worst[0].Id);
    }

    [Fact]
    public void Lastro_AnyRuim_CapsTheScore()
    {
        var s = Lastro.Calculate(new IHealthIndicator[] { new Fixed("a", HealthLevel.Bom, 100, 10), new Fixed("b", HealthLevel.Ruim, 20, 1) });
        Assert.Equal(Lastro.RuimCap, s.Lastro);
        Assert.Equal(HealthLevel.Ruim, s.Level);
    }

    [Fact]
    public void Lastro_NoIndicators_IsNotCalculated() => Assert.Equal(-1, Lastro.Calculate(Array.Empty<IHealthIndicator>()).Lastro);

    private sealed class FakeModule(string id) : IModule
    {
        public readonly List<string> Calls = new();
        public string Id => id;
        public string DisplayName => id.ToUpperInvariant();
        public IReadOnlyList<NavEntry> Pages => new[] { new NavEntry($"{id}/a", "A", "", 1), new NavEntry($"{id}/b", "B", "", 2) };
        public ModuleCapabilities Capabilities => ModuleCapabilities.None;
        public Task InitializeAsync(IModuleContext ctx, CancellationToken ct) { Calls.Add("init"); return Task.CompletedTask; }
        public Task OnPageActivatedAsync(string route, CancellationToken ct) { Calls.Add("on:" + route); return Task.CompletedTask; }
        public Task OnPageDeactivatedAsync(string route) { Calls.Add("off:" + route); return Task.CompletedTask; }
        public IEnumerable<IHealthIndicator> HealthIndicators => new[] { new Fixed(id, HealthLevel.Bom, 80) };
        public ValueTask ShutdownAsync() { Calls.Add("shutdown"); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Registry_InitializesOnce_AndSwitchesPages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fkhost-" + Guid.NewGuid().ToString("N"));
        var host = new FortKnoxHost(dir, null, new ListLog());
        try
        {
            var cofre = new FakeModule("cofre");
            var radar = new FakeModule("radar");
            host.Modules.Add(cofre);
            host.Modules.Add(radar);
            string? last = null;
            host.Bus.Subscribe<PageChanged>(e => last = e.To, Delivery.Synchronous);

            await host.Modules.NavigateAsync("cofre/a");
            await host.Modules.NavigateAsync("cofre/b");
            await host.Modules.NavigateAsync("radar/a");

            Assert.Equal(new[] { "init", "on:cofre/a", "off:cofre/a", "on:cofre/b", "off:cofre/b" }, cofre.Calls);
            Assert.Equal("radar/a", last);
            Assert.Equal(80, host.Modules.RecalculateLastro().Lastro);
            Assert.Equal(80, host.State.Get<HealthState>().Lastro);
            Assert.Throws<InvalidOperationException>(() => host.Modules.Add(new FakeModule("cofre")));

            // Página que ainda não virou módulo: só desativa a anterior
            await host.Modules.NavigateAsync("legado/PageDash");
            Assert.Equal("off:radar/a", radar.Calls[^1]);
            Assert.Equal("legado/PageDash", host.Modules.ActiveRoute);

            // Encerrar duas vezes (janela e depois o host) chama cada módulo uma vez só
            await host.Modules.ShutdownAsync();
            await host.Modules.ShutdownAsync();
            Assert.Single(cofre.Calls, c => c == "shutdown");
        }
        finally
        {
            await host.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
