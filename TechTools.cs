using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Atalho para uma ferramenta do Windows (aba Técnico).</summary>
public sealed record WinTool(string Name, string Exe, string Args = "");

/// <summary>
/// Ferramentas do dia a dia de técnico de TI: reparo do Windows (SFC/DISM/CHKDSK), rede, fila de impressão,
/// Windows Update, licença e lista de programas. Os reparos longos abrem num console visível para acompanhar o progresso.
/// </summary>
public static class TechTools
{
    public static IReadOnlyList<WinTool> Shortcuts { get; } = new WinTool[]
    {
        new("Gerenciador de Dispositivos", "devmgmt.msc"),
        new("Gerenciamento do Computador", "compmgmt.msc"),
        new("Gerenciamento de Disco", "diskmgmt.msc"),
        new("Serviços", "services.msc"),
        new("Visualizador de Eventos", "eventvwr.msc"),
        new("Agendador de Tarefas", "taskschd.msc"),
        new("Editor do Registro", "regedit.exe"),
        new("Configuração do Sistema", "msconfig.exe"),
        new("Informações do Sistema", "msinfo32.exe"),
        new("Monitor de Recursos", "resmon.exe"),
        new("Gerenciador de Tarefas", "taskmgr.exe"),
        new("Programas e Recursos", "appwiz.cpl"),
        new("Conexões de Rede", "ncpa.cpl"),
        new("Opções de Energia", "powercfg.cpl"),
        new("Propriedades do Sistema", "sysdm.cpl"),
        new("Usuários e Senhas", "netplwiz.exe"),
        new("Política de Grupo (Pro)", "gpedit.msc"),
        new("Impressoras", "control.exe", "printers"),
        new("Painel de Controle", "control.exe"),
        new("Limpeza de Disco do Windows", "cleanmgr.exe"),
        new("Prompt de Comando (admin)", "cmd.exe"),
        new("PowerShell (admin)", "powershell.exe"),
    };

    public static void Open(WinTool t)
        => Process.Start(new ProcessStartInfo(t.Exe, t.Args) { UseShellExecute = true });

    /// <summary>Abre um console com o comando rodando (herda o administrador do app) e deixa a janela aberta no final.</summary>
    public static void RunConsole(string title, string command)
    {
        string script = $"title RESET EXPLODE - {title} & echo {title} & echo. & {command} & echo. & echo ===== Terminado. Pode fechar esta janela. =====";
        Process.Start(new ProcessStartInfo("cmd.exe", "/k " + script) { UseShellExecute = true });
    }

