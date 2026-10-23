using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>Um tipo de lixo da aba Limpeza (temporários, cache do Windows Update, lixeira...).</summary>
public sealed class CleanupItem : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool PreSelect { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>Pastas cujo conteúdo é apagado (a pasta em si fica).</summary>
    public Func<IEnumerable<string>> Folders { get; init; } = () => Array.Empty<string>();
    /// <summary>Filtro de nome de arquivo (ex.: "thumbcache_*.db"); null = tudo.</summary>
    public string? Pattern { get; init; }
    /// <summary>Não apaga arquivos alterados há menos que isso (instaladores e programas abertos usam o %TEMP%).</summary>
    public TimeSpan MinAge { get; init; }
    /// <summary>Serviços parados durante a limpeza e religados depois (ex.: Windows Update).</summary>
    public string[] StopServices { get; init; } = Array.Empty<string>();
    /// <summary>Tratamento especial (lixeira): mede e limpa por conta própria.</summary>
    public Func<long>? CustomSize { get; init; }
    public Func<long>? CustomClean { get; init; }

    private bool _selected;
    private long _size = -1;
    private string _status = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public long Size
    {
        get => _size;
        set { Set(ref _size, value); OnChanged(nameof(SizeText)); }
    }
    public string SizeText => _size < 0 ? "—" : CleanupManager.FormatBytes(_size);
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// Limpeza de disco: mede e apaga arquivos descartáveis. Arquivos em uso são pulados, atalhos/junções nunca são seguidos
/// (só apaga o que está fisicamente dentro da pasta) e nada fora das pastas listadas é tocado.
/// </summary>
public static class CleanupManager
{
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<CleanupItem> Catalog() => new()
    {
        new()
        {
            Name = "Temporários dos usuários (%TEMP%)",
            Description = "Pasta Temp de todos os usuários do PC. Pula arquivos usados nas últimas 2 horas e os que estão abertos.",
            PreSelect = true, MinAge = TimeSpan.FromHours(2),
            Folders = () => UserFolders(@"AppData\Local\Temp").Append(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)),
        },
        new()
        {
            Name = "Temporários do Windows",
            Description = @"C:\Windows\Temp: sobras de instalações e atualizações.",
            PreSelect = true, NeedsAdmin = true, MinAge = TimeSpan.FromHours(2),
            Folders = () => new[] { Path.Combine(Win, "Temp") },
        },
        new()
        {
            Name = "Cache do Windows Update",
            Description = "Atualizações já baixadas e instaladas. O serviço do Windows Update é parado durante a limpeza e religado depois.",
            PreSelect = true, NeedsAdmin = true,
            StopServices = new[] { "wuauserv", "bits" },
            Folders = () => new[] { Path.Combine(Win, @"SoftwareDistribution\Download") },
        },
        new()
        {
            Name = "Otimização de Entrega",
            Description = "Cópias de atualizações que o Windows compartilha com outros PCs da rede.",
            PreSelect = true, NeedsAdmin = true,
            StopServices = new[] { "dosvc" },
            Folders = () => new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
        },
        new()
        {
            Name = "Lixeira",
            Description = "Esvazia a lixeira de todas as unidades. Não dá para desfazer.",
            PreSelect = false,
            CustomSize = RecycleBinSize,
            CustomClean = EmptyRecycleBin,
        },
        new()
        {
            Name = "Relatórios de erro do Windows",
            Description = "Relatórios de travamento de programas já enviados ou na fila (WER).",
            PreSelect = true,
            Folders = () => new[]
            {
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\Temp"),
            }.Concat(UserFolders(@"AppData\Local\Microsoft\Windows\WER")),
        },
        new()
        {
            Name = "Cache de miniaturas",
            Description = "Miniaturas de fotos e vídeos do Explorer. São recriadas sozinhas; as que estão em uso ficam.",
            PreSelect = true, Pattern = "thumbcache_*.db",
            Folders = () => UserFolders(@"AppData\Local\Microsoft\Windows\Explorer"),
        },
        new()
        {
            Name = "Logs de instalação e do Windows",
            Description = @"Logs antigos (mais de 7 dias) em C:\Windows\Logs e do instalador de componentes (CBS/DISM).",
            PreSelect = false, NeedsAdmin = true, MinAge = TimeSpan.FromDays(7),
            Folders = () => new[] { Path.Combine(Win, "Logs") },
        },
        new()
        {
            Name = "Despejos de memória (tela azul)",
            Description = "Minidumps e MEMORY.DMP. Desmarcado: guarde se ainda for analisar uma tela azul (ex.: BlueScreenView, WinDbg).",
            PreSelect = false, NeedsAdmin = true,
            Folders = () => new[] { Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports") }
                            .Concat(UserFolders(@"AppData\Local\CrashDumps")),
            CustomSize = () => FolderSizeSafe(Path.Combine(Win, "Minidump")) + FolderSizeSafe(Path.Combine(Win, "LiveKernelReports"))
                               + UserFolders(@"AppData\Local\CrashDumps").Sum(FolderSizeSafe) + FileSize(Path.Combine(Win, "MEMORY.DMP")),
            CustomClean = () =>
            {
                long n = 0;
                foreach (var f in new[] { Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports") }
                                  .Concat(UserFolders(@"AppData\Local\CrashDumps")))
                    n += CleanFolder(f, null, TimeSpan.Zero);
                string mem = Path.Combine(Win, "MEMORY.DMP");
                long s = FileSize(mem);
                try { if (s > 0) { File.Delete(mem); n += s; } } catch { }
                return n;
            },
        },
        new()
        {
            Name = "Cache dos navegadores",
            Description = "Chrome, Edge, Brave, Opera e Firefox (só o cache: senhas, histórico e logins ficam). Feche os navegadores antes.",
            PreSelect = false,
            Folders = BrowserCaches,
        },
        new()
        {
            Name = "Cache de shaders (DirectX, NVIDIA, AMD)",
            Description = "Desmarcado: os jogos recriam os shaders e podem engasgar na primeira vez. Útil depois de trocar driver de vídeo.",
            PreSelect = false,
            Folders = () => UserFolders(@"AppData\Local\D3DSCache")
                .Concat(UserFolders(@"AppData\Local\NVIDIA\DXCache"))
                .Concat(UserFolders(@"AppData\Local\NVIDIA\GLCache"))
                .Concat(UserFolders(@"AppData\Local\AMD\DxCache"))
                .Concat(UserFolders(@"AppData\Local\AMD\GLCache")),
        },
    };

    /// <summary>Mede o tamanho do que seria apagado.</summary>
    public static long Measure(CleanupItem item)
    {
        if (item.CustomSize != null) return item.CustomSize();
        long total = 0;
        DateTime limit = DateTime.Now - item.MinAge;
        foreach (var dir in item.Folders().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Files(dir, item.Pattern))
                try { if (f.LastWriteTime <= limit) total += f.Length; } catch { }
        return total;
    }

    /// <summary>Apaga e devolve quantos bytes foram liberados.</summary>
    public static long Clean(CleanupItem item)
    {
        var stopped = new List<string>();
        try
        {
            foreach (var name in item.StopServices)
                if (Service("query", name).Contains("RUNNING"))
                {
                    Service("stop", name);
                    stopped.Add(name);
                }

            if (item.CustomClean != null) return item.CustomClean();
            long freed = 0;
            foreach (var dir in item.Folders().Distinct(StringComparer.OrdinalIgnoreCase))
                freed += CleanFolder(dir, item.Pattern, item.MinAge);
            return freed;
        }
        finally
        {
            foreach (var name in stopped) Service("start", name);
        }
    }

    /// <summary>net start/stop (espera o serviço terminar de parar ou iniciar) e sc query.</summary>
    private static string Service(string verb, string name)
    {
        try
        {
            var psi = verb == "query"
                ? new ProcessStartInfo("sc.exe", $"query {name}")
                : new ProcessStartInfo("net.exe", $"{verb} {name} /y");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return o;
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ arquivos

    /// <summary>Arquivos da pasta e subpastas, sem entrar em junções/links simbólicos.</summary>
    private static IEnumerable<FileInfo> Files(string dir, string? pattern)
    {
        if (!Directory.Exists(dir)) yield break;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(dir));
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = d.GetFileSystemInfos(); } catch { continue; }
            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (e is DirectoryInfo sub) stack.Push(sub);
                else if (e is FileInfo f && (pattern == null || FileSystemName.MatchesSimpleExpression(pattern, f.Name, ignoreCase: true)))
                    yield return f;
            }
        }
    }

