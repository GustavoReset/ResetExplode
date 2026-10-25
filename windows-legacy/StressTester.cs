using System.Management;

namespace ResetExplode;

/// <summary>Gera carga sintética de CPU (todos os threads) e/ou RAM (escrita + verificação).</summary>
public sealed class StressTester
{
    private CancellationTokenSource? _cts;
    private readonly List<Thread> _threads = new();
    private long _memErrors;
    private long _memPasses;

    public bool Running => _cts != null;
    public long MemoryErrors => Interlocked.Read(ref _memErrors);
    public long MemoryPasses => Interlocked.Read(ref _memPasses);
    public long MemoryAllocatedBytes { get; private set; }

    /// <param name="cpu">Carga em todos os processadores lógicos.</param>
    /// <param name="ram">Aloca uma fração da RAM livre e grava/verifica padrões continuamente.</param>
    /// <param name="ramFraction">Fração da memória livre a usar (0.1 a 0.9).</param>
    public void Start(bool cpu, bool ram, double ramFraction = 0.6)
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Interlocked.Exchange(ref _memErrors, 0);
        Interlocked.Exchange(ref _memPasses, 0);
        MemoryAllocatedBytes = 0;

        int cpuThreads = Environment.ProcessorCount;
        // Com RAM ativa, reserva um thread para o worker de memória.
        if (cpu && ram && cpuThreads > 1) cpuThreads--;

        if (cpu)
            for (int i = 0; i < cpuThreads; i++)
            {
                int seed = i;
                StartThread(() => CpuWorker(token, seed), $"Stress CPU {seed}");
            }

        if (ram)
        {
            long bytes = (long)(FreePhysicalMemory() * Math.Clamp(ramFraction, 0.1, 0.9));
            MemoryAllocatedBytes = bytes;
            StartThread(() => RamWorker(token, bytes), "Stress RAM");
        }
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
        _cts.Dispose();
        _cts = null;
        GC.Collect();
    }

    private void StartThread(ThreadStart body, string name)
    {
        // BelowNormal mantém a interface responsiva sem reduzir a carga (núcleos continuam a 100%).
        var t = new Thread(body) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _threads.Add(t);
        t.Start();
    }

    private static void CpuWorker(CancellationToken token, int seed)
    {
        // Mistura ponto flutuante (FMA/sqrt/sin) e inteiros sobre um buffer que cabe no cache L1/L2.
        var buf = new double[4096];
        for (int i = 0; i < buf.Length; i++) buf[i] = i + seed + 1;
        ulong x = (ulong)(seed + 1) * 0x9E3779B97F4A7C15UL;

        while (!token.IsCancellationRequested)
        {
            for (int rep = 0; rep < 64; rep++)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    double v = buf[i];
                    v = Math.FusedMultiplyAdd(v, 1.0000001, 0.5);
                    v = Math.Sqrt(v * v + 1.0) + Math.Sin(v) * 1e-3;
                    buf[i] = v > 1e12 ? i + 1 : v;

                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                }
            }
            if (x == 0) buf[0] = 0; // evita que o JIT elimine o cálculo inteiro
        }
    }

    private void RamWorker(CancellationToken token, long totalBytes)
    {
        const int chunkLongs = 16 * 1024 * 1024; // 128 MB por bloco
        var chunks = new List<long[]>();
        try
        {
            long allocated = 0;
            try
            {
                while (allocated + chunkLongs * 8L <= totalBytes && !token.IsCancellationRequested)
                {
                    chunks.Add(new long[chunkLongs]);
                    allocated += chunkLongs * 8L;
                }
            }
            catch (OutOfMemoryException)
            {
                // Segue com o que conseguiu alocar.
            }
            MemoryAllocatedBytes = allocated;
            if (chunks.Count == 0) return;

            ulong pass = 0;
            while (!token.IsCancellationRequested)
            {
                pass++;
                long pattern = unchecked((long)(pass * 0x9E3779B97F4A7C15UL));
                foreach (var c in chunks)
                {
                    for (int i = 0; i < c.Length; i++) c[i] = pattern ^ i;
                    if (token.IsCancellationRequested) return;
                }
                foreach (var c in chunks)
                {
                    long errs = 0;
                    for (int i = 0; i < c.Length; i++)
                        if (c[i] != (pattern ^ i)) errs++;
                    if (errs > 0) Interlocked.Add(ref _memErrors, errs);
                    if (token.IsCancellationRequested) return;
                }
                Interlocked.Increment(ref _memPasses);
            }
        }
        finally
        {
            chunks.Clear();
        }
    }

    private static long FreePhysicalMemory()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
                return Convert.ToInt64(mo["FreePhysicalMemory"]) * 1024; // vem em KB
        }
        catch { }
        return 1L << 30;
    }
}
