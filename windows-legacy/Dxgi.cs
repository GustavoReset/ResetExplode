using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>Enumera adaptadores via DXGI (VRAM real, sem o limite de 4 GB do WMI) e testa o feature level do Direct3D 12.</summary>
internal static class Dxgi
{
    public record Adapter(string Description, uint VendorId, uint DeviceId,
                          ulong DedicatedVideo, ulong DedicatedSystem, ulong SharedSystem, string? FeatureLevel);

    public static List<Adapter> Enumerate()
    {
        var list = new List<Adapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) < 0) return list;
            for (uint i = 0; factory.EnumAdapters1(i, out var a) == 0; i++)
            {
                if (a.GetDesc1(out var d) == 0 && (d.Flags & 2) == 0) // ignora o adaptador de software (WARP)
                    list.Add(new Adapter(d.Description, d.VendorId, d.DeviceId,
                                         d.DedicatedVideoMemory, d.DedicatedSystemMemory, d.SharedSystemMemory,
                                         FeatureLevel(a)));
                Marshal.ReleaseComObject(a);
            }
            Marshal.ReleaseComObject(factory);
        }
        catch { }
        return list;
    }

    /// <summary>Maior feature level aceito. Com ppDevice nulo o D3D12 só testa, sem criar o dispositivo.</summary>
    private static string? FeatureLevel(IDXGIAdapter1 adapter)
    {
        var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
        (int Level, string Name)[] levels =
        {
            (0xc200, "12_2"), (0xc100, "12_1"), (0xc000, "12_0"), (0xb100, "11_1"), (0xb000, "11_0")
        };
        try
        {
            foreach (var (level, name) in levels)
                if (D3D12CreateDevice(adapter, level, ref iid, IntPtr.Zero) >= 0) return name;
        }
        catch { }
        return null;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice([MarshalAs(UnmanagedType.IUnknown)] object adapter,
                                                int minimumFeatureLevel, ref Guid riid, IntPtr device);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Só os slots da vtable importam; métodos não usados ficam como marcadores na ordem correta.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();          // IDXGIObject
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain();
        void CreateSoftwareAdapter();                                                                            // IDXGIFactory
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);                                  // IDXGIFactory1
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();          // IDXGIObject
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();                                        // IDXGIAdapter
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);                                                       // IDXGIAdapter1
    }
}
