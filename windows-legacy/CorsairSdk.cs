using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ResetExplode;

/// <summary>Dispositivo Corsair exposto pelo iCUE (inclui iCUE LINK: hub, ventoinhas, bombas, tiras...).</summary>
public sealed class CorsairDevice
{
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    public int Type { get; init; }
    /// <summary>Contagem de LEDs informada na ficha do dispositivo (para comparar com as posições lidas; diagnóstico).</summary>
    public int InfoLedCount { get; init; }
    public int ChannelCount { get; init; }
    /// <summary>Resultado da leitura das posições dos LEDs (diagnóstico).</summary>
    public string LedReadInfo { get; set; } = "";
    public uint[] LedIds { get; set; } = Array.Empty<uint>();
    /// <summary>Posição horizontal de cada LED, de 0 a 1 dentro do próprio dispositivo (usada pelos efeitos de onda).</summary>
    public double[] LedX { get; set; } = Array.Empty<double>();
    public int LedCount => LedIds.Length;

    public string TypeName => Type switch
    {
        1 => "Teclado", 2 => "Mouse", 4 => "Mousepad", 8 => "Headset", 16 => "Suporte de headset",
        32 => "Controlador de ventoinhas", 64 => "Controlador de LED", 128 => "Memória", 256 => "Cooler",
        512 => "Placa-mãe", 1024 => "Placa de vídeo", 2048 => "Touchbar", 4096 => "Controle", _ => "Dispositivo"
    };
}

/// <summary>
/// Ponte com o iCUE SDK v4 da Corsair (iCUESDK.x64_2019.dll). A DLL não vem junto: é carregada em tempo de execução
/// da pasta do programa ou de %AppData%\ResetExplode. O iCUE precisa estar aberto, com o SDK habilitado nas configurações.
/// Nada é gravado no hardware: as cores valem enquanto o controle estiver ativo e voltam ao iCUE ao liberar.
/// </summary>
public sealed unsafe class CorsairSdk : IDisposable
{
    private const string DllFile = "iCUESDK.x64_2019.dll";

    // ---- assinaturas nativas (todas usam a convenção padrão do x64) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConnectFn(IntPtr onState, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDevicesFn(IntPtr filter, int sizeMax, IntPtr devices, out int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetLedPositionsFn([MarshalAs(UnmanagedType.LPStr)] string deviceId, int sizeMax, IntPtr positions, out int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetLedColorsFn([MarshalAs(UnmanagedType.LPStr)] string deviceId, int size, IntPtr colors);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestControlFn([MarshalAs(UnmanagedType.LPStr)] string deviceId, int accessLevel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReleaseControlFn([MarshalAs(UnmanagedType.LPStr)] string deviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetPriorityFn(uint priority);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateCb(IntPtr context, IntPtr eventData);

    private IntPtr _lib;
    private ConnectFn? _connect;
    private NoArgFn? _disconnect;
    private GetDevicesFn? _getDevices;
    private GetLedPositionsFn? _getLeds;
    private SetLedColorsFn? _setColors;
    private RequestControlFn? _request;
    private ReleaseControlFn? _release;
    private SetPriorityFn? _priority;
    private StateCb? _cb; // referência mantida: o SDK chama de volta por este delegate

    /// <summary>Estado da sessão com o iCUE (6 = conectado).</summary>
    public int State { get; private set; }
    public event Action<int>? StateChanged;
    public bool DllLoaded => _lib != IntPtr.Zero;
    public string? LoadedFrom { get; private set; }

    public static string StateText(int s) => s switch
    {
        1 => "fechada", 2 => "conectando…", 3 => "tempo esgotado", 4 => "conexão recusada pelo iCUE",
        5 => "conexão perdida", 6 => "conectado", _ => "desconhecido"
    };

    public static string ErrorText(int e) => e switch
    {
        0 => "ok", 1 => "não conectado ao iCUE", 2 => "sem controle do dispositivo (outro programa o controla)",
        3 => "versão do SDK incompatível com o iCUE", 4 => "argumentos inválidos", 5 => "operação inválida",
        6 => "dispositivo não encontrado", 7 => "o iCUE não permitiu (SDK desativado nas configurações?)", _ => $"erro {e}"
    };

