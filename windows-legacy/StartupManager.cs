using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Programa que abre junto com o Windows (chave Run do registro ou atalho na pasta Inicializar).</summary>
public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";          // ex.: "Registro · usuário"
    public bool Machine { get; init; }                 // HKLM / pasta comum: precisa de administrador
    public bool Enabled { get; set; }
    public string Detail { get; init; } = "";          // descrição · fabricante
    public bool FileMissing { get; init; }

    internal string Kind { get; init; } = "";          // Run | Run32 | Folder
    internal string? FilePath { get; init; }           // só para Folder

    public string State => FileMissing ? "ARQUIVO NÃO EXISTE" : Enabled ? "ATIVADO" : "DESATIVADO";
}

/// <summary>Item removido, guardado para "Restaurar removidos".</summary>
public sealed class StartupBackup
{
    public string Kind { get; set; } = "";
    public bool Machine { get; set; }
    public string Name { get; set; } = "";
    public string? Command { get; set; }   // registro
    public string? Backup { get; set; }    // pasta: cópia do atalho em %AppData%\ResetExplode\Startup
    public string? Original { get; set; }  // pasta: caminho original
}

/// <summary>
/// Lista, ativa/desativa e remove programas de inicialização. Desativar usa as mesmas chaves StartupApproved do
/// Gerenciador de Tarefas (a entrada continua lá e pode ser religada); remover apaga a entrada, com cópia para desfazer.
/// </summary>
public static class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    public static string BackupFolder => Path.Combine(AppSettings.Folder, "Startup");

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, RegistryHive.CurrentUser, RegistryView.Default, "Run", false, "Registro · usuário");
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", true, "Registro · todos os usuários");
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", true, "Registro · todos os usuários (32 bits)");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), false, "Pasta Inicializar · usuário");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true, "Pasta Inicializar · todos os usuários");
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupEntry> list, RegistryHive hive, RegistryView view, string kind, bool machine, string source)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(RunPath);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string cmd = key.GetValue(name)?.ToString() ?? "";
                string? exe = ExeFromCommand(cmd);
                list.Add(new StartupEntry
                {
                    Name = name, Command = cmd, Source = source, Machine = machine, Kind = kind,
                    Enabled = IsApproved(machine, kind, name),
                    Detail = Describe(exe), FileMissing = exe != null && !File.Exists(exe),
                });
            }
        }
        catch { }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, bool machine, string source)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder))
            {
                string fn = Path.GetFileName(file);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string? target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : null;
                string? exe = target != null ? ExeFromCommand(target) : file;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(fn), Command = target ?? file, Source = source, Machine = machine,
                    Kind = "Folder", FilePath = file,
                    Enabled = IsApproved(machine, "StartupFolder", fn),
                    Detail = Describe(exe), FileMissing = target != null && exe != null && !File.Exists(exe),
                });
            }
        }
        catch { }
    }

    // ----- ativar / desativar (StartupApproved, como o Gerenciador de Tarefas) -----

    private static string ApprovedKind(StartupEntry e) => e.Kind == "Folder" ? "StartupFolder" : e.Kind;
    private static string ApprovedName(StartupEntry e) => e.Kind == "Folder" ? Path.GetFileName(e.FilePath!) : e.Name;

    private static bool IsApproved(bool machine, string kind, string name)
    {
        try
        {
            using var key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(ApprovedPath + kind);
            // 02/06 = ativado, 03/07 = desativado; sem valor = ativado
            return key?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        using var key = (e.Machine ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(ApprovedPath + ApprovedKind(e));
        key.SetValue(ApprovedName(e), data, RegistryValueKind.Binary);
        e.Enabled = enabled;
    }

    // ----- remover / restaurar -----

    public static StartupBackup Remove(StartupEntry e)
    {
        StartupBackup b;
        if (e.Kind == "Folder")
        {
            Directory.CreateDirectory(BackupFolder);
            string copy = Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(e.FilePath!)}");
            File.Move(e.FilePath!, copy);
            b = new StartupBackup { Kind = e.Kind, Machine = e.Machine, Name = e.Name, Backup = copy, Original = e.FilePath };
        }
        else
        {
            using var key = RegistryKey.OpenBaseKey(e.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                                                    e.Kind == "Run32" ? RegistryView.Registry32 : e.Machine ? RegistryView.Registry64 : RegistryView.Default)
                                       .OpenSubKey(RunPath, writable: true)
                            ?? throw new InvalidOperationException("Chave Run não encontrada.");
            key.DeleteValue(e.Name);
            b = new StartupBackup { Kind = e.Kind, Machine = e.Machine, Name = e.Name, Command = e.Command };
        }
        try
        {
            using var ap = (e.Machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(ApprovedPath + ApprovedKind(e), writable: true);
            ap?.DeleteValue(ApprovedName(e), throwOnMissingValue: false);
        }
        catch { }
        return b;
    }

    public static void Restore(StartupBackup b)
    {
        if (b.Kind == "Folder")
        {
            if (b.Backup == null || b.Original == null || !File.Exists(b.Backup)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(b.Original)!);
            File.Move(b.Backup, b.Original, overwrite: true);
            return;
        }
        using var key = RegistryKey.OpenBaseKey(b.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                                                b.Kind == "Run32" ? RegistryView.Registry32 : b.Machine ? RegistryView.Registry64 : RegistryView.Default)
                                   .CreateSubKey(RunPath);
        key.SetValue(b.Name, b.Command ?? "", RegistryValueKind.String);
    }

    // ----- utilidades -----

    /// <summary>Extrai o executável da linha de comando ("C:\x\a.exe" -min  →  C:\x\a.exe).</summary>
    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.Length == 0) return null;
        string path;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end <= 1) return null;
            path = cmd[1..end];
        }
        else
        {
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int sp = cmd.IndexOf(' ');
            path = exe > 0 ? cmd[..(exe + 4)] : sp > 0 ? cmd[..sp] : cmd;
        }
        if (Path.IsPathRooted(path)) return path;
        // "rundll32.exe ..." e afins: procura no System32; se não achar, não dá para saber onde está
        string sys = Path.Combine(Environment.SystemDirectory, path);
        return File.Exists(sys) ? sys : null;
    }

    private static string Describe(string? exe)
    {
        try
        {
            if (exe == null || !File.Exists(exe)) return "";
            var v = FileVersionInfo.GetVersionInfo(exe);
            return string.Join(" · ", new[] { v.FileDescription, v.CompanyName }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct());
        }
        catch { return ""; }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            if (string.IsNullOrEmpty(target)) return null;
            return string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
        }
        catch { return null; }
    }
}
