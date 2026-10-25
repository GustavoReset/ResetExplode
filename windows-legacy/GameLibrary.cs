using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Jogo instalado (de qualquer loja) ou adicionado à mão.</summary>
public sealed class LibGame
{
    public string Store { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string InstallDir { get; init; } = "";
    public string? ExePath { get; set; }
    public string? LaunchUri { get; init; }
    public string Args { get; init; } = "";
    public string Key => Store + ":" + Id;
    public string Subtitle => Store;
    public bool IsSteam => Store == "Steam";
}

/// <summary>Procura jogos instalados: Steam, Epic Games, GOG, Ubisoft Connect e os adicionados manualmente. Só lê arquivos e registro.</summary>
public static class GameLibrary
{
    public static List<LibGame> Scan(IEnumerable<CustomGame> custom)
    {
        var all = new List<LibGame>();
        try { all.AddRange(Steam()); } catch { }
        try { all.AddRange(Epic()); } catch { }
        try { all.AddRange(Gog()); } catch { }
        try { all.AddRange(Ubisoft()); } catch { }
        foreach (var c in custom)
        {
            if (!File.Exists(c.Exe)) continue;
            all.Add(new LibGame
            {
                Store = "Manual", Id = c.Exe, Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileNameWithoutExtension(c.Exe) : c.Name,
                InstallDir = Path.GetDirectoryName(c.Exe) ?? "", ExePath = c.Exe, Args = c.Args,
            });
        }
        return all.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------------- Steam ----------------

    private static IEnumerable<LibGame> Steam()
    {
        string? steam = SteamInfo.SteamPath();
        if (steam == null) yield break;
        foreach (var lib in SteamInfo.LibraryFolders(steam))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                LibGame? g = null;
                try
                {
                    var st = Vdf.Parse(File.ReadAllText(acf)).Child("AppState");
                    if (st == null) continue;
                    string? id = st.Get("appid"), name = st.Get("name"), dir = st.Get("installdir");
                    if (id == null || name == null || dir == null) continue;
                    if (long.TryParse(st.Get("StateFlags"), out long flags) && (flags & 4) == 0) continue; // 4 = instalado por completo
                    if (id == "228980" || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                    string full = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(full)) continue;
                    g = new LibGame { Store = "Steam", Id = id, Name = name, InstallDir = full };
                }
                catch { }
                if (g != null) yield return g;
            }
        }
    }

    // ---------------- Epic ----------------

    private static IEnumerable<LibGame> Epic()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            LibGame? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? name = S("DisplayName"), loc = S("InstallLocation"), app = S("AppName"), ns = S("CatalogNamespace"), cat = S("CatalogItemId"), exe = S("LaunchExecutable");
                if (name == null || loc == null || app == null || !Directory.Exists(loc)) continue;
                string? exePath = !string.IsNullOrEmpty(exe) ? Path.Combine(loc, exe) : null;
                g = new LibGame
                {
                    Store = "Epic Games", Id = app, Name = name, InstallDir = loc,
                    ExePath = exePath != null && File.Exists(exePath) ? exePath : null,
                    LaunchUri = ns != null && cat != null ? $"com.epicgames.launcher://apps/{ns}%3A{cat}%3A{app}?action=launch&silent=true" : null,
                };
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    // ---------------- GOG ----------------

    private static IEnumerable<LibGame> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) yield break;
        foreach (var sub in root.GetSubKeyNames())
        {
            LibGame? g = null;
            try
            {
                using var k = root.OpenSubKey(sub);
                string? name = k?.GetValue("gameName") as string, path = k?.GetValue("path") as string, exe = k?.GetValue("exe") as string;
                if (name == null || path == null || !Directory.Exists(path)) continue;
                g = new LibGame
                {
                    Store = "GOG", Id = sub, Name = name, InstallDir = path,
                    ExePath = exe != null && File.Exists(exe) ? exe : null,
                    Args = k?.GetValue("launchParam") as string ?? "",
                };
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    // ---------------- Ubisoft Connect ----------------

    private static IEnumerable<LibGame> Ubisoft()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root == null) yield break;
        foreach (var sub in root.GetSubKeyNames())
        {
            LibGame? g = null;
            try
            {
                using var k = root.OpenSubKey(sub);
                string? dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
                if (dir == null || !Directory.Exists(dir)) continue;
                g = new LibGame { Store = "Ubisoft Connect", Id = sub, Name = Path.GetFileName(dir), InstallDir = dir, LaunchUri = $"uplay://launch/{sub}/0" };
            }
            catch { }
            if (g != null) yield return g;
        }
    }
}

