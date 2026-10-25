using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ResetExplode;

public sealed record DiskBenchResult(string Drive, long FileMb, double SeqReadMBs, double SeqWriteMBs,
                                     double ReadIopsQ1, double WriteIopsQ1, double ReadLatencyUs, double WriteLatencyUs,
                                     double ReadIopsQ8)
{
    /// <summary>Nota só para a barra do histórico: sequencial pesa em cópia/carregamento, 4K em abrir programas e sistema.</summary>
    public double Score => (SeqReadMBs + SeqWriteMBs) / 2 + (ReadIopsQ1 + WriteIopsQ1) / 200.0 + ReadIopsQ8 / 2000.0;
}

/// <summary>
/// Benchmark de armazenamento no estilo CrystalDiskMark: leitura e escrita sequenciais (blocos de 1 MB) e acesso
/// aleatório de 4 KB (1 e 8 requisições em paralelo). Usa FILE_FLAG_NO_BUFFERING + WRITE_THROUGH para medir o disco
/// e não o cache do Windows. O arquivo de teste é criado numa pasta própria da unidade e apagado no final.
/// </summary>
public static class DiskBench
{
    private const int FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const int Block = 1 << 20;     // 1 MB (sequencial)
    private const int Small = 4096;        // 4 KB (aleatório)
    private const double RandomSeconds = 4;

    /// <summary>Unidades que podem ser testadas (fixas e removíveis, prontas e com pelo menos 1,5 GB livres).</summary>
    public static List<(string Root, string Text)> Drives()
    {
        var list = new List<(string, string)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                if (d.TotalFreeSpace < 1536L << 20) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" {d.VolumeLabel}";
                list.Add((d.Name, $"{d.Name.TrimEnd('\\')}{label}"));
            }
            catch { }
        }
        return list;
    }

    public static unsafe DiskBenchResult Run(string driveRoot, long fileMb, Action<string, double> progress, CancellationToken ct)
    {
        long fileBytes = fileMb << 20;
        var di = new DriveInfo(driveRoot);
        if (di.TotalFreeSpace < fileBytes + (512L << 20))
            throw new IOException($"Pouco espaço livre em {driveRoot} (precisa de {fileMb + 512} MB).");

        string dir = Path.Combine(driveRoot, "ResetExplodeBench");
        string path = Path.Combine(dir, "bench.tmp");
        Directory.CreateDirectory(dir);

        void* seqMem = NativeMemory.AlignedAlloc((nuint)Block, 4096);
        try
        {
            var seq = new Span<byte>(seqMem, Block);
            new Random(42).NextBytes(seq); // dados incompressíveis (não deixam o controlador "trapacear")

            using var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                                          FileOptions.WriteThrough | (FileOptions)FILE_FLAG_NO_BUFFERING, fileBytes);

            // ---------- Sequencial: escrita ----------
            var sw = Stopwatch.StartNew();
            for (long off = 0; off < fileBytes; off += Block)
            {
                ct.ThrowIfCancellationRequested();
                RandomAccess.Write(h, seq, off);
                progress("Escrita sequencial (1 MB)", 0.00 + 0.25 * off / fileBytes);
            }
            double seqWrite = fileMb / sw.Elapsed.TotalSeconds;

            // ---------- Sequencial: leitura ----------
            sw.Restart();
            for (long off = 0; off < fileBytes; off += Block)
            {
                ct.ThrowIfCancellationRequested();
                RandomAccess.Read(h, seq, off);
                progress("Leitura sequencial (1 MB)", 0.25 + 0.25 * off / fileBytes);
            }
            double seqRead = fileMb / sw.Elapsed.TotalSeconds;

            long slots = fileBytes / Small;

            // ---------- 4 KB aleatório, 1 requisição por vez: leitura ----------
            var small = new Span<byte>(seqMem, Small);
            double readIops = RandomLoop(h, small, slots, write: false, progress, "Leitura 4 KB aleatória (1 por vez)", 0.50, 0.15, ct, out double readLat);

            // ---------- 4 KB aleatório, 1 requisição por vez: escrita ----------
            double writeIops = RandomLoop(h, small, slots, write: true, progress, "Escrita 4 KB aleatória (1 por vez)", 0.65, 0.15, ct, out double writeLat);

            // ---------- 4 KB aleatório, 8 requisições em paralelo: leitura ----------
            progress("Leitura 4 KB aleatória (8 em paralelo)", 0.80);
            double q8 = ParallelRead(h, slots, 8, ct, progress);

            progress("Concluído", 1.0);
            return new DiskBenchResult(driveRoot.TrimEnd('\\'), fileMb, seqRead, seqWrite, readIops, writeIops, readLat, writeLat, q8);
        }
        finally
        {
            NativeMemory.AlignedFree(seqMem);
            try { File.Delete(path); } catch { }
            try { Directory.Delete(dir); } catch { }
        }
    }

    private static double RandomLoop(Microsoft.Win32.SafeHandles.SafeFileHandle h, Span<byte> buf, long slots, bool write,
                                     Action<string, double> progress, string phase, double start, double span,
                                     CancellationToken ct, out double latencyUs)
    {
        var rng = new Random(write ? 7 : 3);
        long ops = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RandomSeconds)
        {
            // checa o cancelamento a cada 64 operações (barato) e atualiza a barra
            for (int i = 0; i < 64; i++)
            {
                long off = rng.NextInt64(slots) * Small;
                if (write) RandomAccess.Write(h, buf, off); else RandomAccess.Read(h, buf, off);
                ops++;
            }
            ct.ThrowIfCancellationRequested();
            progress(phase, start + span * Math.Min(1, sw.Elapsed.TotalSeconds / RandomSeconds));
        }
        double secs = sw.Elapsed.TotalSeconds;
        latencyUs = secs / ops * 1e6;
        return ops / secs;
    }

    private static unsafe double ParallelRead(Microsoft.Win32.SafeHandles.SafeFileHandle h, long slots, int threads,
                                              CancellationToken ct, Action<string, double> progress)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int seed = 100 + t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                void* mem = NativeMemory.AlignedAlloc((nuint)Small, 4096);
                try
                {
                    var buf = new Span<byte>(mem, Small);
                    var rng = new Random(seed);
                    long local = 0;
                    while (sw.Elapsed.TotalSeconds < RandomSeconds && !ct.IsCancellationRequested)
                    {
                        for (int i = 0; i < 32; i++)
                        {
                            RandomAccess.Read(h, buf, rng.NextInt64(slots) * Small);
                            local++;
                        }
                    }
                    Interlocked.Add(ref total, local);
                }
                finally { NativeMemory.AlignedFree(mem); }
            }, TaskCreationOptions.LongRunning);
        }
        while (!Task.WaitAll(tasks, 150))
            progress("Leitura 4 KB aleatória (8 em paralelo)", 0.80 + 0.19 * Math.Min(1, sw.Elapsed.TotalSeconds / RandomSeconds));
        ct.ThrowIfCancellationRequested();
        return total / sw.Elapsed.TotalSeconds;
    }
}
