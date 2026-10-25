using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ResetExplode;

/// <summary>Dispositivo USB/serial registrado no Windows (conectado ou "fantasma").</summary>
public sealed class UsbDevice : INotifyPropertyChanged
{
    public string InstanceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    public string? Port { get; init; }
    public bool Present { get; init; }
    public string? Maker { get; init; }   // Arduino, CH340, FTDI... (pelo VID)
    public string? Vid { get; init; }
    public string? Pid { get; init; }
    /// <summary>Código de problema do Gerenciador de Dispositivos (28 = sem driver, 10 = não inicia...); null = funcionando.</summary>
    public uint? Problem { get; init; }
    /// <summary>Chave do driver em HKLM\SYSTEM\CurrentControlSet\Control\Class (ex.: {guid}\0003).</summary>
    public string? DriverKey { get; init; }
    public string Detail => string.Join(" · ", new[] { Port, Maker, Class }.Where(s => !string.IsNullOrEmpty(s)));

    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Limpeza de registros USB antigos (dispositivos "fantasmas") e das reservas de número COM, que fazem a
/// IDE do Arduino não achar a placa ou a porta ir para COM20+. Remover um dispositivo ausente é seguro:
/// ao plugar de novo, o Windows reinstala. Requer administrador.
/// </summary>
public static class UsbCleaner
{
    /// <summary>Classes relevantes para Arduino/ESP/conversores seriais.</summary>
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ports", "USB", "USBDevice", "libusbK devices", "libusb-win32 devices", "WinUSB", "Modem",
    };

