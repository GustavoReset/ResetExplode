using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ResetExplode;

public sealed record GpuGameResult(double AvgFps, double Low1Fps, int Frames, double Seconds, int Width, int Height, string Adapter)
{
    /// <summary>Nota = FPS médio × 10.</summary>
    public double Score => AvgFps * 10;
}

/// <summary>
/// Cena de jogo para benchmark de GPU (no estilo 3DMark/Unigine): vale com terreno por ruído fractal, 40 mil árvores
/// por instancing, sombras em tempo real (shadow map 4096² com PCF), água com ondas procedurais, neblina, céu e
/// pós-processamento HDR. Câmera em trajeto fixo por tempo, resolução fixa: o trabalho é igual em qualquer PC.
/// Usa a mesma técnica do stress de GPU para não ficar preso ao V-Sync (render fora da tela + fila de frames).
/// </summary>
public sealed class GpuGameBench
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);
    public string AdapterName { get; init; } = "";

    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    public string? Error { get; private set; }
    public bool Cancelled { get; private set; }

    private volatile bool _stop;
    private WndProc? _wndProc;

    public void Cancel() => _stop = true;

    // ------------------------------------------------------------------ cena (CPU)

    private const int TerrainN = 512;        // vértices por lado
    private const float TerrainSize = 800f;
    private const float WaterY = 6f;
    private const int TreeCount = 40_000;

    /// <summary>Altura do terreno: ruído fractal (fBm) com um vale sinuoso cortando o mapa.</summary>
    public static float TerrainHeight(float x, float z)
    {
        float h = 0, amp = 1, freq = 0.005f;
        for (int o = 0; o < 6; o++) { h += amp * ValueNoise(x * freq + o * 17.3f, z * freq - o * 9.1f); amp *= 0.5f; freq *= 2.03f; }
        h = h * 55 + 10;
        float river = MathF.Sin(z * 0.007f) * 120;                       // curva do vale
        float d = MathF.Abs(x - river);
        float valley = MathF.Min(1, d / 90f);
        return h * (0.25f + 0.75f * valley * valley) - (1 - valley) * 4;
    }

    private static float ValueNoise(float x, float z)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        float u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
        float a = Hash(xi, zi), b = Hash(xi + 1, zi), c = Hash(xi, zi + 1), d = Hash(xi + 1, zi + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static float Hash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector3 Pos; public Vector3 Normal; public float Mat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameCB
    {
        public Matrix4x4 ViewProj, LightViewProj, InvViewProj;
        public Vector3 CamPos; public float Time;
        public Vector3 SunDir; public float Pad;
    }

    private static (Vertex[] V, uint[] I) BuildTerrain()
    {
        var v = new Vertex[TerrainN * TerrainN];
        float step = TerrainSize / (TerrainN - 1), half = TerrainSize / 2;
        for (int z = 0; z < TerrainN; z++)
            for (int x = 0; x < TerrainN; x++)
            {
                float wx = x * step - half, wz = z * step - half;
                float h = TerrainHeight(wx, wz);
                float hx = TerrainHeight(wx + 0.5f, wz) - TerrainHeight(wx - 0.5f, wz);
                float hz = TerrainHeight(wx, wz + 0.5f) - TerrainHeight(wx, wz - 0.5f);
                v[z * TerrainN + x] = new Vertex { Pos = new(wx, h, wz), Normal = Vector3.Normalize(new(-hx, 1, -hz)), Mat = 0 };
            }
        var idx = new uint[(TerrainN - 1) * (TerrainN - 1) * 6];
        int k = 0;
        for (int z = 0; z < TerrainN - 1; z++)
            for (int x = 0; x < TerrainN - 1; x++)
            {
                uint a = (uint)(z * TerrainN + x), b = a + 1, c = a + TerrainN, d = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        return (v, idx);
    }

    /// <summary>Árvore low-poly: tronco (cilindro) + 3 cones de folhagem. Mat 1 = tronco, 2 = folhas.</summary>
    private static (Vertex[] V, uint[] I) BuildTree()
    {
        var v = new List<Vertex>(); var idx = new List<uint>();
        void Cone(float baseY, float height, float r0, float r1, int seg, float mat)
        {
            uint start = (uint)v.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau, c = MathF.Cos(a), s = MathF.Sin(a);
                float slope = (r0 - r1) / height;
                var n = Vector3.Normalize(new Vector3(c, slope, s));
                v.Add(new Vertex { Pos = new(c * r0, baseY, s * r0), Normal = n, Mat = mat });
                v.Add(new Vertex { Pos = new(c * r1, baseY + height, s * r1), Normal = n, Mat = mat });
            }
            for (int i = 0; i < seg; i++)
            {
                uint a = start + (uint)i * 2;
                idx.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
            }
        }
        Cone(0, 2.0f, 0.25f, 0.2f, 8, 1);
        Cone(1.4f, 2.6f, 2.0f, 0.05f, 12, 2);
        Cone(2.8f, 2.3f, 1.6f, 0.05f, 12, 2);
        Cone(4.0f, 2.0f, 1.1f, 0.02f, 12, 2);
        return (v.ToArray(), idx.ToArray());
    }

    private static Vector4[] PlaceTrees()
    {
        var list = new List<Vector4>(TreeCount);
        uint s = 2024;
        float R() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s >> 8) / 16777216f; }
        while (list.Count < TreeCount)
        {
            float x = (R() - 0.5f) * TerrainSize * 0.98f, z = (R() - 0.5f) * TerrainSize * 0.98f;
            float h = TerrainHeight(x, z);
            float slope = MathF.Abs(TerrainHeight(x + 1, z) - TerrainHeight(x - 1, z)) + MathF.Abs(TerrainHeight(x, z + 1) - TerrainHeight(x, z - 1));
            if (h < WaterY + 1.5f || h > 48 || slope > 1.4f) continue;
            list.Add(new Vector4(x, h - 0.2f, z, 0.7f + R() * 0.9f));
        }
        return list.ToArray();
    }

    // Trajeto da câmera (Catmull-Rom fechado): sobe o vale por uma margem do rio e volta pela outra.
    private static readonly Vector2[] Path = BuildPath();

    private static Vector2[] BuildPath()
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 6; i++) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 + 45, z)); }
        for (int i = 6; i >= 0; i--) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 - 75, z)); }
        return p.ToArray();
    }

    private static Vector2 PathAt(float t)
    {
        int n = Path.Length;
        float f = (t % 1 + 1) % 1 * n;
        int i = (int)f; float u = f - i;
        Vector2 p0 = Path[(i - 1 + n) % n], p1 = Path[i], p2 = Path[(i + 1) % n], p3 = Path[(i + 2) % n];
        return 0.5f * (2 * p1 + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u * u * u);
    }

    private static float CamHeight(Vector2 p)
    {
        float h = WaterY;
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++) h = MathF.Max(h, TerrainHeight(p.X + dx * 10, p.Y + dz * 10));
        return h + 26;
    }

    // ------------------------------------------------------------------ shaders

    private const string Hlsl = @"
cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 LightViewProj;
    row_major float4x4 InvViewProj;
    float3 CamPos; float Time;
    float3 SunDir; float Pad;
};
StructuredBuffer<float4> Trees : register(t1);
Texture2D<float> ShadowMap : register(t0);
SamplerComparisonState ShadowSampler : register(s0);
Texture2D<float4> Hdr : register(t2);
SamplerState LinearSampler : register(s1);

struct VSIn { float3 pos : POSITION; float3 nrm : NORMAL; float mat : TEXCOORD0; uint iid : SV_InstanceID; };
struct PSIn { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 nrm : NORMAL; float mat : TEXCOORD1; };

float3 TreeWorld(float3 p, uint iid, out float3x3 rot)
{
    float4 t = Trees[iid];
    float a = frac(sin(iid * 12.9898) * 43758.5453) * 6.2831;
    float c = cos(a), s = sin(a);
    rot = float3x3(c, 0, -s, 0, 1, 0, s, 0, c);
    float sway = sin(Time * 1.3 + iid) * 0.03 * p.y;   // vento
    return mul(p, rot) * t.w + t.xyz + float3(sway, 0, sway * 0.5);
}

PSIn TerrainVS(VSIn i)
{
    PSIn o; o.wpos = i.pos; o.nrm = i.nrm; o.mat = i.mat;
    o.pos = mul(float4(i.pos, 1), ViewProj);
    return o;
}

