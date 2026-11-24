using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>App do catálogo "Instalar apps" (pós-formatação), instalado pelo winget.</summary>
public sealed class InstallableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string WingetId { get; init; } = "";
    /// <summary>Começo do nome em "Aplicativos instalados" (Painel de Controle), para saber se já está instalado.</summary>
    public string DisplayPrefix { get; init; } = "";
    public bool PreSelect { get; init; } = true;

    private bool _selected, _installed;
    private string _status = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Installed
    {
        get => _installed;
        set { Set(ref _installed, value); OnChanged(nameof(CanSelect)); }
    }
    public bool CanSelect => !Installed;
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
/// Catálogo de apps essenciais e instalação silenciosa pelo winget (Gerenciador de Pacotes do Windows), que baixa
/// sempre a versão oficial mais recente do site do fabricante e confere o hash do instalador.
/// </summary>
public static class AppInstaller
{
    public static List<InstallableApp> Catalog() => new()
    {
        new() { Name = "Steam", Description = "Loja e biblioteca de jogos da Valve.", WingetId = "Valve.Steam", DisplayPrefix = "Steam" },
        new() { Name = "Google Chrome", Description = "Navegador do Google.", WingetId = "Google.Chrome", DisplayPrefix = "Google Chrome" },
        new() { Name = "Discord", Description = "Chat de voz e texto para jogar com os amigos.", WingetId = "Discord.Discord", DisplayPrefix = "Discord" },
        new() { Name = "Lightshot", Description = "Captura de tela rápida com a tecla Print Screen.", WingetId = "Skillbrains.Lightshot", DisplayPrefix = "Lightshot" },
        new() { Name = "7-Zip", Description = "Compactador gratuito (zip, rar, 7z). Abre e cria arquivos compactados.", WingetId = "7zip.7zip", DisplayPrefix = "7-Zip" },
        new() { Name = "WinRAR", Description = "Compactador clássico (pago após o teste). Escolha ele ou o 7-Zip.", WingetId = "RARLab.WinRAR", DisplayPrefix = "WinRAR", PreSelect = false },
        new() { Name = "VLC", Description = "Player de vídeo e música que abre qualquer formato.", WingetId = "VideoLAN.VLC", DisplayPrefix = "VLC media player" },
    };

    private static string AliasPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    /// <summary>
    /// Caminho completo do winget.exe: o alias do usuário, o PATH e, por último, a pasta do pacote "Instalador de
    /// Aplicativo" (útil quando o programa roda como administrador de outra conta, onde o alias não existe).
    /// </summary>
    public static string WingetPath
    {
        get
        {
            if (File.Exists(AliasPath)) return AliasPath;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try { var f = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(f)) return f; } catch { }
            }
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                var pkg = Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                                   .Select(d => Path.Combine(d, "winget.exe")).Where(File.Exists)
                                   .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (pkg != null) return pkg;
            }
            catch { }
            return AliasPath;
        }
    }

    public static bool WingetAvailable => File.Exists(WingetPath);

    /// <summary>
    /// Instala o winget (Instalador de Aplicativo) sem depender da Microsoft Store: primeiro registra o pacote que já
    /// vem no Windows e, se não existir, baixa o instalador oficial (aka.ms/getwinget) e suas dependências.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallWingetAsync(CancellationToken ct)
    {
        if (WingetAvailable) return (true, "O winget já está instalado.");
        const string script = @"
$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe } catch { }
if (-not (Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue | Where-Object { $_.InstallLocation -and (Test-Path (Join-Path $_.InstallLocation 'winget.exe')) })) {
    $tmp = Join-Path $env:TEMP 'reset-winget'; New-Item -ItemType Directory -Force $tmp | Out-Null
    $deps = @()
    foreach ($u in 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx',
                   'https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx') {
        $f = Join-Path $tmp ([IO.Path]::GetFileName($u)); Invoke-WebRequest -UseBasicParsing $u -OutFile $f; $deps += $f
    }
    $bundle = Join-Path $tmp 'winget.msixbundle'
    Invoke-WebRequest -UseBasicParsing 'https://aka.ms/getwinget' -OutFile $bundle
    Add-AppxPackage -Path $bundle -DependencyPath $deps
}
";
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        string output = await outTask + await errTask;

        if (WingetAvailable) return (true, "winget instalado.");
        return (false, $"Não foi possível instalar o winget. {LastLine(output)}".Trim());
    }

    /// <summary>Nomes em "Aplicativos instalados" (máquina 64/32 bits e usuário atual).</summary>
    public static HashSet<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && !string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch { }
        }
        return names;
    }

    public static void RefreshInstalled(IEnumerable<InstallableApp> apps)
    {
        var names = InstalledNames();
        foreach (var a in apps)
        {
            a.Installed = names.Any(n => n.StartsWith(a.DisplayPrefix, StringComparison.OrdinalIgnoreCase));
            if (a.Installed) a.Selected = false;
        }
    }

    /// <summary>Instala um app em silêncio. Retorna (ok, mensagem curta).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(InstallableApp app, CancellationToken ct, bool upgrade = false)
    {
        var psi = new ProcessStartInfo(WingetPath,
            $"{(upgrade ? "upgrade" : "install")} --id {app.WingetId} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        string output = await outTask + await errTask;

        return p.ExitCode switch
        {
            0 => (true, "Instalado."),
            unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) => (true, "Já estava instalado."),
            unchecked((int)0x8A150014) => (false, "Pacote não encontrado no winget."),
            unchecked((int)0x8A150008) => (false, "Falha no download: confira a internet e tente de novo."),
            _ => (false, $"Falhou (código 0x{p.ExitCode:X8}). {LastLine(output)}".Trim()),
        };
    }

    /// <summary>Última linha útil da saída do winget (sem as barras de progresso).</summary>
    private static string LastLine(string output) =>
        output.Split('\n', '\r').Select(l => l.Trim())
              .LastOrDefault(l => l.Length > 3 && !l.All(c => "-\\|/█▒ ".Contains(c)) && !l.Contains("KB /") && !l.Contains("MB /")) ?? "";
}
