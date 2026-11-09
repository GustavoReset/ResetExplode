using System.IO;
using System.Runtime.InteropServices;

namespace FortKnox;

/// <summary>Uma categoria de limpeza: pastas onde procurar, regra do que pode sair e se começa marcada.</summary>
public sealed record CleanupCategory(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Folders,
    /// <summary>Arquivos modificados há menos que isto ficam (instalador ou programa pode estar usando agora).</summary>
    TimeSpan MinAge,
    bool PreSelect,
    /// <summary>Só os arquivos com estes padrões (null = todos).</summary>
    IReadOnlyList<string>? Patterns = null,
    bool NeedsAdmin = false);

/// <summary>Resultado da varredura de uma categoria (o que seria apagado).</summary>
public sealed record CleanupFinding(CleanupCategory Category, long Bytes, int Files, IReadOnlyList<string> Paths);

/// <summary>Resultado da limpeza.</summary>
public sealed record CleanupOutcome(long FreedBytes, int Deleted, int Skipped);

/// <summary>
/// ARSENAL · Limpeza (Fase 3.4b): temporários, caches que se recriam sozinhos e a Lixeira. Sem WPF (testável).
/// Regras de segurança: só caches e temporários (nada de documentos, cookies, histórico ou senhas dos navegadores);
/// não segue atalhos de pasta (junction/symlink); arquivos recentes ficam; arquivo em uso é pulado, nunca forçado.
/// Os itens apagados são caches recriáveis, por isso não vão para a Lixeira (iria só mover o lixo de lugar).
/// </summary>
public static class CleanupService
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    private static string Local => Env(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Env(Environment.SpecialFolder.ApplicationData);
    private static string Windows => Env(Environment.SpecialFolder.Windows);
    private static string ProgramData => Env(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Categorias padrão deste PC (pastas inexistentes são ignoradas na varredura).</summary>
    public static List<CleanupCategory> DefaultCategories()
    {
        var chromiumCaches = new List<string>();
        foreach (var (vendor, product) in new[] { ("Google", "Chrome"), ("Microsoft", "Edge"), ("BraveSoftware", "Brave-Browser") })
            foreach (var profile in Profiles(Path.Combine(Local, vendor, product, "User Data")))
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                    chromiumCaches.Add(Path.Combine(profile, sub));
        foreach (var opera in new[] { "Opera Stable", "Opera GX Stable" })
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                chromiumCaches.Add(Path.Combine(Local, "Opera Software", opera, sub));
        var firefox = Directory.Exists(Path.Combine(Local, "Mozilla", "Firefox", "Profiles"))
            ? Directory.GetDirectories(Path.Combine(Local, "Mozilla", "Firefox", "Profiles")).Select(p => Path.Combine(p, "cache2")).ToList()
            : new List<string>();

        return new()
        {
            new("temp-usuario", "Temporários do usuário", "Arquivos que programas e instaladores deixaram em %TEMP%.",
                new[] { Path.GetTempPath() }, TimeSpan.FromDays(1), PreSelect: true),
            new("temp-windows", "Temporários do Windows", "A pasta Temp do próprio Windows.",
                new[] { Path.Combine(Windows, "Temp") }, TimeSpan.FromDays(1), PreSelect: true, NeedsAdmin: true),
            new("cache-navegadores", "Cache dos navegadores",
                "Imagens e scripts guardados por Chrome, Edge, Brave, Opera e Firefox. Não mexe em senhas, cookies, histórico nem favoritos.",
                chromiumCaches.Concat(firefox).ToList(), TimeSpan.FromHours(1), PreSelect: true),
            new("cache-shaders", "Cache de shaders", "Shaders compilados do DirectX e da NVIDIA. Os jogos recriam na primeira abertura.",
                new[] { Path.Combine(Local, "D3DSCache"), Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache") },
                TimeSpan.FromDays(1), PreSelect: false),
            new("miniaturas", "Miniaturas do Explorador", "O Windows recria as miniaturas das pastas quando precisar.",
                new[] { Path.Combine(Local, "Microsoft", "Windows", "Explorer") }, TimeSpan.FromDays(1), PreSelect: false,
                Patterns: new[] { "thumbcache_*.db", "iconcache_*.db" }),
            new("relatorios-erro", "Relatórios de erro e despejos", "Relatórios de falha do Windows e despejos de memória de programas.",
                new[] { Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                        Path.Combine(Local, "CrashDumps"), Path.Combine(Windows, "Minidump") },
                TimeSpan.FromDays(1), PreSelect: true, NeedsAdmin: true),
        };
    }

    private static IEnumerable<string> Profiles(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var d in Directory.GetDirectories(userData))
        {
            string name = Path.GetFileName(d);
            if (name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal)) yield return d;
        }
    }

    /// <summary>Varre uma categoria. Não apaga nada.</summary>
    public static CleanupFinding Scan(CleanupCategory cat, DateTime now, CancellationToken ct = default)
    {
        long bytes = 0;
        var paths = new List<string>();
        foreach (var folder in cat.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var f in SafeFiles(folder, cat.Patterns, ct))
            {
                try
                {
                    var info = new FileInfo(f);
                    if (now - info.LastWriteTime < cat.MinAge) continue;
                    bytes += info.Length;
                    paths.Add(f);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return new CleanupFinding(cat, bytes, paths.Count, paths);
    }

    /// <summary>Apaga os arquivos encontrados. Em uso ou sem permissão: pula e conta.</summary>
    public static CleanupOutcome Clean(IEnumerable<CleanupFinding> findings, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var all = findings.SelectMany(f => f.Paths).ToList();
        long freed = 0;
        int deleted = 0, skipped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(all[i]);
                if (!info.Exists) continue;
                if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
                long len = info.Length;
                info.Delete();
                freed += len;
                deleted++;
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
            if (i % 200 == 0) progress?.Report(all.Count == 0 ? 1 : (double)i / all.Count);
        }
        progress?.Report(1);
        return new CleanupOutcome(freed, deleted, skipped);
    }

    /// <summary>Arquivos da pasta e subpastas, sem atravessar junctions/symlinks (que podem apontar para fora do cache).</summary>
    internal static IEnumerable<string> SafeFiles(string root, IReadOnlyList<string>? patterns, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try
            {
                files = patterns == null ? Directory.GetFiles(dir)
                      : patterns.SelectMany(p => Directory.GetFiles(dir, p)).Distinct().ToArray();
                dirs = patterns == null ? Directory.GetDirectories(dir) : Array.Empty<string>(); // padrões: só a pasta indicada
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                try { if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                stack.Push(d);
            }
        }
    }

    // ---------------- Lixeira (Shell32) ----------------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    /// <summary>Tamanho e itens na Lixeira de todas as unidades.</summary>
    public static (long Bytes, long Items) RecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Esvazia a Lixeira (a tela confirma antes: não tem volta).</summary>
    public static bool EmptyRecycleBin(IntPtr owner)
        => SHEmptyRecycleBin(owner, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) is 0 or unchecked((int)0x8000FFFF); // E_UNEXPECTED = já vazia
}
