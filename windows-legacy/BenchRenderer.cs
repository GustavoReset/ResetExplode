using System.Diagnostics;
using System.Numerics;

namespace ResetExplode;

public sealed record BenchResult(int Threads, double Seconds, long Samples, int Passes)
{
    /// <summary>Pontos RESET BENCH: amostras de path tracing por segundo ÷ 10.000. Mesma cena e mesma
    /// carga em todo teste, então a nota é comparável entre execuções e entre máquinas (nesta versão do app).</summary>
    public double Score => Seconds > 0 ? Samples / Seconds / 10_000 : 0;
}

/// <summary>
/// Renderizador de benchmark no modelo do Cinebench: path tracer de CPU, imagem dividida em blocos (buckets)
/// distribuídos entre threads a partir do centro. Monocromático (combina com o app) e determinístico: cada
/// pixel tem semente fixa, então a imagem e o volume de trabalho são idênticos em toda execução.
/// </summary>
public sealed class BenchRenderer
{
    public const int Width = 960, Height = 540, BucketSize = 32;
    public const int Spp = 1024;   // amostras por pixel
    private const int MaxDepth = 6;

    public int[] Pixels { get; } = new int[Width * Height];         // BGRA32, lido pela interface
    public IReadOnlyList<(int X, int Y, int W, int H)> Buckets { get; }
    public int[] Active { get; private set; } = Array.Empty<int>(); // bloco de cada thread (-1 = livre)
    public long SamplesDone => Interlocked.Read(ref _samples);
    public int BucketsDone => Volatile.Read(ref _bucketsDone);
    public int BucketsTotal { get; private set; }

    private long _samples;
    private int _bucketsDone;

    public BenchRenderer()
    {
        var list = new List<(int, int, int, int)>();
        for (int y = 0; y < Height; y += BucketSize)
            for (int x = 0; x < Width; x += BucketSize)
                list.Add((x, y, Math.Min(BucketSize, Width - x), Math.Min(BucketSize, Height - y)));
        // Do centro para fora, como o Cinebench.
        Buckets = list.OrderBy(b => Math.Pow(b.Item1 + b.Item3 / 2.0 - Width / 2.0, 2) +
                                    Math.Pow((b.Item2 + b.Item4 / 2.0 - Height / 2.0) * 1.6, 2)).ToList();
    }

    /// <summary>
    /// Renderiza a imagem inteira com N threads (multi-core e single-core fazem o mesmo trabalho, como no Cinebench).
    /// Com minDuration, repete passadas até completar o tempo (desempenho sustentado).
    /// </summary>
    public BenchResult Run(int threads, TimeSpan minDuration, CancellationToken ct, Action? onPass = null)
    {
        int count = Buckets.Count;
        BucketsTotal = count;
        _samples = 0;
        int passes = 0;
        var sw = Stopwatch.StartNew();

        do
        {
            Array.Fill(Pixels, unchecked((int)0xFF000000));
            _bucketsDone = 0;
            int next = -1;
            Active = Enumerable.Repeat(-1, threads).ToArray();
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                int ti = t;
                workers[t] = new Thread(() =>
                {
                    int b;
                    while (!ct.IsCancellationRequested && (b = Interlocked.Increment(ref next)) < count)
                    {
                        Active[ti] = b;
                        RenderBucket(Buckets[b], ct);
                        Interlocked.Increment(ref _bucketsDone);
                    }
                    Active[ti] = -1;
                }) { IsBackground = true, Name = $"RESET BENCH {ti}" };
                workers[t].Start();
            }
            foreach (var w in workers) w.Join();
            if (ct.IsCancellationRequested) break;
            passes++;
            onPass?.Invoke();
        } while (sw.Elapsed < minDuration);

