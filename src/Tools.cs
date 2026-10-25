namespace ResetExplode;

public sealed record GameEntry(string Name, string AppId, string Library, long SizeBytes);
public sealed record AutostartEntry(string Name, string Path, bool Enabled);

/// <summary>Ferramentas Linux: jogos da Steam, autostart (XDG), portas seriais e ajustes de desempenho (somente leitura).</summary>
public static class Tools
{
    public static List<GameEntry> SteamGames()
    {
        var games = new List<GameEntry>();
        var roots = new[] { ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" }
            .Select(r => Path.Combine(Sys.Home, r)).Where(Directory.Exists).ToList();
        var libs = new HashSet<string>();
        foreach (var r in roots)
        {
            libs.Add(r);
            var vdf = Path.Combine(r, "steamapps/libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                var root = Vdf.Parse(File.ReadAllText(vdf));
                var lf = root.Child("libraryfolders") ?? root;
                foreach (var c in lf.Children) if (c.Value.Get("path") is { } p) libs.Add(p);
            }
            catch { }
        }
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var f in Directory.GetFiles(apps, "appmanifest_*.acf"))
                try
                {
                    var s = Vdf.Parse(File.ReadAllText(f));
                    var st = s.Child("AppState") ?? s;
                    string name = st.Get("name") ?? "?";
                    if (name.StartsWith("Proton") || name.StartsWith("Steam Linux Runtime") || name.StartsWith("Steamworks")) continue;
                    games.Add(new GameEntry(name, st.Get("appid") ?? "", lib, long.TryParse(st.Get("SizeOnDisk"), out var sz) ? sz : 0));
                }
                catch { }
        }
        return games.OrderBy(g => g.Name).ToList();
    }

    public static void Launch(GameEntry g)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"steam://rungameid/{g.AppId}") { UseShellExecute = false });

    private static string AutostartDir => Path.Combine(Sys.Home, ".config/autostart");

    public static List<AutostartEntry> Autostart()
    {
        var list = new List<AutostartEntry>();
        if (!Directory.Exists(AutostartDir)) return list;
        foreach (var f in Directory.GetFiles(AutostartDir, "*.desktop").OrderBy(x => x))
        {
            var kv = Sys.KeyValues(File.ReadAllText(f), '=');
            bool hidden = kv.TryGetValue("Hidden", out var h) && h == "true";
            list.Add(new AutostartEntry(kv.GetValueOrDefault("Name", Path.GetFileNameWithoutExtension(f)), f, !hidden));
        }
        return list;
    }

    public static void SetAutostartEnabled(AutostartEntry e, bool enabled)
    {
        var lines = File.ReadAllLines(e.Path).Where(l => !l.StartsWith("Hidden=")).ToList();
        if (!enabled) lines.Add("Hidden=true");
        File.WriteAllLines(e.Path, lines);
    }

    public static List<DiskHealthCard> SerialPorts()
    {
        var m = new List<KeyValuePair<string, string>>();
        foreach (var dev in Directory.GetFiles("/dev", "tty*").Where(d => d.Contains("ttyUSB") || d.Contains("ttyACM")).OrderBy(x => x))
        {
            var info = Sys.Run("udevadm", $"info -q property -n {dev}") ?? "";
            var p = Sys.KeyValues(info, '=');
            m.Add(new(dev, $"{p.GetValueOrDefault("ID_VENDOR_FROM_DATABASE", p.GetValueOrDefault("ID_VENDOR", "?"))} {p.GetValueOrDefault("ID_MODEL_FROM_DATABASE", p.GetValueOrDefault("ID_MODEL", ""))} [{p.GetValueOrDefault("ID_VENDOR_ID", "")}:{p.GetValueOrDefault("ID_MODEL_ID", "")}]"));
        }
        string user = Environment.UserName;
        bool dialout = (Sys.Run("id", "-nG") ?? "").Split(' ', '\n').Contains("dialout");
        var warn = new List<string>();
        if (!dialout) warn.Add($"O usuário {user} não está no grupo 'dialout': rode 'sudo usermod -aG dialout {user}' e faça login de novo para acessar as portas.");
        if (File.Exists("/usr/lib/udev/rules.d/85-brltty.rules") || Directory.Exists("/usr/lib/brltty"))
            warn.Add("O brltty está instalado e costuma capturar CH340/CP210x (Arduino); remova com 'sudo apt remove brltty' se a porta sumir.");
        if (m.Count == 0) m.Add(new("Portas", "nenhuma /dev/ttyUSB* ou /dev/ttyACM* encontrada"));
        return new() { new DiskHealthCard { Title = "Portas seriais (Arduino, CH340, CP210x, FTDI)", Meta = "drivers já vêm no kernel Linux", Verdict = warn.Count == 0 ? DiskVerdict.Good : DiskVerdict.Warning, Label = warn.Count == 0 ? "OK" : "ATENÇÃO", Reasons = string.Join(" ", warn), Metrics = m } };
    }

    public static List<DiskHealthCard> PerformanceTweaks()
    {
        var m = new List<KeyValuePair<string, string>>
        {
            new("CPU governor", Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor") ?? "—"),
            new("Perfil de energia", (Sys.Run("powerprofilesctl", "get") ?? "—").Trim()),
            new("vm.swappiness", Sys.Read("/proc/sys/vm/swappiness") ?? "—"),
            new("Transparent Huge Pages", Sys.Read("/sys/kernel/mm/transparent_hugepage/enabled") ?? "—"),
            new("GameMode (feralgamemode)", Sys.HasCommand("gamemoderun") ? "instalado" : "não instalado (sudo apt install gamemode)"),
        };
        return new() { new DiskHealthCard { Title = "Ajustes de desempenho", Meta = "somente leitura", Verdict = DiskVerdict.Good, Label = "INFO",
            Reasons = "Para jogos use 'powerprofilesctl set performance' e inicie com 'gamemoderun %command%' na Steam.", Metrics = m } };
    }
}