    /// <summary>Fabricantes por Vendor ID USB (placas e conversores USB-serial comuns).</summary>
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2341"] = "Arduino", ["2A03"] = "Arduino", ["1A86"] = "CH340/CH341", ["0403"] = "FTDI", ["10C4"] = "CP210x",
        ["303A"] = "Espressif (ESP32)", ["239A"] = "Adafruit", ["16C0"] = "Teensy", ["1B4F"] = "SparkFun",
        ["2E8A"] = "Raspberry Pi Pico", ["067B"] = "Prolific PL2303", ["0483"] = "STMicro", ["1366"] = "SEGGER",
    };

    public static List<UsbDevice> List()
    {
        var list = new List<UsbDevice>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES); // sem DIGCF_PRESENT: inclui ausentes
        if (set == INVALID_HANDLE) throw new Win32Exception();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string cls = RegString(set, ref data, SPDRP_CLASS) ?? "";
                string id = InstanceId(set, ref data);
                var vid = System.Text.RegularExpressions.Regex.Match(id, @"VID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool knownVid = vid.Success && Vendors.ContainsKey(vid.Groups[1].Value);
                // Placa conectada sem driver não tem classe ("Outros dispositivos"): entra pelo VID conhecido.
                if (!Classes.Contains(cls) && !knownVid) continue;
                bool present = CM_Get_DevNode_Status(out uint status, out uint problem, data.DevInst, 0) == 0;
                // Hubs e controladores USB raiz não interessam aqui (e não devem ser mexidos).
                if (cls.Equals("USB", StringComparison.OrdinalIgnoreCase) && (id.StartsWith("USB\\ROOT_HUB") || id.StartsWith("PCI\\"))) continue;

                string name = RegString(set, ref data, SPDRP_FRIENDLYNAME) ?? RegString(set, ref data, SPDRP_DEVICEDESC) ?? id;
                var pid = System.Text.RegularExpressions.Regex.Match(id, @"PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                list.Add(new UsbDevice
                {
                    InstanceId = id, Name = name, Class = cls, Present = present, Port = PortName(set, ref data),
                    Maker = knownVid ? Vendors[vid.Groups[1].Value] : null,
                    Vid = vid.Success ? vid.Groups[1].Value.ToUpperInvariant() : null,
                    Pid = pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : null,
                    Problem = present && (status & DN_HAS_PROBLEM) != 0 ? problem : null,
                    DriverKey = RegString(set, ref data, SPDRP_DRIVER),
                    // Pré-marca só fantasmas USB de porta COM ou de placas/conversores conhecidos (não a serial da placa-mãe,
                    // que vem como ACPI); o resto o usuário decide.
                    Selected = !present && !id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) && (cls.Equals("Ports", StringComparison.OrdinalIgnoreCase) || vid.Success && Vendors.ContainsKey(vid.Groups[1].Value)),
                });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list.OrderBy(d => d.Present).ThenByDescending(d => d.Maker != null).ThenBy(d => d.Port == null).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Remove os dispositivos (só os ausentes; conectados são ignorados por segurança).</summary>
    public static (int Removed, List<string> Failed) Remove(IEnumerable<string> instanceIds)
    {
        var wanted = new HashSet<string>(instanceIds, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        var failed = new List<string>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) throw new Win32Exception();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string id = InstanceId(set, ref data);
                if (!wanted.Contains(id)) continue;
                if (CM_Get_DevNode_Status(out _, out _, data.DevInst, 0) == 0) { failed.Add($"{id} (está conectado)"); continue; }
                if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref data)) removed++;
                else failed.Add($"{id} ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return (removed, failed);
    }

    // ------------------------------------------------------------------ reservas de número COM (ComDB)

    private const string ArbiterKey = @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter";

    /// <summary>Números COM reservados no ComDB (cada bit = uma porta) e os realmente em uso agora.</summary>
    public static (List<int> Reserved, List<int> InUse) ComStatus()
    {
        var reserved = new List<int>();
        if (Registry.LocalMachine.OpenSubKey(ArbiterKey)?.GetValue("ComDB") is byte[] db)
            for (int n = 1; n <= db.Length * 8; n++)
                if ((db[(n - 1) / 8] & (1 << ((n - 1) % 8))) != 0) reserved.Add(n);
        return (reserved, PortsInUse());
    }

    /// <summary>Portas COM ativas: as que o Windows publicou em SERIALCOMM + as de dispositivos conectados.</summary>
    private static List<int> PortsInUse()
    {
        var ports = new HashSet<int>();
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            if (k != null)
                foreach (var n in k.GetValueNames())
                    if (ParseCom(k.GetValue(n) as string) is int p) ports.Add(p);
        foreach (var d in List().Where(d => d.Present))
            if (ParseCom(d.Port) is int p) ports.Add(p);
        return ports.OrderBy(p => p).ToList();
    }

    private static int? ParseCom(string? s) =>
        s != null && s.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[3..], out int n) ? n : null;

    /// <summary>
    /// Reescreve o ComDB mantendo só as portas em uso agora: os números presos por dispositivos antigos voltam a
    /// ficar livres (o próximo Arduino plugado pega COM3/COM4 em vez de COM20). Retorna o valor antigo (backup).
    /// </summary>
    public static byte[] RebuildComDb()
    {
        using var k = Registry.LocalMachine.OpenSubKey(ArbiterKey, writable: true)
                      ?? throw new InvalidOperationException("Chave COM Name Arbiter não encontrada.");
        byte[] old = k.GetValue("ComDB") as byte[] ?? new byte[32];
        var db = new byte[Math.Max(32, old.Length)];
        foreach (int p in PortsInUse())
            if (p >= 1 && p <= db.Length * 8) db[(p - 1) / 8] |= (byte)(1 << ((p - 1) % 8));
        k.SetValue("ComDB", db, RegistryValueKind.Binary);
        return old;
    }

    public static void RestoreComDb(byte[] backup)
    {
        using var k = Registry.LocalMachine.OpenSubKey(ArbiterKey, writable: true)
                      ?? throw new InvalidOperationException("Chave COM Name Arbiter não encontrada.");
        k.SetValue("ComDB", backup, RegistryValueKind.Binary);
    }

    // ------------------------------------------------------------------ SetupAPI

    private static string InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : "";
    }

    private static string? RegString(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, (uint)buf.Length, out uint req)) return null;
        string s = Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req, buf.Length)).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    /// <summary>"PortName" (COMx) fica na chave de parâmetros do dispositivo; existe também para dispositivos ausentes.</summary>
    private static string? PortName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        IntPtr h = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (h == INVALID_HANDLE || h == IntPtr.Zero) return null;
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(h, ownsHandle: true));
        return key.GetValue("PortName") as string;
    }

    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const int DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_CLASS = 0x7, SPDRP_DRIVER = 0x9, SPDRP_FRIENDLYNAME = 0xC;
    private const uint DN_HAS_PROBLEM = 0x400;
    private const uint DIF_REMOVE = 0x5, DICS_FLAG_GLOBAL = 0x1, DIREG_DEV = 0x1, KEY_READ = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint regType,
                                                                 byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint hwProfile, uint keyType, uint access);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
