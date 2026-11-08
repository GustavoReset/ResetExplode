using System.IO;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FortKnox;

public sealed record ProcessNetUsage(int Pid, string Name, double DownBps, double UpBps, long TotalDown, long TotalUp)
{
    public double TotalBps => DownBps + UpBps;
}

/// <summary>
/// Contabilidade de bytes por processo, sem nenhuma dependência do ETW (testável sem administrador).
/// Os eventos chegam numa thread do ETW e o <see cref="Snapshot"/> é lido na thread da interface: tudo sob lock.
/// </summary>
public sealed class NetUsageAggregator
{
    private const int MaxEntries = 1000; // teto de segurança: máquina com milhares de processos de vida curta não cresce sem limite

    private sealed class Counter { public long Down, Up, LastDown, LastUp, Touched; }

    private readonly object _lock = new();
    private readonly Dictionary<int, Counter> _byPid = new();
    private long _tick;

    public void Add(int pid, long bytes, bool received)
    {
        if (bytes <= 0 || pid < 0) return;
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out var c))
            {
                if (_byPid.Count >= MaxEntries) DropOldest();
                _byPid[pid] = c = new Counter();
            }
            if (received) c.Down += bytes; else c.Up += bytes;
            c.Touched = _tick;
        }
    }

    /// <summary>Taxas (bytes/s) desde o snapshot anterior. <paramref name="seconds"/> = tempo decorrido. Ordenado por maior uso.</summary>
    public List<(int Pid, double DownBps, double UpBps, long TotalDown, long TotalUp)> Snapshot(double seconds)
    {
        seconds = Math.Max(seconds, 0.001);
        lock (_lock)
        {
            _tick++;
            var list = new List<(int, double, double, long, long)>(_byPid.Count);
            foreach (var (pid, c) in _byPid)
            {
                list.Add((pid, (c.Down - c.LastDown) / seconds, (c.Up - c.LastUp) / seconds, c.Down, c.Up));
                c.LastDown = c.Down; c.LastUp = c.Up;
            }
            return list.OrderByDescending(x => x.Item2 + x.Item3).ThenByDescending(x => x.Item4 + x.Item5).ToList();
        }
    }

    public void Clear() { lock (_lock) { _byPid.Clear(); _tick = 0; } }
    public int Count { get { lock (_lock) return _byPid.Count; } }

    private void DropOldest()
    {
        int oldest = _byPid.OrderBy(kv => kv.Value.Touched).First().Key;
        _byPid.Remove(oldest);
    }
}

/// <summary>
/// Consumo de rede por processo via ETW (provedor "Microsoft-Windows-Kernel-Network": bytes enviados/recebidos por TCP e UDP,
/// IPv4 e IPv6). Exige administrador (o app já roda assim). Sessão própria, parada ao descartar; uma sessão órfã de uma execução
/// anterior fechada à força é encerrada na abertura. É estatística em tempo real, não contabilidade: sob carga extrema o ETW
/// pode descartar eventos. Inclui tráfego local (loopback) quando o provedor o reporta.
/// </summary>
public sealed class ProcessBandwidthMonitor : IDisposable
{
    private const string SessionName = "FortKnox-Net";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // IDs de evento do Microsoft-Windows-Kernel-Network
    private static readonly HashSet<int> SendIds = new() { 10, 26, 42, 58 };   // TCP v4/v6 e UDP v4/v6 enviados
    private static readonly HashSet<int> RecvIds = new() { 11, 27, 43, 59 };   // idem, recebidos

    private readonly NetUsageAggregator _usage = new();
    private readonly Dictionary<int, string> _names = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _lastSnapshot;

    public bool Running => _thread?.IsAlive == true;
    public string? Error { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { } // órfã de execução anterior
            _usage.Clear();
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            var parser = new RegisteredTraceEventParser(_session.Source);
            parser.All += OnEvent;
            _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, ulong.MaxValue);
            _lastSnapshot = Stopwatch.GetTimestamp();
            Error = null;
            var session = _session;
            _thread = new Thread(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Error = ex.Message; }
            }) { IsBackground = true, Name = "ETW rede" };
            _thread.Start();
        }
        catch (UnauthorizedAccessException) { Fail("O monitor de consumo precisa do aplicativo executando como administrador."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException)
        { Fail("Não foi possível iniciar o monitor de rede: " + ex.Message); }
    }

    private void Fail(string message)
    {
        Error = message;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderGuid != KernelNetwork) return;
        int id = (int)e.ID;
        bool send = SendIds.Contains(id), recv = RecvIds.Contains(id);
        if (!send && !recv) return;
        try
        {
            // O PID do cabeçalho do evento é o do contexto de execução (pode ser o System em recebimentos): o do payload é o dono real.
            long pid = Convert.ToInt64(e.PayloadByName("PID"));
            long size = Convert.ToInt64(e.PayloadByName("size"));
            _usage.Add((int)pid, size, recv);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentNullException) { }
    }

    /// <summary>Taxas desde a última chamada, com o nome do processo. Chamar ~1×/s na thread da interface.</summary>
    public List<ProcessNetUsage> Snapshot()
    {
        long now = Stopwatch.GetTimestamp();
        double secs = (now - _lastSnapshot) / (double)Stopwatch.Frequency;
        _lastSnapshot = now;
        return _usage.Snapshot(secs).Select(r => new ProcessNetUsage(r.Pid, NameOf(r.Pid), r.DownBps, r.UpBps, r.TotalDown, r.TotalUp)).ToList();
    }

    private string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var cached) && !cached.EndsWith("(encerrado)")) return cached;
        string name;
        if (pid == 0) name = "Inativo";
        else if (pid == 4) name = "System";
        else
        {
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { name = $"PID {pid} (encerrado)"; }
        }
        _names[pid] = name;
        return name;
    }

    public void Stop()
    {
        var session = _session;
        _session = null;
        try { session?.Dispose(); } catch { } // StopOnDispose encerra a sessão no Windows e faz Process() retornar
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();
}
