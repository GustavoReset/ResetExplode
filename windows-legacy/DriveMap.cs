using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Media;

namespace ResetExplode;

/// <summary>Cartão de uma unidade (letra) com o anel de ocupação já calculado.</summary>
public sealed class DriveCard
{
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string Disk { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public bool Removable { get; init; }

    public double UsedFraction => Total > 0 ? (double)(Total - Free) / Total : 0;
    public string UsedPercent => $"{UsedFraction:P0}";
    public string Title => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter}  {Label}";
    public string Sizes => $"{Fmt(Total - Free)} usados · {Fmt(Free)} livres de {Fmt(Total)}";
    public string Meta => string.Join(" · ", new[] { Kind, FileSystem, Disk }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Arco do anel (parte usada), em um quadrado de 84×84 com traço centrado no raio 36.</summary>
    public Geometry UsedArc => Arc(UsedFraction, 42, 42, 36);

    public static string Fmt(long b)
    {
        double v = b; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    private static Geometry Arc(double frac, double cx, double cy, double r)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0) return Geometry.Empty;
        double a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);                                       // topo, sentido horário
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }
}

/// <summary>Lê as unidades montadas (letras) e o disco físico de cada uma.</summary>
public static class DriveMap
{
    public static List<DriveCard> Read()
    {
        var disks = PhysicalDiskByLetter();
        var list = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string letter = d.Name.TrimEnd('\\');
                disks.TryGetValue(letter, out var disk);
                bool usb = disk.Bus?.Equals("USB", StringComparison.OrdinalIgnoreCase) == true;
                string kind = d.DriveType switch
                {
                    DriveType.Removable => "Removível",
                    DriveType.Fixed when usb => "Externo (USB)",
                    DriveType.Fixed => "Interno",
                    DriveType.Network => "Rede",
                    DriveType.CDRom => "Óptico",
                    _ => d.DriveType.ToString()
                };
                list.Add(new DriveCard
                {
                    Letter = letter, Label = d.VolumeLabel, Kind = kind, FileSystem = d.DriveFormat,
                    Disk = disk.Model ?? "", Total = d.TotalSize, Free = d.TotalFreeSpace,
                    Removable = d.DriveType == DriveType.Removable || usb,
                });
            }
            catch { /* unidade sumiu no meio da leitura */ }
        }
        return list.OrderBy(c => c.Letter).ToList();
    }

    /// <summary>
    /// "C:" → modelo e barramento do disco físico. Lê as tabelas de associação do WMI (disco→partição e
    /// partição→letra) direto, porque consultas "ASSOCIATORS OF {caminho}" falham em algumas máquinas.
    /// </summary>
    private static Dictionary<string, (string? Model, string? Bus)> PhysicalDiskByLetter()
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var disks = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT DeviceID, Model, InterfaceType, PNPDeviceID FROM Win32_DiskDrive"))
                foreach (ManagementObject d in s.Get())
                {
                    string? bus = d["InterfaceType"]?.ToString();
                    if (d["PNPDeviceID"]?.ToString()?.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) == true) bus = "USB";
                    disks[d["DeviceID"].ToString()!] = (d["Model"]?.ToString()?.Trim(), bus);
                }

            var diskOfPartition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
                foreach (ManagementObject a in s.Get())
                    if (RefId(a["Antecedent"]) is string disk && RefId(a["Dependent"]) is string part) diskOfPartition[part] = disk;

            using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                foreach (ManagementObject a in s.Get())
                    if (RefId(a["Antecedent"]) is string part && RefId(a["Dependent"]) is string letter &&
                        diskOfPartition.TryGetValue(part, out var disk) && disks.TryGetValue(disk, out var info))
                        map[letter] = info;
        }
        catch { }
        return map;
    }

    /// <summary>Referência WMI → valor de DeviceID: ...Win32_DiskDrive.DeviceID="\\\\.\\PHYSICALDRIVE0" → \\.\PHYSICALDRIVE0.</summary>
    private static string? RefId(object? reference)
    {
        var m = System.Text.RegularExpressions.Regex.Match(reference?.ToString() ?? "", "DeviceID=\"(.*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }
}