PSIn TreeVS(VSIn i)
{
    PSIn o; float3x3 rot;
    o.wpos = TreeWorld(i.pos, i.iid, rot);
    o.nrm = mul(i.nrm, rot); o.mat = i.mat;
    o.pos = mul(float4(o.wpos, 1), ViewProj);
    return o;
}

float4 TerrainShadowVS(VSIn i) : SV_POSITION { return mul(float4(i.pos, 1), LightViewProj); }
float4 TreeShadowVS(VSIn i) : SV_POSITION { float3x3 r; return mul(float4(TreeWorld(i.pos, i.iid, r), 1), LightViewProj); }

float3 Sky(float3 dir)
{
    float up = saturate(dir.y);
    float3 col = lerp(float3(0.42, 0.42, 0.42), float3(0.12, 0.12, 0.12), pow(up, 0.6));
    float sun = saturate(dot(dir, -SunDir));
    col += pow(sun, 600) * 12 + pow(sun, 12) * 0.25;
    return col;
}

float Shadow(float3 wpos, float3 n)
{
    float4 lp = mul(float4(wpos + n * 0.15, 1), LightViewProj);
    float2 uv = lp.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv > 1)) return 1;
    float sum = 0;
    [unroll] for (int y = -2; y <= 2; y++)
        [unroll] for (int x = -2; x <= 2; x++)
            sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, uv + float2(x, y) / 4096.0, lp.z - 0.0015);
    return sum / 25;
}

float3 Shade(float3 wpos, float3 n, float albedo)
{
    float3 l = -SunDir;
    float ndl = saturate(dot(n, l));
    float3 direct = ndl * Shadow(wpos, n) * 1.5;
    float3 ambient = lerp(0.06, 0.16, n.y * 0.5 + 0.5);
    float3 col = albedo * (direct + ambient);
    float dist = length(wpos - CamPos);
    float fog = 1 - exp(-dist * 0.0022);
    return lerp(col, Sky(normalize(wpos - CamPos)), fog);
}

float4 TerrainPS(PSIn i) : SV_TARGET
{
    float3 n = normalize(i.nrm);
    float slope = 1 - n.y;
    float h = i.wpos.y;
    float albedo = lerp(0.22, 0.15, saturate((h - 8) / 25));               // grama
    albedo = lerp(albedo, 0.28, saturate((slope - 0.25) * 4));             // rocha nas encostas
    albedo = lerp(albedo, 0.70, saturate((h - 62) / 10) * saturate(1 - slope * 3)); // neve só nos picos
    albedo *= 0.9 + 0.1 * frac(sin(dot(floor(i.wpos.xz * 2), float2(12.9898, 78.233))) * 43758.5453);
    return float4(Shade(i.wpos, n, albedo), 1);
}

float4 TreePS(PSIn i) : SV_TARGET
{
    float albedo = i.mat < 1.5 ? 0.08 : 0.11;
    return float4(Shade(i.wpos, normalize(i.nrm), albedo), 1);
}

PSIn WaterVS(VSIn i)
{
    PSIn o; o.wpos = i.pos; o.nrm = float3(0, 1, 0); o.mat = 0;
    o.pos = mul(float4(i.pos, 1), ViewProj);
    return o;
}

float4 WaterPS(PSIn i) : SV_TARGET
{
    float2 p = i.wpos.xz;
    float3 n = float3(0, 1, 0);
    [unroll] for (int k = 0; k < 6; k++)           // ondas somadas
    {
        float2 d = float2(cos(k * 1.7), sin(k * 1.7));
        float f = 0.15 * (k + 1), ph = dot(d, p) * f + Time * (1 + k * 0.4);
        n.xz += d * cos(ph) * 0.06 / (k + 1);
    }
    n = normalize(n);
    float3 v = normalize(i.wpos - CamPos);
    float fres = 0.04 + 0.96 * pow(1 - saturate(dot(-v, n)), 5);
    float3 refl = Sky(reflect(v, n)) * Shadow(i.wpos, n);
    float3 col = lerp(float3(0.05, 0.05, 0.05), refl, fres);
    float fog = 1 - exp(-length(i.wpos - CamPos) * 0.0022);
    return float4(lerp(col, Sky(v), fog), 1);
}

