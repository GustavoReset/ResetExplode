using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResetExplode;

public sealed record SurfaceScanResult(string Drive, bool Full, long ScannedBytes, long TotalBytes, long BadSectors,
                                       int SlowReads, double AvgMBs, List<(long Offset, long Length)> BadRanges, bool Cancelled);

/// <summary>
/// Teste de setores defeituosos (bad blocks) SOMENTE LEITURA: lê o volume inteiro (ou uma amostra) direto do disco,
/// sem cache do Windows, e anota onde a leitura falha ou fica muito lenta. Não grava nada, então é seguro em disco com dados.
/// Um setor que falha na leitura é um sinal sério de defeito; leituras muito lentas costumam indicar setores em desgaste.
/// </summary>
public static class DiskSurfaceScan
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const int Block = 4 << 20;        // leitura em blocos de 4 MB
    private const int Sector = 4096;          // varredura fina de um bloco com erro
    private const double SlowMs = 700;        // um bloco de 4 MB que demora mais que isso é suspeito
    private const long Segment = 256L << 20;  // no modo rápido: lê 256 MB e pula 9 segmentos

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    /// <summary>Unidades fixas e removíveis prontas para a verificação.</summary>
    public static List<(string Root, string Text)> Drives() => DiskBench.Drives();

    public static unsafe SurfaceScanResult Run(string driveRoot, bool full, Action<string, double> progress, CancellationToken ct)
    {
        var di = new DriveInfo(driveRoot);
        long total = di.TotalSize;
        string letter = driveRoot.TrimEnd('\\');
        using var h = CreateFileW(@"\\.\" + letter, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                                  FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Não foi possível abrir {letter} para leitura (erro {Marshal.GetLastWin32Error()}). Execute o programa como administrador.");

        void* mem = NativeMemory.AlignedAlloc((nuint)Block, 4096);
        var bad = new List<(long, long)>();
        long badSectors = 0, scanned = 0;
        int slow = 0;
        var clock = Stopwatch.StartNew();
        bool cancelled = false;
        try
        {
            var buf = new Span<byte>(mem, Block);
            long step = full ? Segment : Segment * 10;
            for (long segStart = 0; segStart < total && !cancelled; segStart += step)
            {
                long segEnd = Math.Min(total, segStart + Segment);
                for (long off = segStart; off < segEnd; off += Block)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    int want = (int)Math.Min(Block, (segEnd - off) / Sector * Sector);
                    if (want <= 0) break;
                    var sw = Stopwatch.StartNew();
                    try { RandomAccess.Read(h, buf[..want], off); scanned += want; }
                    catch (IOException)
                    {
                        // bloco com erro: lê setor a setor para achar exatamente onde está o defeito
                        for (int s = 0; s < want; s += Sector)
                        {
                            try { RandomAccess.Read(h, buf.Slice(0, Sector), off + s); scanned += Sector; }
                            catch (IOException)
                            {
                                badSectors++;
                                long pos = off + s;
                                if (bad.Count > 0 && bad[^1].Item1 + bad[^1].Item2 == pos) bad[^1] = (bad[^1].Item1, bad[^1].Item2 + Sector);
                                else if (bad.Count < 500) bad.Add((pos, Sector));
                            }
                        }
                    }
                    if (sw.Elapsed.TotalMilliseconds > SlowMs) slow++;
                    double done = scanned / (double)Math.Max(1, full ? total : total / 10);
                    double mbs = scanned / 1048576.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds);
                    progress($"Lendo {letter} · {off / 1073741824.0:0.0} de {total / 1073741824.0:0.0} GB · {mbs:0} MB/s · {badSectors} setores ruins · {slow} leituras lentas",
                             Math.Min(1, done));
                }
            }
        }
        finally { NativeMemory.AlignedFree(mem); }

        double avg = scanned / 1048576.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds);
        return new SurfaceScanResult(driveRoot, full, scanned, total, badSectors, slow, avg, bad, cancelled);
    }
}
