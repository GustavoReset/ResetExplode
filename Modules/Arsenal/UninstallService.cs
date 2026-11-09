using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FortKnox;

/// <summary>Programa instalado (entrada de "Aplicativos instalados" do Windows).</summary>
public sealed record InstalledProgram(
    string Name, string Publisher, string Version, DateTime? InstalledOn, long SizeBytes,
    string InstallLocation, string UninstallCommand, bool Machine, string RegistryKey)
{
    public string SizeText => SizeBytes <= 0 ? "—" : SizeBytes >= 1L << 30 ? $"{SizeBytes / 1073741824.0:0.0} GB" : $"{SizeBytes / 1048576.0:0} MB";
    public string DateText => InstalledOn is DateTime d ? d.ToString("dd/MM/yyyy") : "—";
}

public enum LeftoverKind { Folder, RegistryKey }

/// <summary>Um resto encontrado depois da desinstalação. Reason diz por que ele foi associado ao programa.</summary>
public sealed record Leftover(LeftoverKind Kind, string Path, long Bytes, string Reason, bool Recommended);

/// <summary>
/// ARSENAL · Desinstalar (Fase 3.4c). Sem WPF (testável):
/// 1) lista os programas pelo registro (mesma fonte de "Aplicativos instalados");
/// 2) roda o desinstalador OFICIAL do programa (nunca apaga um programa "na marra");
/// 3) depois, procura restos com regras conservadoras: pasta de instalação que sobrou e pastas/chaves com o NOME EXATO
///    do programa ou "Fabricante\Programa" em locais conhecidos. Nada de busca por palavra solta, nada em pastas do sistema,
///    e nada que pertença a outro programa ainda instalado.
/// Remover restos: pastas vão para a Lixeira; chaves do registro são exportadas para um .reg antes de sair.
/// </summary>
public static class UninstallService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallPath);
                if (key == null) return;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    if (k.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;          // componentes escondidos pelo próprio Windows
                    if (k.GetValue("ParentKeyName") is string) continue;                        // atualizações de outro programa
                    string uninstall = k.GetValue("UninstallString") as string ?? "";
                    if (uninstall.Length == 0) continue;
                    DateTime? date = k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
                    long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                    list.Add(new InstalledProgram(name.Trim(), (k.GetValue("Publisher") as string ?? "").Trim(),
                        (k.GetValue("DisplayVersion") as string ?? "").Trim(), date, size,
                        (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'), uninstall,
                        hive == RegistryHive.LocalMachine, $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{UninstallPath}\{sub}"));
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64);
        return list.GroupBy(p => (p.Name, p.Version)).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Separa "executável" e "argumentos" de um UninstallString (com ou sem aspas).</summary>
    public static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var m = Regex.Match(command, @"^(.+?\.exe)(\s+.*)?$", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.Trim()) : (command, "");
    }

    /// <summary>Abre o desinstalador oficial e espera ele terminar (a pessoa responde às perguntas dele).</summary>
    public static async Task<int?> RunUninstallerAsync(InstalledProgram p, CancellationToken ct)
    {
        var (file, args) = SplitCommand(p.UninstallCommand);
        // MsiExec /I{GUID} abre o reparo: o pedido aqui é desinstalar
        if (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
            args = Regex.Replace(args, @"(?i)/I(\{)", "/X$1");
        using var proc = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        if (proc == null) return null;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>O programa ainda aparece na lista do Windows? (o desinstalador pode ter sido cancelado)</summary>
    public static bool StillInstalled(InstalledProgram p) => List().Any(x => x.RegistryKey == p.RegistryKey || (x.Name == p.Name && x.Version == p.Version));

    // ---------------- restos ----------------

    /// <summary>Raízes onde programas costumam deixar pastas (injetáveis nos testes).</summary>
    public sealed record SearchRoots(IReadOnlyList<string> Folders)
    {
        public static SearchRoots Default() => new(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        }.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Normaliza nomes para comparar: sem versão, sem "(x64)", sem pontuação, minúsculas. "Foo App 2.3 (64-bit)" → "foo app".</summary>
    public static string Normalize(string s)
    {
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"\bv?\d+(\.\d+)+\b", " ");
        s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit|amd64)\b", " ", RegexOptions.IgnoreCase);
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static readonly string[] Protected =
    {
        "microsoft", "windows", "windowsapps", "common files", "nvidia corporation", "intel", "amd", "package cache",
        "installshield installation information", "internet explorer", "packages", "temp", "programs",
    };

    /// <summary>
    /// Procura pastas que sobraram. <paramref name="others"/> = programas ainda instalados (para não tocar no que é deles).
    /// </summary>
    public static List<Leftover> FindFolderLeftovers(InstalledProgram p, SearchRoots roots, IEnumerable<InstalledProgram> others)
    {
        var result = new List<Leftover>();
        string name = Normalize(p.Name), publisher = Normalize(p.Publisher);
        if (name.Length < 3) return result; // nome curto demais para comparar com segurança
        var otherLocations = others.Where(o => o.InstallLocation.Length > 0)
                                   .Select(o => Path.GetFullPath(o.InstallLocation).TrimEnd('\\')).ToList();
        var otherNames = others.Select(o => Normalize(o.Name)).ToHashSet();

        bool BelongsToOther(string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            return otherLocations.Any(l => l.Equals(full, StringComparison.OrdinalIgnoreCase)
                                        || l.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)
                                        || full.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase));
        }
        void Add(string dir, string reason, bool recommended)
        {
            if (!Directory.Exists(dir) || BelongsToOther(dir)) return;
            if (Protected.Contains(Path.GetFileName(dir.TrimEnd('\\')).ToLowerInvariant())) return;
            if (roots.Folders.Any(r => Path.GetFullPath(r).TrimEnd('\\').Equals(Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            if (result.Any(x => x.Path.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
            result.Add(new Leftover(LeftoverKind.Folder, dir, FolderSize(dir), reason, recommended));
        }

        if (p.InstallLocation.Length > 0) Add(p.InstallLocation, "Pasta de instalação que o desinstalador deixou.", true);

        foreach (var root in roots.Folders.Where(Directory.Exists))
        {
            foreach (var dir in SafeDirs(root))
            {
                string n = Normalize(Path.GetFileName(dir));
                if (n == name && !otherNames.Contains(n)) Add(dir, $"Pasta com o nome do programa em {Path.GetFileName(root)}.", true);
                else if (publisher.Length >= 3 && n == publisher)
                    foreach (var sub in SafeDirs(dir))
                        if (Normalize(Path.GetFileName(sub)) == name)
                            Add(sub, $"Pasta \"{Path.GetFileName(dir)}\\{Path.GetFileName(sub)}\" do fabricante.", true);
            }
        }
        return result;
    }

    /// <summary>Chaves HKCU/HKLM\Software\Fabricante\Programa (e Software\Programa) que sobraram.</summary>
    public static List<Leftover> FindRegistryLeftovers(InstalledProgram p, IEnumerable<InstalledProgram> others)
    {
        var result = new List<Leftover>();
        string name = Normalize(p.Name), publisher = Normalize(p.Publisher);
        if (name.Length < 3) return result;
        var otherNames = others.Select(o => Normalize(o.Name)).ToHashSet();
        if (otherNames.Contains(name)) return result; // outra versão ainda instalada usa as mesmas chaves
        foreach (var (hive, label) in new[] { (RegistryHive.CurrentUser, "HKCU"), (RegistryHive.LocalMachine, "HKLM") })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var software = root.OpenSubKey("Software");
                    if (software == null) continue;
                    foreach (var top in software.GetSubKeyNames())
                    {
                        string n = Normalize(top);
                        if (n == name)
                            AddKey(result, $@"{label}\Software\{top}", "Chave com o nome do programa.", view);
                        else if (publisher.Length >= 3 && n == publisher)
                        {
                            using var pub = software.OpenSubKey(top);
                            foreach (var sub in pub?.GetSubKeyNames() ?? Array.Empty<string>())
                                if (Normalize(sub) == name)
                                    AddKey(result, $@"{label}\Software\{top}\{sub}", "Chave do fabricante com o nome do programa.", view);
                        }
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        return result;
    }

    private static void AddKey(List<Leftover> list, string path, string reason, RegistryView view)
    {
        if (view == RegistryView.Registry32) path = path.Replace(@"\Software\", @"\Software\WOW6432Node\");
        if (list.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        if (path.Contains(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Classes", StringComparison.OrdinalIgnoreCase)) return;
        list.Add(new Leftover(LeftoverKind.RegistryKey, path, 0, reason, true));
    }

    /// <summary>Remove os restos escolhidos: pastas para a Lixeira; chaves exportadas para .reg em <paramref name="backupFolder"/> e apagadas.</summary>
    public static (int Removed, List<string> Failed, string? Backup) RemoveLeftovers(IEnumerable<Leftover> items, string backupFolder)
    {
        int removed = 0;
        var failed = new List<string>();
        string? backup = null;
        foreach (var l in items)
        {
            try
            {
                if (l.Kind == LeftoverKind.Folder)
                {
                    if (!CleanupService.SendToRecycleBin(l.Path)) { failed.Add($"{l.Path} (o Windows não moveu para a Lixeira)"); continue; }
                }
                else
                {
                    Directory.CreateDirectory(backupFolder);
                    backup ??= Path.Combine(backupFolder, $"restos-{DateTime.Now:yyyyMMdd-HHmmss}");
                    Directory.CreateDirectory(backup);
                    string file = Path.Combine(backup, Regex.Replace(l.Path, @"[\\/:*?""<>|]", "_") + ".reg");
                    var export = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{l.Path}\" \"{file}\" /y")
                    { CreateNoWindow = true, UseShellExecute = false });
                    export?.WaitForExit(15000);
                    if (export == null || export.ExitCode != 0 || !File.Exists(file)) { failed.Add($"{l.Path} (sem cópia, não apagada)"); continue; }
                    DeleteKey(l.Path);
                }
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
            {
                failed.Add($"{l.Path} ({ex.Message})");
            }
        }
        return (removed, failed, backup);
    }

    private static void DeleteKey(string path)
    {
        var hive = path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        bool wow = path.Contains(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase);
        string sub = path[(path.IndexOf('\\') + 1)..].Replace(@"WOW6432Node\", "", StringComparison.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(hive, wow ? RegistryView.Registry32 : RegistryView.Registry64);
        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try
        {
            return Directory.GetDirectories(root).Where(d =>
            {
                try { return !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint); }
                catch (IOException) { return false; }
            }).ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static long FolderSize(string dir)
    {
        long total = 0;
        foreach (var f in CleanupService.SafeFiles(dir, null, CancellationToken.None))
            try { total += new FileInfo(f).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return total;
    }
}