    /// <summary>Roda comandos sem janela e devolve a saída (em UTF-8, para os acentos saírem certos).</summary>
    public static async Task<(int Code, string Output)> RunHiddenAsync(string command)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c chcp 65001 >nul & {command}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o cmd.");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string text = (await o + await e).Replace("\r\n", "\n");
        // Tira linhas vazias repetidas (ipconfig e netsh têm muitas)
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return (p.ExitCode, text.Trim());
    }

    // ------------------------------------------------------------------ ações rápidas

    public static Task<(int, string)> FlushDnsAsync() => RunHiddenAsync("ipconfig /flushdns");

    public static Task<(int, string)> RenewIpAsync() => RunHiddenAsync("ipconfig /release & ipconfig /renew & ipconfig /flushdns");

    /// <summary>Winsock + pilha TCP/IP + DNS. Precisa reiniciar o PC.</summary>
    public static Task<(int, string)> ResetNetworkAsync()
        => RunHiddenAsync("netsh winsock reset & netsh int ip reset & netsh int ipv6 reset & ipconfig /flushdns");

    /// <summary>Destrava a fila de impressão: para o spooler, apaga os trabalhos presos e liga de novo.</summary>
    public static Task<(int, string)> ClearPrintQueueAsync()
    {
        string spool = Path.Combine(Environment.SystemDirectory, @"spool\PRINTERS");
        return RunHiddenAsync($"net stop spooler /y & del /f /q \"{spool}\\*.*\" & net start spooler & echo Fila de impressão limpa.");
    }

    /// <summary>
    /// Reset clássico do Windows Update: para os serviços, renomeia SoftwareDistribution e catroot2 para .old
    /// (dá para voltar renomeando de novo) e religa os serviços.
    /// </summary>
    public static Task<(int, string)> ResetWindowsUpdateAsync()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        string sd = Path.Combine(win, "SoftwareDistribution"), cr = Path.Combine(Environment.SystemDirectory, "catroot2");
        return RunHiddenAsync(
            "net stop wuauserv /y & net stop bits /y & net stop cryptsvc /y & net stop dosvc /y & " +
            $"ren \"{sd}\" SoftwareDistribution.old-{stamp} & ren \"{cr}\" catroot2.old-{stamp} & " +
            "net start cryptsvc & net start bits & net start wuauserv & net start dosvc & " +
            $"echo. & echo Pastas antigas guardadas como SoftwareDistribution.old-{stamp} e catroot2.old-{stamp}.");
    }

    public static async Task<string> CreateRestorePointAsync()
    {
        var (code, output) = await RunHiddenAsync(
            "powershell -NoProfile -NonInteractive -Command \"Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue; " +
            "Checkpoint-Computer -Description 'RESET EXPLODE - Técnico' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop\"");
        return code == 0 ? "Ponto de restauração criado."
             : "Não foi possível criar o ponto de restauração (o Windows só permite um a cada 24 h, ou a proteção do sistema está desligada).\n" + output;
    }

    // ------------------------------------------------------------------ informações

    /// <summary>Edição do Windows, estado da ativação e a chave OEM gravada na BIOS (notebooks e PCs de marca).</summary>
    public static string WindowsLicense()
    {
        var sb = new StringBuilder();
        try
        {
            using var os = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject o in os.Get())
            {
                sb.AppendLine($"Sistema: {o["Caption"]} (versão {o["Version"]}, build {o["BuildNumber"]})");
                if (o["InstallDate"] is string d && d.Length >= 8)
                    sb.AppendLine($"Instalado em: {d[6..8]}/{d[4..6]}/{d[..4]}");
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT OA3xOriginalProductKey, OA3xOriginalProductKeyDescription FROM SoftwareLicensingService");
            foreach (ManagementObject o in s.Get())
            {
                string key = o["OA3xOriginalProductKey"] as string ?? "";
                sb.AppendLine(key.Length > 0
                    ? $"Chave OEM na BIOS: {key} ({o["OA3xOriginalProductKeyDescription"]})"
                    : "Chave OEM na BIOS: nenhuma (PC montado ou licença digital/varejo).");
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, LicenseStatus, PartialProductKey, Description FROM SoftwareLicensingProduct " +
                "WHERE PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'");
            foreach (ManagementObject o in s.Get())
            {
                int st = Convert.ToInt32(o["LicenseStatus"]);
                string state = st switch
                {
                    1 => "ATIVADO",
                    0 => "não licenciado",
                    2 or 3 => "em período de carência",
                    4 => "não original",
                    5 => "em notificação (precisa ativar)",
                    6 => "carência estendida",
                    _ => $"desconhecido ({st})",
                };
                sb.AppendLine($"Ativação: {state} · final da chave instalada: {o["PartialProductKey"]}");
                sb.AppendLine($"Canal: {o["Description"]}");
            }
        }
        catch { }

        return sb.Length == 0 ? "Não foi possível ler a licença do Windows." : sb.ToString().TrimEnd();
    }

    public sealed record InstalledProgram(string Name, string Version, string Publisher, string InstallDate, string Size);

    /// <summary>Programas instalados (64 e 32 bits, máquina e usuário), sem atualizações e componentes de sistema.</summary>
    public static List<InstalledProgram> InstalledPrograms()
    {
        var list = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey hive, string path)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string dn || string.IsNullOrWhiteSpace(dn)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is string rt && rt.Contains("Update")) continue;
                    string size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? CleanupManager.FormatBytes(kb * 1024L) : "";
                    string date = k.GetValue("InstallDate") is string d && d.Length == 8 ? $"{d[6..8]}/{d[4..6]}/{d[..4]}" : "";
                    list[dn.Trim()] = new InstalledProgram(dn.Trim(), k.GetValue("DisplayVersion") as string ?? "",
                                                           k.GetValue("Publisher") as string ?? "", date, size);
                }
            }
            catch { }
        }
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, Uninstall);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, Uninstall);
        return list.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Grava a lista em CSV (abre no Excel) e devolve quantos programas foram exportados.</summary>
    public static int ExportInstalledPrograms(string path)
    {
        var progs = InstalledPrograms();
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("sep=;");
        sb.AppendLine("Programa;Versão;Fabricante;Instalado em;Tamanho");
        foreach (var p in progs)
            sb.AppendLine(string.Join(";", Q(p.Name), Q(p.Version), Q(p.Publisher), Q(p.InstallDate), Q(p.Size)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return progs.Count;
    }
}
