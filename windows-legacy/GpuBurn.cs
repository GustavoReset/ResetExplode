using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ResetExplode;

public sealed class GpuBurnSettings
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool Fullscreen { get; init; }
    public int Msaa { get; init; } = 1;        // 1 = sem MSAA
    public int Shells { get; init; } = 96;     // camadas de pelo (overdraw)
    public int Iterations { get; init; }       // carga extra de ALU no pixel shader
    public string AdapterName { get; init; } = "";
}

/// <summary>
/// Stress de GPU no estilo FurMark: um torus com pelos renderizado por "shells" (a malha é desenhada
/// N vezes, cada camada inflada ao longo da normal, com pixels descartados para formar os fios).
/// Gera overdraw massivo + shader pesado, sem limite de FPS, numa janela Win32 própria.
/// </summary>
public sealed class GpuBurn
{
    private Thread? _thread;
    private volatile bool _stop;
    private long _frames;
    private double _seconds;
    private WndProc? _wndProc; // mantido vivo enquanto a janela existir

    public bool Running => _thread?.IsAlive == true;
    public double Fps { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>Frames efetivamente exibidos na janela (~60/s; o resto é renderizado fora da tela).</summary>
    public long Presents => Interlocked.Read(ref _presents);
    private long _presents;
    public double AverageFps => _seconds > 0 ? Frames / _seconds : 0;
    public string? Error { get; private set; }
    public string AdapterUsed { get; private set; } = "";
    public bool ClosedByUser { get; private set; }

    public void Start(GpuBurnSettings s)
    {
        if (Running) return;
        _stop = false; _frames = 0; _seconds = 0; Fps = 0; Error = null; ClosedByUser = false;
        _thread = new Thread(() => Run(s)) { IsBackground = true, Name = "GPU burn" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    // ------------------------------------------------------------------ renderização

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector3 Pos; public Vector3 Normal; public Vector2 Uv; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 World, ViewProj;
        public float Time, ShellCount, FurLength, Iterations;
    }

    private const string Hlsl = @"
cbuffer CB : register(b0)
{
    row_major float4x4 World;
    row_major float4x4 ViewProj;
    float Time; float ShellCount; float FurLength; float Iterations;
};

struct VSIn  { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; uint inst : SV_InstanceID; };
struct PSIn  { float4 pos : SV_POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; float shell : TEXCOORD1; };

PSIn VS(VSIn i)
{
    PSIn o;
    float s = i.inst / ShellCount;                       // 0 = raiz, 1 = ponta
    float3 p = i.pos + i.nrm * (FurLength * s);
    float3 w = mul(float4(p, 1), World).xyz;
    w.y -= 0.10 * s * s;                                  // gravidade
    w   += 0.025 * s * sin(Time * 2.7 + i.pos.yzx * 7.0); // vento
    o.pos = mul(float4(w, 1), ViewProj);
    o.nrm = normalize(mul(float4(i.nrm, 0), World).xyz);
    o.uv = i.uv;
    o.shell = s;
    return o;
}

float hash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float4 PS(PSIn i) : SV_TARGET
{
    float h = hash(floor(i.uv * float2(900, 380)));

    // Carga de ALU: cadeia de FMAs vetoriais (o que mais consome energia na GPU), antes do descarte
    // e ligada a ele, para o compilador não poder pular o cálculo.
    float4 a = float4(h, i.uv, i.shell), b = a.wzyx + 0.5;
    [loop] for (int k = 0; k < (int)Iterations; k++)
    {
        a = mad(a, 1.0001, b * 0.0003);
        b = mad(b, 0.9997, a * 0.0002);
    }
    float acc = frac(dot(a, b));

    if (i.shell > 0 && h + acc * 1e-6 < i.shell) discard; // fios afinam até a ponta

    float3 L = normalize(float3(-0.4, 0.8, -0.5));
    float diff = saturate(dot(i.nrm, L)) * 0.75 + 0.25;
    float ao = lerp(0.18, 1.0, i.shell);                  // raiz escura, ponta clara
    float c = diff * ao * (0.9 + 0.1 * acc);
    return float4(c, c, c, lerp(1.0, 0.55, i.shell));     // pontas translúcidas
}";

    private void Run(GpuBurnSettings s)
    {
        IntPtr hwnd = IntPtr.Zero;
        string cls = "ResetExplodeGpuBurn" + Environment.TickCount64;
        IntPtr hInst = GetModuleHandleW(null);
        var disposables = new List<IDisposable>();
        T Keep<T>(T d) where T : IDisposable { disposables.Add(d); return d; }

        ID3D11Texture2D? backBuffer = null, colorMs = null, depthTex = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11DepthStencilView? dsv = null;
        void ReleaseTargets()
        {
            rtv?.Dispose(); dsv?.Dispose(); depthTex?.Dispose(); colorMs?.Dispose(); backBuffer?.Dispose();
            rtv = null; dsv = null; depthTex = null; colorMs = null; backBuffer = null;
        }

        try
        {
            // ---- Janela ----
            _wndProc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
                lpszClassName = cls
            };
            RegisterClassExW(ref wc);

            int x, y, w, h;
            uint style;
            if (s.Fullscreen)
            {
                style = WS_POPUP | WS_VISIBLE;
                x = 0; y = 0; w = GetSystemMetrics(0); h = GetSystemMetrics(1);
            }
            else
            {
                style = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
                var r = new RECT { Right = s.Width, Bottom = s.Height };
                AdjustWindowRect(ref r, style, false);
                w = r.Right - r.Left; h = r.Bottom - r.Top;
                x = Math.Max(0, (GetSystemMetrics(0) - w) / 2);
                y = Math.Max(0, (GetSystemMetrics(1) - h) / 2);
            }
            hwnd = CreateWindowExW(0, cls, "RESET EXPLODE · GPU burn", style, x, y, w, h,
                                   IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Não foi possível criar a janela de renderização.");

            // ---- Dispositivo na GPU escolhida (por nome; senão a com mais VRAM) ----
            var factory = Keep(DXGI.CreateDXGIFactory1<IDXGIFactory2>());
            var candidates = new List<IDXGIAdapter1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
            {
                Keep(a);
                if ((a.Description1.Flags & AdapterFlags.Software) == 0) candidates.Add(a);
            }
            var adapter = candidates.FirstOrDefault(a => s.AdapterName.Length > 0 &&
                              a.Description1.Description.Contains(s.AdapterName, StringComparison.OrdinalIgnoreCase))
                          ?? candidates.OrderByDescending(a => a.Description1.DedicatedVideoMemory).FirstOrDefault()
                          ?? throw new InvalidOperationException("Nenhuma GPU compatível com Direct3D 11.");
            AdapterUsed = adapter.Description1.Description;

            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                                    out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
            Keep(device); Keep(ctx);

            int msaa = s.Msaa;
            while (msaa > 1 && device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, (uint)msaa) == 0) msaa /= 2;

            bool tearing;
            using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) tearing = f5?.PresentAllowTearing ?? false;
            var scFlags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None;

            GetClientRect(hwnd, out var rc);
            int cw = Math.Max(1, rc.Right), ch = Math.Max(1, rc.Bottom);
            var swap = Keep(factory.CreateSwapChainForHwnd(device, hwnd,
                new SwapChainDescription1((uint)cw, (uint)ch, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                                          Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, scFlags)));
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            // Renderiza sempre numa textura própria; o back buffer só recebe uma cópia na hora de apresentar.
            void CreateTargets(int tw, int th)
            {
                backBuffer = swap.GetBuffer<ID3D11Texture2D>(0);
                var cd = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)tw, (uint)th, 1, 1, BindFlags.RenderTarget,
                                                  ResourceUsage.Default, CpuAccessFlags.None, (uint)msaa, 0, ResourceOptionFlags.None);
                colorMs = device.CreateTexture2D(in cd);
                rtv = device.CreateRenderTargetView(colorMs);
                var dd = new Texture2DDescription(Format.D32_Float, (uint)tw, (uint)th, 1, 1, BindFlags.DepthStencil,
                                                  ResourceUsage.Default, CpuAccessFlags.None, (uint)msaa, 0, ResourceOptionFlags.None);
                depthTex = device.CreateTexture2D(in dd);
                dsv = device.CreateDepthStencilView(depthTex);
            }
            CreateTargets(cw, ch);