struct FsOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
FsOut FullscreenVS(uint id : SV_VertexID)
{
    FsOut o; o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 1, 1);
    return o;
}

float4 SkyPS(FsOut i) : SV_TARGET
{
    float4 w = mul(float4(i.uv * float2(2, -2) + float2(-1, 1), 1, 1), InvViewProj);
    return float4(Sky(normalize(w.xyz / w.w - CamPos)), 1);
}

float4 PostPS(FsOut i) : SV_TARGET
{
    float3 c = Hdr.Sample(LinearSampler, i.uv).rgb;
    c = c * (2.51 * c + 0.03) / (c * (2.43 * c + 0.59) + 0.14);         // ACES
    float2 d = i.uv - 0.5;
    c *= 1 - dot(d, d) * 0.9;                                           // vinheta
    float g = pow(saturate(c.r), 1 / 2.2);
    return float4(g, g, g, 1);
}";

    // ------------------------------------------------------------------ execução

    public GpuGameResult? Run()
    {
        var oldPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest; // medição de frametime precisa de pontualidade
        IntPtr hwnd = IntPtr.Zero;
        string cls = "ResetExplodeGameBench" + Environment.TickCount64;
        IntPtr hInst = GetModuleHandleW(null);
        var keep = new List<IDisposable>();
        T K<T>(T d) where T : IDisposable { keep.Add(d); return d; }

        try
        {
            // ---- Janela (cabe na tela, mantendo a proporção da resolução de render) ----
            _wndProc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst, hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512), lpszClassName = cls
            };
            RegisterClassExW(ref wc);
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            double scale = Math.Min(1.0, Math.Min(sw * 0.85 / Width, sh * 0.85 / Height));
            var rect = new RECT { Right = (int)(Width * scale), Bottom = (int)(Height * scale) };
            const uint style = 0x00CF0000 | 0x10000000; // WS_OVERLAPPEDWINDOW | WS_VISIBLE
            AdjustWindowRect(ref rect, style, false);
            int ww = rect.Right - rect.Left, wh = rect.Bottom - rect.Top;
            hwnd = CreateWindowExW(0, cls, "RESET BENCH · Cena de jogo", style, (sw - ww) / 2, (sh - wh) / 2, ww, wh,
                                   IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

            // ---- Dispositivo ----
            var factory = K(DXGI.CreateDXGIFactory1<IDXGIFactory2>());
            var adapters = new List<IDXGIAdapter1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
            {
                K(a);
                if ((a.Description1.Flags & AdapterFlags.Software) == 0) adapters.Add(a);
            }
            var adapter = adapters.FirstOrDefault(a => AdapterName.Length > 0 && a.Description1.Description.Contains(AdapterName, StringComparison.OrdinalIgnoreCase))
                          ?? adapters.OrderByDescending(a => a.Description1.DedicatedVideoMemory).First();
            string adapterName = adapter.Description1.Description;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
            K(device); K(ctx);

            var swap = K(factory.CreateSwapChainForHwnd(device, hwnd,
                new SwapChainDescription1((uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                                          Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None)));
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            var backBuffer = K(swap.GetBuffer<ID3D11Texture2D>(0));

            // ---- Alvos: HDR + profundidade, LDR, shadow map ----
            Texture2DDescription Tex(Format f, int w, int h, BindFlags b) =>
                new(f, (uint)w, (uint)h, 1, 1, b, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            var hdrDesc = Tex(Format.R16G16B16A16_Float, Width, Height, BindFlags.RenderTarget | BindFlags.ShaderResource);
            var hdr = K(device.CreateTexture2D(in hdrDesc));
            var hdrRtv = K(device.CreateRenderTargetView(hdr));
            var hdrSrv = K(device.CreateShaderResourceView(hdr));
            var depthDesc = Tex(Format.D32_Float, Width, Height, BindFlags.DepthStencil);
            var depth = K(device.CreateTexture2D(in depthDesc));
            var dsv = K(device.CreateDepthStencilView(depth));
            var ldrDesc = Tex(Format.B8G8R8A8_UNorm, Width, Height, BindFlags.RenderTarget);
            var ldr = K(device.CreateTexture2D(in ldrDesc));
            var ldrRtv = K(device.CreateRenderTargetView(ldr));
            const int ShadowRes = 4096;
            var smDesc = Tex(Format.R32_Typeless, ShadowRes, ShadowRes, BindFlags.DepthStencil | BindFlags.ShaderResource);
            var shadowTex = K(device.CreateTexture2D(in smDesc));
            var shadowDsv = K(device.CreateDepthStencilView(shadowTex, new DepthStencilViewDescription(shadowTex, DepthStencilViewDimension.Texture2D, Format.D32_Float)));
            var shadowSrv = K(device.CreateShaderResourceView(shadowTex, new ShaderResourceViewDescription(shadowTex, ShaderResourceViewDimension.Texture2D, Format.R32_Float)));

            // ---- Shaders ----
            ReadOnlyMemory<byte> C(string entry, string profile) => Compiler.Compile(Hlsl, entry, "scene.hlsl", profile, ShaderFlags.OptimizationLevel3);
            var terrainVsCode = C("TerrainVS", "vs_5_0");
            var terrainVs = K(device.CreateVertexShader(terrainVsCode.Span));
            var treeVs = K(device.CreateVertexShader(C("TreeVS", "vs_5_0").Span));
            var waterVs = K(device.CreateVertexShader(C("WaterVS", "vs_5_0").Span));
            var terrainShadowVs = K(device.CreateVertexShader(C("TerrainShadowVS", "vs_5_0").Span));
            var treeShadowVs = K(device.CreateVertexShader(C("TreeShadowVS", "vs_5_0").Span));
            var fullscreenVs = K(device.CreateVertexShader(C("FullscreenVS", "vs_5_0").Span));
            var terrainPs = K(device.CreatePixelShader(C("TerrainPS", "ps_5_0").Span));
            var treePs = K(device.CreatePixelShader(C("TreePS", "ps_5_0").Span));
            var waterPs = K(device.CreatePixelShader(C("WaterPS", "ps_5_0").Span));
            var skyPs = K(device.CreatePixelShader(C("SkyPS", "ps_5_0").Span));
            var postPs = K(device.CreatePixelShader(C("PostPS", "ps_5_0").Span));
            var layout = K(device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32_Float, 24, 0),
            }, terrainVsCode.Span));

            // ---- Geometria ----
            var (tv, ti) = BuildTerrain();
            var terrainVb = K(device.CreateBuffer(tv, BindFlags.VertexBuffer));
            var terrainIb = K(device.CreateBuffer(ti, BindFlags.IndexBuffer));
            var (treeV, treeI) = BuildTree();
            var treeVb = K(device.CreateBuffer(treeV, BindFlags.VertexBuffer));
            var treeIb = K(device.CreateBuffer(treeI, BindFlags.IndexBuffer));
            var trees = PlaceTrees();
            var treeBuf = K(device.CreateBuffer(trees, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None,
                                                ResourceOptionFlags.BufferStructured, 0, 16));
            var treeSrv = K(device.CreateShaderResourceView(treeBuf));
            float wh2 = TerrainSize / 2;
            var water = new[]
            {
                new Vertex { Pos = new(-wh2, WaterY, -wh2) }, new Vertex { Pos = new(wh2, WaterY, -wh2) },
                new Vertex { Pos = new(-wh2, WaterY, wh2) }, new Vertex { Pos = new(wh2, WaterY, wh2) },
            };
            var waterVb = K(device.CreateBuffer(water, BindFlags.VertexBuffer));
            var waterIb = K(device.CreateBuffer(new uint[] { 0, 2, 1, 1, 2, 3 }, BindFlags.IndexBuffer));

            // ---- Estados ----
            var cb = K(device.CreateBuffer((uint)Marshal.SizeOf<FrameCB>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            var rsMain = K(device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)));
            var rsShadow = K(device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)
            {
                DepthBias = 800, SlopeScaledDepthBias = 2.0f, DepthClipEnable = true
            }));
            var noDepth = K(device.CreateDepthStencilState(DepthStencilDescription.None));
            var cmpSampler = K(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.ComparisonMinMagLinearMipPoint,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.LessEqual, MaxLOD = float.MaxValue,
            }));
            var linSampler = K(device.CreateSamplerState(SamplerDescription.LinearClamp));

            var sunDir = Vector3.Normalize(new Vector3(-0.45f, -0.55f, -0.35f));
            var lightView = Matrix4x4.CreateLookAt(-sunDir * 900, Vector3.Zero, Vector3.UnitY);
            var lightProj = Matrix4x4.CreateOrthographic(900, 900, 1, 1900);
            var lightVP = lightView * lightProj;
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.5f, 2000);
            uint vStride = (uint)Marshal.SizeOf<Vertex>();

            // ---- Loop: fila de 8 frames, apresentação a ~60 Hz (não fica preso ao V-Sync) ----
            const int inFlight = 8;
            var fences = new ID3D11Query[inFlight];
            for (int i = 0; i < inFlight; i++) fences[i] = K(device.CreateQuery(QueryType.Event, QueryFlags.None));
            var frameTimes = new List<double>(20000);
            var clock = Stopwatch.StartNew();
            double lastDone = -1, lastPresent = 0, lastTitle = 0; int titleFrames = 0;
            long frame = 0;
            int frames = 0;

            while (!_stop && clock.Elapsed < Duration)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                if (_stop) break;

                var fence = fences[frame % inFlight];
                if (frame >= inFlight)
                {
                    // Espera ativa (sem ceder o núcleo): ceder a vez pode custar um "tick" de 15,6 ms do Windows
                    // e registraria uma travada que a GPU não teve.
                    while (!ctx.IsDataAvailable(fence) && !_stop) Thread.SpinWait(20);
                    // Momento em que a GPU terminou um frame: intervalo = tempo de frame real da GPU.
                    double done = clock.Elapsed.TotalMilliseconds;
                    if (lastDone >= 0) frameTimes.Add(done - lastDone);
                    lastDone = done;
                    frames++; titleFrames++;
                }

                // Câmera pelo tempo (não pelos frames): a mesma cena aparece no mesmo segundo em qualquer PC.
                float t = (float)(clock.Elapsed.TotalSeconds / Duration.TotalSeconds);
                var p2 = PathAt(t); var ahead = PathAt(t + 0.035f);
                var eye = new Vector3(p2.X, CamHeight(p2), p2.Y);
                var target = new Vector3(ahead.X, CamHeight(ahead) - 22, ahead.Y);
                var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
                var vp = view * proj;
                Matrix4x4.Invert(vp, out var inv);
                var data = new FrameCB { ViewProj = vp, LightViewProj = lightVP, InvViewProj = inv, CamPos = eye,
                                         Time = (float)clock.Elapsed.TotalSeconds, SunDir = sunDir };
                var map = ctx.Map(cb, MapMode.WriteDiscard);
                Marshal.StructureToPtr(data, map.DataPointer, false);
                ctx.Unmap(cb);

                ctx.VSSetConstantBuffer(0, cb); ctx.PSSetConstantBuffer(0, cb);
                ctx.IASetInputLayout(layout);
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                ctx.VSSetShaderResource(1, treeSrv);

                // 1) Mapa de sombras
                ctx.PSSetShaderResource(0, null!);
                ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, shadowDsv);
                ctx.ClearDepthStencilView(shadowDsv, DepthStencilClearFlags.Depth, 1, 0);
                ctx.RSSetViewport(0, 0, ShadowRes, ShadowRes);
                ctx.RSSetState(rsShadow);
                ctx.OMSetDepthStencilState(null);
                ctx.PSSetShader(null);
                ctx.VSSetShader(terrainShadowVs);
                ctx.IASetVertexBuffer(0, terrainVb, vStride); ctx.IASetIndexBuffer(terrainIb, Format.R32_UInt, 0);
                ctx.DrawIndexed((uint)ti.Length, 0, 0);
                ctx.VSSetShader(treeShadowVs);
                ctx.IASetVertexBuffer(0, treeVb, vStride); ctx.IASetIndexBuffer(treeIb, Format.R32_UInt, 0);
                ctx.DrawIndexedInstanced((uint)treeI.Length, (uint)trees.Length, 0, 0, 0);

                // 2) Cena em HDR: céu, terreno, árvores, água
                ctx.OMSetRenderTargets(hdrRtv, dsv);
                ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1, 0);
                ctx.RSSetViewport(0, 0, Width, Height);
                ctx.RSSetState(rsMain);
                ctx.PSSetShaderResource(0, shadowSrv);
                ctx.PSSetSampler(0, cmpSampler); ctx.PSSetSampler(1, linSampler);

                ctx.OMSetDepthStencilState(noDepth);
                ctx.VSSetShader(fullscreenVs); ctx.PSSetShader(skyPs);
                ctx.Draw(3, 0);
                ctx.OMSetDepthStencilState(null);

                ctx.VSSetShader(terrainVs); ctx.PSSetShader(terrainPs);
                ctx.IASetVertexBuffer(0, terrainVb, vStride); ctx.IASetIndexBuffer(terrainIb, Format.R32_UInt, 0);
                ctx.DrawIndexed((uint)ti.Length, 0, 0);

                ctx.VSSetShader(treeVs); ctx.PSSetShader(treePs);
                ctx.IASetVertexBuffer(0, treeVb, vStride); ctx.IASetIndexBuffer(treeIb, Format.R32_UInt, 0);
                ctx.DrawIndexedInstanced((uint)treeI.Length, (uint)trees.Length, 0, 0, 0);

                ctx.VSSetShader(waterVs); ctx.PSSetShader(waterPs);
                ctx.IASetVertexBuffer(0, waterVb, vStride); ctx.IASetIndexBuffer(waterIb, Format.R32_UInt, 0);
                ctx.DrawIndexed(6, 0, 0);

                // 3) Pós-processamento: HDR → LDR (tone mapping + vinheta)
                ctx.OMSetRenderTargets(ldrRtv);
                ctx.OMSetDepthStencilState(noDepth);
                ctx.PSSetShaderResource(0, null!);
                ctx.PSSetShaderResource(2, hdrSrv);
                ctx.VSSetShader(fullscreenVs); ctx.PSSetShader(postPs);
                ctx.Draw(3, 0);
                ctx.PSSetShaderResource(2, null!);

                ctx.End(fence);
                ctx.Flush(); // envia o frame à GPU já; sem isso o driver segura os comandos e a GPU fica com buracos ociosos
                frame++;

                double now = clock.Elapsed.TotalSeconds;
                if (now - lastPresent >= 1 / 60.0)
                {
                    // DoNotWait: se a tela ainda não liberou o buffer, pula esta apresentação em vez de travar a fila da GPU.
                    ctx.CopyResource(backBuffer, ldr);
                    if (swap.Present(0, PresentFlags.DoNotWait).Success) lastPresent = now;
                }
                if (now - lastTitle >= 0.5)
                {
                    CurrentFps = titleFrames / (now - lastTitle);
                    titleFrames = 0; lastTitle = now;
                    Progress = now / Duration.TotalSeconds;
                    SetWindowTextW(hwnd, $"RESET BENCH · Cena de jogo · {Width}x{Height} · {CurrentFps:0} FPS · {Progress:P0}");
                }
            }

            if (_stop) { Cancelled = true; return null; }
            var sorted = frameTimes.OrderBy(x => x).ToArray();
            double p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
            double secs = frameTimes.Sum() / 1000;
            return new GpuGameResult(secs > 0 ? frameTimes.Count / secs : 0, p99 > 0 ? 1000 / p99 : 0,
                                     frames, clock.Elapsed.TotalSeconds, Width, Height, adapterName);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            for (int i = keep.Count - 1; i >= 0; i--) try { keep[i].Dispose(); } catch { }
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClassW(cls, hInst);
            Thread.CurrentThread.Priority = oldPriority;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_SYSCOMMAND = 0x0112;
        if (msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == 0xF100) return IntPtr.Zero; // ALT não pausa
        if (msg == WM_CLOSE || (msg == WM_KEYDOWN && wParam == (IntPtr)0x1B)) { _stop = true; return IntPtr.Zero; }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ Win32

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string text);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr hInst, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
