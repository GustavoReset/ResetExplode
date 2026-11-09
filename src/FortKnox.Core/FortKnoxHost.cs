using FortKnox.Core.Changes;
using FortKnox.Core.Config;
using FortKnox.Core.Events;
using FortKnox.Core.Jobs;
using FortKnox.Core.Modules;
using FortKnox.Core.State;

namespace FortKnox.Core;

/// <summary>
/// Monta o núcleo uma vez, na abertura do app (custo: abrir o log e o SQLite, alguns milissegundos).
/// Arquivos em <paramref name="dataFolder"/>: logs\, modules.json (preferências por módulo) e history.db (Diário de Ferrolhos).
/// </summary>
public sealed class FortKnoxHost : IModuleContext, IAsyncDisposable
{
    private readonly FileLog? _fileLog;

    public FortKnoxHost(string dataFolder, SynchronizationContext? uiContext, ILog? log = null)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        if (log == null) { _fileLog = new FileLog(Path.Combine(dataFolder, "logs")); log = _fileLog; }
        Log = log;
        Bus = new EventBus(uiContext, Log);
        State = new StateStore(Bus);
        Jobs = new JobRunner(Bus, Log);
        Settings = new SettingsStore(Path.Combine(dataFolder, "modules.json"), Log);
        var journal = new ChangeJournal(Path.Combine(dataFolder, "history.db"), Bus, Log);
        journal.Register(new RegistryValueChange());
        Changes = journal;
        Modules = new ModuleRegistry(this);
    }

    public string DataFolder { get; }
    public ILog Log { get; }
    public IEventBus Bus { get; }
    public IStateStore State { get; }
    public IJobRunner Jobs { get; }
    public SettingsStore Settings { get; }
    public IChangeJournal Changes { get; }
    public ModuleRegistry Modules { get; }

    /// <summary>Fechamento: cancela trabalhos (até 3 s), encerra módulos e fecha o log.</summary>
    public async ValueTask DisposeAsync()
    {
        await Jobs.CancelAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Modules.ShutdownAsync().ConfigureAwait(false);
        Log.Info("Núcleo encerrado");
        _fileLog?.Dispose();
    }
}