/// <summary>Informações da instalação da Steam e edição das "Opções de inicialização" (localconfig.vdf).</summary>
public static class SteamInfo
{
    public static string? SteamPath()
    {
        try
        {
            string? p = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            p ??= Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
            if (p != null) p = Path.GetFullPath(p.Replace('/', '\\'));
            if (p != null && Directory.Exists(p)) return p;
        }
        catch { }
        string def = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(def) ? def : null;
    }

    public static List<string> LibraryFolders(string steam)
    {
        var list = new List<string> { steam };
        try
        {
            string f = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(f))
            {
                var root = Vdf.Parse(File.ReadAllText(f)).Child("libraryfolders");
                if (root != null)
                    foreach (var (_, c) in root.Children)
                    {
                        string? p = c.Get("path");
                        if (p != null && Directory.Exists(p.Replace('/', '\\')) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p.Replace('/', '\\'));
                    }
            }
        }
        catch { }
        return list;
    }

    public static bool IsRunning => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>localconfig.vdf mais recente (a conta que usou a Steam por último).</summary>
    private static string? LocalConfig()
    {
        string? steam = SteamPath();
        if (steam == null) return null;
        string ud = Path.Combine(steam, "userdata");
        if (!Directory.Exists(ud)) return null;
        return Directory.GetDirectories(ud).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static VdfNode? AppsNode(VdfNode root, bool create)
    {
        VdfNode? n = root.Child("UserLocalConfigStore");
        foreach (var key in new[] { "Software", "Valve", "Steam", "apps" })
        {
            if (n == null) return null;
            n = create ? n.GetOrAddChild(key) : n.Child(key);
        }
        return n;
    }

    public static string? GetLaunchOptions(string appId)
    {
        try
        {
            string? f = LocalConfig();
            if (f == null) return null;
            return AppsNode(Vdf.Parse(File.ReadAllText(f)), false)?.Child(appId)?.Get("LaunchOptions");
        }
        catch { return null; }
    }

    /// <summary>Grava as opções de inicialização do jogo. A Steam precisa estar fechada (senão ela sobrescreve ao sair). Devolve null se deu certo, ou o erro.</summary>
    public static string? SetLaunchOptions(string appId, string options)
    {
        if (IsRunning) return "A Steam está aberta. Feche-a para gravar as opções (ela sobrescreve o arquivo ao sair).";
        string? f = LocalConfig();
        if (f == null) return "Não achei o localconfig.vdf da Steam (entre na Steam ao menos uma vez neste PC).";
        try
        {
            var root = Vdf.Parse(File.ReadAllText(f));
            var apps = AppsNode(root, true)!;
            var app = apps.GetOrAddChild(appId);
            if (string.IsNullOrWhiteSpace(options)) app.Remove("LaunchOptions"); else app.Set("LaunchOptions", options.Trim());
            File.Copy(f, f + ".resetexplode.bak", true); // cópia de segurança do arquivo original
            File.WriteAllText(f, Vdf.Serialize(root), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) { return "Não consegui gravar: " + ex.Message; }
    }

    /// <summary>Pede à Steam para fechar (steam.exe -shutdown) e espera até 25 s.</summary>
    public static async Task<bool> ShutdownAsync()
    {
        string? steam = SteamPath();
        if (steam == null) return false;
        try { Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "-shutdown") { UseShellExecute = true }); } catch { }
        for (int i = 0; i < 50 && IsRunning; i++) await Task.Delay(500);
        return !IsRunning;
    }
}
