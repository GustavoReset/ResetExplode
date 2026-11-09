using FortKnox.Core.Events;

namespace FortKnox.Core.Jobs;

/// <summary>Progresso de um trabalho: fração 0..1 (null = indeterminado) e frase curta para a tela.</summary>
public sealed record JobProgress(double? Fraction, string Status);

public enum JobOutcome { Running, Succeeded, Failed, Cancelled }

public sealed record JobOptions(
    /// <summary>Exclusivo: não roda junto com outro exclusivo (ex.: benchmark e varredura completa disputam CPU e disco).</summary>
    bool Exclusive = false);

/// <summary>Publicados pelo JobRunner para o Shell mostrar o indicador de trabalhos.</summary>
public sealed record JobStarted(Guid Id, string Owner, string Title) : IFortKnoxEvent;
public sealed record JobFinished(Guid Id, string Owner, string Title, JobOutcome Outcome, string? Error) : IFortKnoxEvent;

public sealed class JobHandle
{
    private readonly CancellationTokenSource _cts;
    internal JobHandle(string owner, string title, CancellationTokenSource cts) { Owner = owner; Title = title; _cts = cts; }

    public Guid Id { get; } = Guid.NewGuid();
    public string Owner { get; }
    public string Title { get; }
    public JobOutcome Outcome { get; internal set; } = JobOutcome.Running;
    public JobProgress? LastProgress { get; internal set; }
    public string? Error { get; internal set; }
    /// <summary>Termina quando o trabalho termina (nunca lança: o resultado está em Outcome/Error).</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;
    /// <summary>Pede o cancelamento. Seguro de chamar depois que o trabalho já terminou.</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}

public sealed class JobRejectedException(string message) : InvalidOperationException(message);

public interface IJobRunner
{
    /// <summary>Inicia em segundo plano. O trabalho recebe progresso e cancelamento. Exclusivo + outro exclusivo rodando = JobRejectedException.</summary>
    JobHandle Start(string owner, string title, Func<IProgress<JobProgress>, CancellationToken, Task> work, JobOptions? options = null);
    IReadOnlyList<JobHandle> Running { get; }
    /// <summary>Cancela tudo e espera terminar (fechamento do app).</summary>
    Task CancelAllAsync(TimeSpan timeout);
}

/// <summary>
/// Generaliza o padrão "botão vira CANCELAR" que já existe nas telas: todo trabalho longo tem dono, título, progresso,
/// cancelamento e resultado. Exceção do trabalho vira Outcome=Failed + mensagem, nunca derruba o app.
/// </summary>
public sealed class JobRunner : IJobRunner
{
    private readonly IEventBus _bus;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly List<(JobHandle Handle, bool Exclusive)> _running = new();

    public JobRunner(IEventBus bus, ILog? log = null) { _bus = bus; _log = log ?? NullLog.Instance; }

    public IReadOnlyList<JobHandle> Running { get { lock (_gate) return _running.Select(r => r.Handle).ToList(); } }

    public JobHandle Start(string owner, string title, Func<IProgress<JobProgress>, CancellationToken, Task> work, JobOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        bool exclusive = options?.Exclusive ?? false;
        var cts = new CancellationTokenSource();
        var handle = new JobHandle(owner, title, cts);
        lock (_gate)
        {
            if (exclusive && _running.Any(r => r.Exclusive))
            {
                var other = _running.First(r => r.Exclusive).Handle;
                throw new JobRejectedException($"Espere \"{other.Title}\" terminar ou cancele antes de começar \"{title}\".");
            }
            _running.Add((handle, exclusive));
        }
        _bus.Publish(new JobStarted(handle.Id, owner, title));
        var progress = new Progress(handle);
        handle.Completion = Task.Run(async () =>
        {
            try
            {
                await work(progress, cts.Token).ConfigureAwait(false);
                handle.Outcome = cts.IsCancellationRequested ? JobOutcome.Cancelled : JobOutcome.Succeeded;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { handle.Outcome = JobOutcome.Cancelled; }
            catch (Exception ex)
            {
                handle.Outcome = JobOutcome.Failed;
                handle.Error = ex.Message;
                _log.Error($"Trabalho \"{title}\" ({owner}) falhou", ex);
            }
            finally
            {
                lock (_gate) _running.RemoveAll(r => r.Handle == handle);
                cts.Dispose();
                _bus.Publish(new JobFinished(handle.Id, owner, title, handle.Outcome, handle.Error));
            }
        });
        return handle;
    }

    public async Task CancelAllAsync(TimeSpan timeout)
    {
        JobHandle[] all;
        lock (_gate) all = _running.Select(r => r.Handle).ToArray();
        foreach (var h in all) { try { h.Cancel(); } catch (ObjectDisposedException) { } }
        await Task.WhenAny(Task.WhenAll(all.Select(h => h.Completion)), Task.Delay(timeout)).ConfigureAwait(false);
    }

    private sealed class Progress(JobHandle handle) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => handle.LastProgress = value; // quem mostra lê no ritmo da tela (≤10×/s)
    }
}