            // ---- Shaders, malha, estados ----
            var vsCode = Compiler.Compile(Hlsl, "VS", "fur.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3);
            var psCode = Compiler.Compile(Hlsl, "PS", "fur.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3);
            var vs = Keep(device.CreateVertexShader(vsCode.Span));
            var ps = Keep(device.CreatePixelShader(psCode.Span));
            var layout = Keep(device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            }, vsCode.Span));

            BuildTorus(out var vertices, out var indices);
            var vb = Keep(device.CreateBuffer(vertices, BindFlags.VertexBuffer));
            var ib = Keep(device.CreateBuffer(indices, BindFlags.IndexBuffer));
            var cb = Keep(device.CreateBuffer((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer,
                                              ResourceUsage.Dynamic, CpuAccessFlags.Write));
            var raster = Keep(device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)
            {
                MultisampleEnable = msaa > 1
            }));

            // Blending nas camadas: soma carga de ROP e banda de memória (como a pelagem translúcida do FurMark).
            var blendDesc = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha);
            var blend = Keep(device.CreateBlendState(blendDesc));

            // ---- Loop ----
            // A apresentação sincroniza com o monitor e deixaria a GPU ociosa. Por isso renderiza sem parar
            // fora da tela, mantendo até 6 frames na fila (queries de evento), e apresenta só a ~60 Hz.
            const int inFlight = 6;
            var fences = new ID3D11Query[inFlight];
            for (int i = 0; i < inFlight; i++) fences[i] = Keep(device.CreateQuery(QueryType.Event, QueryFlags.None));
            long frame = 0;

            var clock = Stopwatch.StartNew();
            double lastT = 0, lastPresent = 0; long lastFrames = 0;
            while (!_stop)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                if (_stop) break;

                GetClientRect(hwnd, out rc);
                if (rc.Right <= 0 || rc.Bottom <= 0) { Thread.Sleep(50); continue; } // minimizada
                if (rc.Right != cw || rc.Bottom != ch)
                {
                    cw = rc.Right; ch = rc.Bottom;
                    ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
                    ReleaseTargets();
                    swap.ResizeBuffers(2, (uint)cw, (uint)ch, Format.B8G8R8A8_UNorm, scFlags).CheckError();
                    CreateTargets(cw, ch);
                }

                var fence = fences[frame % inFlight];
                if (frame >= inFlight)
                    while (!ctx.IsDataAvailable(fence) && !_stop) Thread.Yield();

                float t = (float)clock.Elapsed.TotalSeconds;
                var world = Matrix4x4.CreateRotationY(t * 0.6f) * Matrix4x4.CreateRotationX(0.55f + 0.25f * MathF.Sin(t * 0.35f));
                var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3.5f), Vector3.Zero, Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, cw / (float)ch, 0.1f, 50f);
                var c = new Constants
                {
                    World = world, ViewProj = view * proj, Time = t,
                    ShellCount = s.Shells, FurLength = 0.2f, Iterations = s.Iterations
                };
                var map = ctx.Map(cb, MapMode.WriteDiscard);
                Marshal.StructureToPtr(c, map.DataPointer, false);
                ctx.Unmap(cb);

                ctx.OMSetRenderTargets(rtv!, dsv);
                ctx.ClearRenderTargetView(rtv!, new Color4(0, 0, 0, 1));
                ctx.ClearDepthStencilView(dsv!, DepthStencilClearFlags.Depth, 1f, 0);
                ctx.RSSetViewport(0, 0, cw, ch);
                ctx.RSSetState(raster);
                ctx.OMSetBlendState(blend);
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                ctx.IASetInputLayout(layout);
                ctx.IASetVertexBuffer(0, vb, (uint)Marshal.SizeOf<Vertex>());
                ctx.IASetIndexBuffer(ib, Format.R32_UInt, 0);
                ctx.VSSetShader(vs);
                ctx.PSSetShader(ps);
                ctx.VSSetConstantBuffer(0, cb);
                ctx.PSSetConstantBuffer(0, cb);
                ctx.DrawIndexedInstanced((uint)indices.Length, (uint)s.Shells, 0, 0, 0);
                ctx.End(fence);
                frame++;
                Interlocked.Increment(ref _frames);

                double now = clock.Elapsed.TotalSeconds;
                if (now - lastPresent >= 1 / 60.0)
                {
                    if (msaa > 1) ctx.ResolveSubresource(backBuffer!, 0, colorMs!, 0, Format.B8G8R8A8_UNorm);
                    else ctx.CopyResource(backBuffer!, colorMs!);
                    swap.Present(0, tearing ? PresentFlags.AllowTearing : PresentFlags.None);
                    Interlocked.Increment(ref _presents);
                    lastPresent = now;
                }
                _seconds = now;
                if (now - lastT >= 0.5)
                {
                    long f = Frames;
                    Fps = (f - lastFrames) / (now - lastT);
                    lastT = now; lastFrames = f;
                    SetWindowTextW(hwnd, $"RESET EXPLODE · GPU burn · {Fps:0} FPS · {cw}x{ch}" +
                                         (msaa > 1 ? $" · MSAA {msaa}x" : "") + $" · {AdapterUsed}");
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            ReleaseTargets();
            for (int i = disposables.Count - 1; i >= 0; i--)
                try { disposables[i].Dispose(); } catch { }
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClassW(cls, hInst);
            Fps = 0;
        }
    }

    private static void BuildTorus(out Vertex[] vertices, out uint[] indices)
    {
        const int seg = 160, sides = 64;
        const float R = 1f, r = 0.42f;
        vertices = new Vertex[(seg + 1) * (sides + 1)];
        for (int i = 0, k = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            for (int j = 0; j <= sides; j++, k++)
            {
                float v = j / (float)sides * MathF.Tau;
                var n = new Vector3(MathF.Cos(u) * MathF.Cos(v), MathF.Sin(v), MathF.Sin(u) * MathF.Cos(v));
                vertices[k] = new Vertex
                {
                    Pos = new Vector3(MathF.Cos(u) * R, 0, MathF.Sin(u) * R) + n * r,
                    Normal = n,
                    Uv = new Vector2(i / (float)seg, j / (float)sides)
                };
            }
        }
        indices = new uint[seg * sides * 6];
        for (int i = 0, k = 0; i < seg; i++)
            for (int j = 0; j < sides; j++)
            {
                uint a = (uint)(i * (sides + 1) + j), b = (uint)((i + 1) * (sides + 1) + j);
                indices[k++] = a; indices[k++] = b; indices[k++] = a + 1;
                indices[k++] = a + 1; indices[k++] = b; indices[k++] = b + 1;
            }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_SYSCOMMAND = 0x0112;
        // ALT sozinho abriria o "modo menu" da janela, um loop modal que pausaria a renderização.
        if (msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == 0xF100) return IntPtr.Zero; // SC_KEYMENU
        if (msg == WM_CLOSE || (msg == WM_KEYDOWN && wParam == (IntPtr)0x1B)) // fechar ou ESC
        {
            ClosedByUser = true;
            _stop = true;
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ Win32

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
                                                 IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string text);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr hInst, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