    private static long CleanFolder(string dir, string? pattern, TimeSpan minAge)
    {
        if (!Directory.Exists(dir)) return 0;
        long freed = 0;
        DateTime limit = DateTime.Now - minAge;
        foreach (var f in Files(dir, pattern).ToList())
        {
            try
            {
                if (f.LastWriteTime > limit) continue;
                long len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
            }
            catch { } // em uso ou sem permissão: fica para a próxima
        }
        if (pattern == null) RemoveEmptyDirs(new DirectoryInfo(dir), isRoot: true, limit);
        return freed;
    }

    private static void RemoveEmptyDirs(DirectoryInfo d, bool isRoot, DateTime limit)
    {
        DirectoryInfo[] subs;
        try { subs = d.GetDirectories(); } catch { return; }
        foreach (var s in subs)
            if (!s.Attributes.HasFlag(FileAttributes.ReparsePoint)) RemoveEmptyDirs(s, false, limit);
        if (isRoot) return;
        try
        {
            if (d.LastWriteTime <= limit && !d.EnumerateFileSystemInfos().Any()) d.Delete();
        }
        catch { }
    }

    private static long FolderSizeSafe(string dir)
    {
        long n = 0;
        foreach (var f in Files(dir, null)) try { n += f.Length; } catch { }
        return n;
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    /// <summary>Mesma subpasta em cada perfil de usuário (C:\Users\*\...). Sem administrador, só o próprio perfil abre.</summary>
    private static IEnumerable<string> UserFolders(string relative)
    {
        string? users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var list = new List<string>();
        if (users != null)
        {
            try
            {
                foreach (var p in new DirectoryInfo(users).GetDirectories())
                {
                    if (p.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // "All Users", "Default User"
                    string path = Path.Combine(p.FullName, relative);
                    if (Directory.Exists(path)) list.Add(path);
                }
            }
            catch { }
        }
        string own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative);
        if (!list.Contains(own, StringComparer.OrdinalIgnoreCase) && Directory.Exists(own)) list.Add(own);
        return list;
    }

    /// <summary>Pastas de cache dos navegadores Chromium (todos os perfis) e Firefox.</summary>
    private static IEnumerable<string> BrowserCaches()
    {
        string[] chromium =
        {
            @"AppData\Local\Google\Chrome\User Data",
            @"AppData\Local\Microsoft\Edge\User Data",
            @"AppData\Local\BraveSoftware\Brave-Browser\User Data",
            @"AppData\Local\Vivaldi\User Data",
        };
        foreach (var rel in chromium)
            foreach (var root in UserFolders(rel))
            {
                string[] profiles;
                try { profiles = Directory.GetDirectories(root); } catch { continue; }
                foreach (var prof in profiles)
                    foreach (var c in new[] { @"Cache", @"Code Cache", @"GPUCache", @"Service Worker\CacheStorage" })
                    {
                        string p = Path.Combine(prof, c);
                        if (Directory.Exists(p)) yield return p;
                    }
            }

        foreach (var root in UserFolders(@"AppData\Local\Opera Software"))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var d in dirs)
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                    if (Directory.Exists(Path.Combine(d, c))) yield return Path.Combine(d, c);
        }

        foreach (var root in UserFolders(@"AppData\Local\Mozilla\Firefox\Profiles"))
        {
            string[] profiles;
            try { profiles = Directory.GetDirectories(root); } catch { continue; }
            foreach (var prof in profiles)
                if (Directory.Exists(Path.Combine(prof, "cache2"))) yield return Path.Combine(prof, "cache2");
        }
    }

    // ------------------------------------------------------------------ lixeira

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    private static long EmptyRecycleBin()
    {
        long size = RecycleBinSize();
        const uint NoConfirmation = 0x1, NoProgressUI = 0x2, NoSound = 0x4;
        SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUI | NoSound);
        return size - RecycleBinSize();
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}
