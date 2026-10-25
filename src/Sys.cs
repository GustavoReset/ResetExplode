using System.Diagnostics;

namespace ResetExplode;

/// <summary>Auxiliares para ler /proc, /sys e executar utilitários do sistema.</summary>
public static class Sys
{
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    public static long? ReadLong(string path) => long.TryParse(Read(path), out var v) ? v : null;

    public static string? Run(string file, string args = "", int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.Environment["LC_ALL"] = "C";
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return outTask.Result;
        }
        catch { return null; }
    }

    public static bool HasCommand(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, name)));

    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }

    public static Dictionary<string, string> KeyValues(string? text, char sep = ':')
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text == null) return d;
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(sep);
            if (i > 0) d.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return d;
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ConfigDir
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var d = Path.Combine(string.IsNullOrEmpty(x) ? Path.Combine(Home, ".config") : x, "resetexplode");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