    /// <summary>Procura e carrega a DLL do SDK. Devolve null se deu certo, ou a explicação.</summary>
    public string? LoadDll()
    {
        if (DllLoaded) return null;
        var dirs = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "x64"), AppSettings.Folder };
        var names = new[] { DllFile, "iCUESDK.dll" };
        foreach (var d in dirs)
            foreach (var n in names)
            {
                string p = Path.Combine(d, n);
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out _lib)) { LoadedFrom = p; goto loaded; }
            }
        if (NativeLibrary.TryLoad(DllFile, out _lib)) { LoadedFrom = DllFile; goto loaded; }
        return $"Não encontrei o {DllFile}. Use o botão BAIXAR O SDK DA CORSAIR (ou copie a DLL para a pasta do ResetExplode.exe ou para {AppSettings.Folder}).";

    loaded:
        try
        {
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
            _connect = Get<ConnectFn>("CorsairConnect");
            _disconnect = Get<NoArgFn>("CorsairDisconnect");
            _getDevices = Get<GetDevicesFn>("CorsairGetDevices");
            _getLeds = Get<GetLedPositionsFn>("CorsairGetLedPositions");
            _setColors = Get<SetLedColorsFn>("CorsairSetLedColors");
            _request = Get<RequestControlFn>("CorsairRequestControl");
            _release = Get<ReleaseControlFn>("CorsairReleaseControl");
            if (NativeLibrary.TryGetExport(_lib, "CorsairSetLayerPriority", out var pr)) _priority = Marshal.GetDelegateForFunctionPointer<SetPriorityFn>(pr);
            return null;
        }
        catch (Exception ex)
        {
            NativeLibrary.Free(_lib); _lib = IntPtr.Zero;
            return $"A DLL carregou, mas não tem as funções esperadas do iCUE SDK v4 ({ex.Message}). Use a versão 4.x do SDK.";
        }
    }

    /// <summary>Abre a sessão com o iCUE. O resultado chega por StateChanged (6 = conectado).</summary>
    public int Connect()
    {
        if (_connect == null) return 1;
        _cb = (_, data) =>
        {
            if (data == IntPtr.Zero) return;
            State = Marshal.ReadInt32(data); // primeiro campo: CorsairSessionState
            StateChanged?.Invoke(State);
        };
        return _connect(Marshal.GetFunctionPointerForDelegate(_cb), IntPtr.Zero);
    }

    private static string AnsiString(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    private static bool Printable(string s) => s.Length > 0 && s.All(c => c >= 0x20 && c < 0x7F);

    /// <summary>Lista os dispositivos e seus LEDs. Detecta sozinho o tamanho dos campos de texto da estrutura (64/128/256) por segurança.</summary>
    public List<CorsairDevice> ReadDevices(out string? problem)
    {
        problem = null;
        var result = new List<CorsairDevice>();
        if (_getDevices == null || _getLeds == null) { problem = "SDK não carregado."; return result; }

        const int Max = 64;
        IntPtr filter = Marshal.AllocHGlobal(4);
        IntPtr buf = Marshal.AllocHGlobal(Max * 1024);
        try
        {
            Marshal.WriteInt32(filter, -1); // todos os tipos
            new Span<byte>((void*)buf, Max * 1024).Clear();
            int err = _getDevices(filter, Max, buf, out int count);
            if (err != 0) { problem = "CorsairGetDevices: " + ErrorText(err); return result; }
            count = Math.Clamp(count, 0, Max);
            if (count == 0) return result;

            foreach (int str in new[] { 128, 256, 64 })
            {
                int stride = 4 + str * 3 + 8;
                var list = new List<CorsairDevice>();
                bool ok = true;
                for (int i = 0; i < count && ok; i++)
                {
                    byte* b = (byte*)buf + i * stride;
                    string id = AnsiString(b + 4, str);
                    string model = AnsiString(b + 4 + str * 2, str);
                    if (!Printable(id) || (model.Length > 0 && !Printable(model))) { ok = false; break; }
                    list.Add(new CorsairDevice { Id = id, Model = model, Type = *(int*)b, InfoLedCount = *(int*)(b + 4 + str * 3), ChannelCount = *(int*)(b + 8 + str * 3) });
                }
                if (!ok) continue;
                foreach (var d in list) FillLeds(d);
                return list;
            }
            problem = "Não consegui ler a lista de dispositivos (estrutura do SDK diferente da esperada). Atualize o iCUE e use o SDK 4.x.";
            return result;
        }
        finally { Marshal.FreeHGlobal(filter); Marshal.FreeHGlobal(buf); }
    }

    private void FillLeds(CorsairDevice d)
    {
        // O SDK limita CORSAIR_DEVICE_LEDCOUNT_MAX a 512; pedir mais devolve "argumentos inválidos". Se ainda recusar, tenta tamanhos menores.
        IntPtr buf = Marshal.AllocHGlobal(1024 * 24);
        try
        {
            int err = -1, n = 0;
            string tried = "";
            foreach (int cap in new[] { 512, 256, 128, 64, 32 })
            {
                new Span<byte>((void*)buf, 1024 * 24).Clear();
                err = _getLeds!(d.Id, cap, buf, out n);
                tried += $"{cap}:{ErrorText(err)} ";
                if (err == 0) break;
            }
            if (err != 0) { d.LedReadInfo = "CorsairGetLedPositions falhou (" + tried.Trim() + ")"; return; }
            n = Math.Clamp(n, 0, 1024);
            d.LedReadInfo = $"ok, {n} posições";
            var ids = new uint[n];
            var xs = new double[n];
            for (int i = 0; i < n; i++)
            {
                byte* p = (byte*)buf + i * 24; // { uint id; (4 de alinhamento); double cx; double cy }
                ids[i] = *(uint*)p;
                double x = *(double*)(p + 8);
                xs[i] = double.IsFinite(x) ? x : 0;
            }
            double min = xs.Length > 0 ? xs.Min() : 0, max = xs.Length > 0 ? xs.Max() : 1;
            double span = max - min;
            for (int i = 0; i < n; i++) xs[i] = span > 1e-9 ? (xs[i] - min) / span : (n > 1 ? i / (double)(n - 1) : 0);
            d.LedIds = ids;
            d.LedX = xs;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Pede o controle exclusivo da iluminação (necessário para as cores valerem sobre as do iCUE).</summary>
    public int RequestControl(CorsairDevice d, int level = 1) => _request?.Invoke(d.Id, level) ?? 1;
    /// <summary>Prioridade da camada de cores deste programa (0–255). Devolve -1 se esta versão do SDK não tem a função.</summary>
    public int SetLayerPriority(uint p) => _priority?.Invoke(p) ?? -1;
    public int ReleaseControl(CorsairDevice d) => _release?.Invoke(d.Id) ?? 1;

    private IntPtr _colorBuf;
    private int _colorCap;

    /// <summary>Define a cor de cada LED do dispositivo (rgb por LED, 0–255).</summary>
    public int SetColors(CorsairDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        if (_setColors == null) return 1;
        int n = Math.Min(colors.Length, d.LedIds.Length);
        if (n == 0) return 0;
        if (_colorCap < n)
        {
            if (_colorBuf != IntPtr.Zero) Marshal.FreeHGlobal(_colorBuf);
            _colorCap = Math.Max(n, 64);
            _colorBuf = Marshal.AllocHGlobal(_colorCap * 8);
        }
        byte* p = (byte*)_colorBuf;
        for (int i = 0; i < n; i++)
        {
            *(uint*)(p + i * 8) = d.LedIds[i];
            p[i * 8 + 4] = colors[i].R; p[i * 8 + 5] = colors[i].G; p[i * 8 + 6] = colors[i].B; p[i * 8 + 7] = 255;
        }
        return _setColors(d.Id, n, _colorBuf);
    }

    public void Disconnect()
    {
        try { _disconnect?.Invoke(); } catch { }
        State = 1;
    }

    public void Dispose()
    {
        Disconnect();
        if (_colorBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_colorBuf); _colorBuf = IntPtr.Zero; }
        // A DLL fica carregada até o programa fechar (descarregar com sessão ativa trava o SDK).
    }
}