        return new BenchResult(threads, sw.Elapsed.TotalSeconds, SamplesDone, passes);
    }

    private void RenderBucket((int X, int Y, int W, int H) b, CancellationToken ct)
    {
        for (int y = b.Y; y < b.Y + b.H; y++)
        {
            if (ct.IsCancellationRequested) return;
            for (int x = b.X; x < b.X + b.W; x++)
            {
                uint seed = (uint)(y * Width + x) * 9781u + 0x9E3779B9u;
                float sum = 0;
                for (int s = 0; s < Spp; s++)
                {
                    float u = (x + Rnd(ref seed)) / Width, v = (y + Rnd(ref seed)) / Height;
                    sum += MathF.Min(Trace(Camera.Ray(u, v), ref seed), 4f); // limita "fireflies"
                }
                Pixels[y * Width + x] = ToPixel(sum / Spp);
            }
            Interlocked.Add(ref _samples, (long)b.W * Spp);
        }
    }

    // ------------------------------------------------------------------ cena

    private enum Mat : byte { Diffuse, Metal, Glass, Light }

    private readonly record struct Sphere(Vector3 C, float R, Mat M, float Albedo, float Rough = 0);

    private static readonly Sphere[] Spheres = BuildScene();
    private static readonly Sphere Lamp = new(new Vector3(-4f, 7f, -3f), 2.2f, Mat.Light, 0);
    private const float LampPower = 9f;

    private static Sphere[] BuildScene()
    {
        var s = new List<Sphere>
        {
            new(new Vector3(0, 1.1f, 0), 1.1f, Mat.Glass, 0.98f),
            new(new Vector3(-2.5f, 0.9f, 0.9f), 0.9f, Mat.Metal, 0.92f, 0.03f),
            new(new Vector3(2.4f, 0.8f, 0.7f), 0.8f, Mat.Diffuse, 0.85f),
            new(new Vector3(1.0f, 0.45f, -1.9f), 0.45f, Mat.Metal, 0.85f, 0.25f),
            new(new Vector3(-1.1f, 0.35f, -2.0f), 0.35f, Mat.Diffuse, 0.15f),
        };
        // Anel de esferas pequenas alternando materiais.
        for (int i = 0; i < 14; i++)
        {
            float a = i / 14f * MathF.Tau;
            var m = (i % 3) switch { 0 => Mat.Metal, 1 => Mat.Diffuse, _ => Mat.Glass };
            s.Add(new(new Vector3(MathF.Cos(a) * 4.2f, 0.28f, MathF.Sin(a) * 4.2f + 1.5f), 0.28f, m, i % 2 == 0 ? 0.9f : 0.3f, 0.1f));
        }
        return s.ToArray();
    }

    private static class Camera
    {
        private static readonly Vector3 Origin = new(0, 2.1f, -7.2f);
        private static readonly Vector3 Lower, Horizontal, Vertical;
        static Camera()
        {
            var target = new Vector3(0, 0.75f, 0);
            float vfov = 38f * MathF.PI / 180, aspect = (float)Width / Height;
            float hh = MathF.Tan(vfov / 2), hw = aspect * hh;
            var w = Vector3.Normalize(Origin - target);
            var u = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, w));
            var v = Vector3.Cross(w, u);
            Lower = Origin - hw * u + hh * v - w;   // canto superior esquerdo (y cresce para baixo na imagem)
            Horizontal = 2 * hw * u;
            Vertical = -2 * hh * v;
        }
        public static (Vector3 O, Vector3 D) Ray(float s, float t)
            => (Origin, Vector3.Normalize(Lower + s * Horizontal + t * Vertical - Origin));
    }

    // ------------------------------------------------------------------ path tracing

    private static float Trace((Vector3 O, Vector3 D) ray, ref uint seed)
    {
        float radiance = 0, throughput = 1;
        bool specularBounce = true; // câmera e reflexos especulares "veem" a lâmpada diretamente
        var (o, d) = ray;

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (!Intersect(o, d, out float t, out int idx, out bool lamp))
            {
                radiance += throughput * Sky(d);
                break;
            }
            if (lamp)
            {
                if (specularBounce) radiance += throughput * LampPower;
                break;
            }

            var p = o + d * t;
            Vector3 n; Mat mat; float albedo, rough = 0;
            if (idx < 0)
            {
                // Chão: xadrez suave em tons de cinza
                n = Vector3.UnitY; mat = Mat.Diffuse;
                bool check = ((int)MathF.Floor(p.X * 0.9f) + (int)MathF.Floor(p.Z * 0.9f) & 1) == 0;
                albedo = check ? 0.62f : 0.38f;
            }
            else
            {
                var sp = Spheres[idx];
                n = (p - sp.C) / sp.R; mat = sp.M; albedo = sp.Albedo; rough = sp.Rough;
            }

            switch (mat)
            {
                case Mat.Diffuse:
                {
                    // Luz direta da lâmpada (amostragem por cone) + rebote difuso cosseno-ponderado.
                    radiance += throughput * albedo * DirectLight(p, n, ref seed);
                    throughput *= albedo;
                    d = CosineHemisphere(n, ref seed);
                    o = p + n * 1e-3f;
                    specularBounce = false;
                    break;
                }
                case Mat.Metal:
                {
                    var r = Vector3.Reflect(d, n) + rough * RandomInSphere(ref seed);
                    if (Vector3.Dot(r, n) <= 0) return radiance;
                    d = Vector3.Normalize(r);
                    o = p + n * 1e-3f;
                    throughput *= albedo;
                    specularBounce = true;
                    break;
                }
                case Mat.Glass:
                {
                    const float ior = 1.5f;
                    bool outside = Vector3.Dot(d, n) < 0;
                    var nn = outside ? n : -n;
                    float eta = outside ? 1 / ior : ior;
                    float cos = MathF.Min(-Vector3.Dot(d, nn), 1);
                    float sin2 = eta * eta * (1 - cos * cos);
                    float r0 = (1 - ior) / (1 + ior); r0 *= r0;
                    float fresnel = r0 + (1 - r0) * MathF.Pow(1 - cos, 5);
                    if (sin2 > 1 || Rnd(ref seed) < fresnel)
                    {
                        d = Vector3.Reflect(d, nn);
                        o = p + nn * 1e-3f;
                    }
                    else
                    {
                        d = Vector3.Normalize(eta * d + (eta * cos - MathF.Sqrt(1 - sin2)) * nn);
                        o = p - nn * 1e-3f;
                    }
                    throughput *= albedo;
                    specularBounce = true;
                    break;
                }
            }

            // Roleta russa depois de alguns rebotes.
            if (depth >= 3)
            {
                float q = MathF.Max(0.05f, 1 - throughput);
                if (Rnd(ref seed) < q) break;
                throughput /= 1 - q;
            }
        }
        return radiance;
    }

    /// <summary>Amostra direção dentro do cone que a lâmpada esférica ocupa e testa sombra.</summary>
    private static float DirectLight(Vector3 p, Vector3 n, ref uint seed)
    {
        var toC = Lamp.C - p;
        float dist2 = toC.LengthSquared();
        float cosMax = MathF.Sqrt(MathF.Max(0, 1 - Lamp.R * Lamp.R / dist2));
        var w = toC / MathF.Sqrt(dist2);

        float r1 = Rnd(ref seed), r2 = Rnd(ref seed);
        float cosT = 1 - r1 * (1 - cosMax), sinT = MathF.Sqrt(1 - cosT * cosT), phi = MathF.Tau * r2;
        Basis(w, out var a, out var b);
        var dir = Vector3.Normalize(a * (MathF.Cos(phi) * sinT) + b * (MathF.Sin(phi) * sinT) + w * cosT);

        float cosN = Vector3.Dot(dir, n);
        if (cosN <= 0) return 0;
        if (!Intersect(p + n * 1e-3f, dir, out _, out _, out bool hitLamp) || !hitLamp) return 0;
        // Lambert (1/π) × radiância × cos × ângulo sólido do cone
        return LampPower * cosN * 2 * (1 - cosMax);
    }

    private static bool Intersect(Vector3 o, Vector3 d, out float tMin, out int idx, out bool lamp)
    {
        tMin = float.MaxValue; idx = -2; lamp = false;
        // Chão (plano y = 0)
        if (d.Y < -1e-6f)
        {
            float t = -o.Y / d.Y;
            if (t > 1e-4f && t < 60) { tMin = t; idx = -1; }
        }
        for (int i = 0; i < Spheres.Length; i++)
            if (HitSphere(Spheres[i], o, d, tMin, out float t)) { tMin = t; idx = i; }
        if (HitSphere(Lamp, o, d, tMin, out float tl)) { tMin = tl; lamp = true; }
        return idx != -2 || lamp;
    }

    private static bool HitSphere(in Sphere s, Vector3 o, Vector3 d, float tMax, out float t)
    {
        var oc = o - s.C;
        float b = Vector3.Dot(oc, d), c = oc.LengthSquared() - s.R * s.R;
        float disc = b * b - c;
        t = 0;
        if (disc < 0) return false;
        float sq = MathF.Sqrt(disc);
        t = -b - sq;
        if (t < 1e-4f) t = -b + sq;
        return t > 1e-4f && t < tMax;
    }

    private static float Sky(Vector3 d) => 0.06f + 0.22f * MathF.Max(0, d.Y);

    // ------------------------------------------------------------------ utilidades

    private static float Rnd(ref uint s)
    {
        // xorshift32: rápido e determinístico por pixel
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s >> 8) * (1f / 16777216f);
    }

    private static Vector3 RandomInSphere(ref uint s)
    {
        while (true)
        {
            var v = new Vector3(Rnd(ref s) * 2 - 1, Rnd(ref s) * 2 - 1, Rnd(ref s) * 2 - 1);
            if (v.LengthSquared() < 1) return v;
        }
    }

    private static Vector3 CosineHemisphere(Vector3 n, ref uint s)
    {
        float r1 = Rnd(ref s), r2 = Rnd(ref s);
        float r = MathF.Sqrt(r1), phi = MathF.Tau * r2;
        Basis(n, out var a, out var b);
        return Vector3.Normalize(a * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(1 - r1));
    }

    private static void Basis(Vector3 n, out Vector3 a, out Vector3 b)
    {
        a = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX, n));
        b = Vector3.Cross(n, a);
    }

    /// <summary>Tone mapping (ACES aproximado) + gama 2.2 → cinza BGRA.</summary>
    private static int ToPixel(float v)
    {
        v *= 1.1f;
        v = v * (2.51f * v + 0.03f) / (v * (2.43f * v + 0.59f) + 0.14f);
        int g = (int)(MathF.Pow(Math.Clamp(v, 0, 1), 1 / 2.2f) * 255 + 0.5f);
        return unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g;
    }
}
