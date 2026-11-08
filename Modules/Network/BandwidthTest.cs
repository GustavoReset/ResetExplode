using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace FortKnox;

/// <summary>
/// Resultado de uma medição de banda (Mbps).
/// <b>Sustentado</b> descarta a fase de aceleração do TCP (primeiros ~2 s) e é o número que melhor representa o link;
/// <b>média</b> inclui a aceleração (e por isso subestima); <b>pico</b> é a melhor janela de 1 s.
/// </summary>
public sealed record BandwidthResult(double Average, double Sustained, double Peak, long Bytes, double Seconds)
{
    public string Text => $"{Sustained:0.#} Mbps sustentado · pico {Peak:0.#} · média {Average:0.#}";
}

/// <summary>Resultado + o motivo da falha (servidor bloqueado, sem internet…) quando não houve dado.</summary>
public sealed record BandwidthOutcome(BandwidthResult? Result, string? Error);

/// <summary>
/// Fase de "ping" do teste, como no Speedtest da Ookla: várias requisições curtas na MESMA conexão (sem refazer TLS).
/// <b>Min</b> é o ping que se divulga; <b>Jitter</b> é a média da diferença entre medidas consecutivas.
/// <see cref="Server"/> é o datacenter que respondeu (Cloudflare: sufixo do cabeçalho cf-ray, ex.: GRU = São Paulo).
/// </summary>
public sealed record LatencyResult(double Min, double Median, double Jitter, int Samples, string? Server)
{
    public string Text => $"mín {Min:0.#} ms · mediana {Median:0.#} · jitter {Jitter:0.#} ms";
}

/// <summary>
/// Testes de largura de banda por HTTP (padrão: servidor público speed.cloudflare.com), com leitura ao vivo a cada 250 ms.
/// Várias conexões em paralelo para saturar o link. Cancelável; nada é gravado em disco.
/// </summary>
public static class BandwidthTest
{
    public const string DefaultDownloadUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
    public const string DefaultUploadUrl = "https://speed.cloudflare.com/__up";

    public const string DefaultPingUrl = "https://speed.cloudflare.com/__down?bytes=0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const int SampleMs = 250;

    /// <summary>
    /// Mede latência HTTP: 1 requisição de aquecimento (abre a conexão e o TLS, não conta) e depois <paramref name="samples"/>
    /// medidas sequenciais. Null se o servidor não responder (a falha de rede aparece no download).
    /// </summary>
    public static async Task<LatencyResult?> LatencyAsync(CancellationToken ct, string url = DefaultPingUrl, int samples = 10)
    {
        var times = new List<double>();
        string? server = null;
        try
        {
            for (int i = 0; i <= samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await resp.Content.ReadAsByteArrayAsync(ct);
                sw.Stop();
                if (i == 0) { server = ColoFrom(resp); continue; } // aquecimento
                times.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(80, ct);
            }
        }
        // falha de rede ou tempo esgotado: sem medida (o download mostra o motivo). Cancelamento do usuário continua propagando.
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) { }
        return SummarizeLatency(times, server);
    }

    /// <summary>"cf-ray: 8f2a…-GRU" → "GRU". Qualquer outro servidor: null.</summary>
    internal static string? ColoFrom(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("cf-ray", out var v)) return null;
        string ray = v.FirstOrDefault() ?? "";
        int dash = ray.LastIndexOf('-');
        return dash >= 0 && dash < ray.Length - 1 ? ray[(dash + 1)..].ToUpperInvariant() : null;
    }

    /// <summary>Função pura (testável): medidas em ms → mínimo, mediana e jitter. Null sem medidas.</summary>
    internal static LatencyResult? SummarizeLatency(IReadOnlyList<double> ms, string? server = null)
    {
        if (ms.Count == 0) return null;
        var sorted = ms.OrderBy(x => x).ToList();
        double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double jitter = 0;
        for (int i = 1; i < ms.Count; i++) jitter += Math.Abs(ms[i] - ms[i - 1]);
        jitter = ms.Count > 1 ? jitter / (ms.Count - 1) : 0;
        return new LatencyResult(sorted[0], median, jitter, ms.Count, server);
    }

    public static Task<BandwidthOutcome> DownloadAsync(int seconds, Action<double>? live, CancellationToken ct,
        string url = DefaultDownloadUrl, int connections = 4)
        => MeasureAsync(seconds, live, ct, connections, async (add, token) =>
        {
            var buf = new byte[81920];
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            using var st = await resp.Content.ReadAsStreamAsync(token);
            int n;
            while ((n = await st.ReadAsync(buf, token)) > 0) add(n);
        });

    public static Task<BandwidthOutcome> UploadAsync(int seconds, Action<double>? live, CancellationToken ct,
        string url = DefaultUploadUrl, int connections = 3)
    {
        var payload = new byte[1_000_000];
        new Random(1).NextBytes(payload);
        return MeasureAsync(seconds, live, ct, connections, async (add, token) =>
        {
            using var resp = await Http.PostAsync(url, new ByteArrayContent(payload), token);
            resp.EnsureSuccessStatusCode();
            add(payload.Length);
        });
    }

    /// <summary>Roda <paramref name="oneRequest"/> em loop em N conexões até o tempo acabar, amostrando o total de bytes a cada 250 ms.</summary>
    private static async Task<BandwidthOutcome> MeasureAsync(int seconds, Action<double>? live, CancellationToken ct, int connections,
        Func<Action<int>, CancellationToken, Task> oneRequest)
    {
        long total = 0;
        string? error = null;
        var samples = new List<(double T, long Bytes)> { (0, 0) };
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(seconds * 1000);
        void Add(int n) => Interlocked.Add(ref total, n);

        var workers = Enumerable.Range(0, connections).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested) await oneRequest(Add, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Interlocked.CompareExchange(ref error, ex.InnerException?.Message ?? ex.Message, null);
            }
        })).ToArray();

        var sampler = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(SampleMs, cts.Token);
                    var now = (sw.Elapsed.TotalSeconds, Interlocked.Read(ref total));
                    samples.Add(now);
                    live?.Invoke(TrailingMbps(samples));
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.WhenAll(workers);
        cts.Cancel(); // se todos os trabalhadores falharam cedo, para o amostrador também
        await sampler;
        ct.ThrowIfCancellationRequested(); // cancelamento do usuário não vira resultado parcial

        samples.Add((sw.Elapsed.TotalSeconds, Interlocked.Read(ref total)));
        var result = Summarize(samples);
        // Conexão pendurada (firewall que descarta em vez de recusar) esgota o tempo sem nenhuma exceção: sem isto o usuário veria "—" sem motivo.
        if (result == null) error ??= "o servidor não respondeu a tempo (bloqueado por firewall/proxy ou sem internet)";
        return new BandwidthOutcome(result, error);
    }

    /// <summary>Velocidade na última janela de ~1 s (para o número que sobe e desce na tela).</summary>
    internal static double TrailingMbps(IReadOnlyList<(double T, long Bytes)> s)
    {
        var last = s[^1];
        int i = s.Count - 1;
        while (i > 0 && last.T - s[i].T < 1.0) i--;
        double dt = last.T - s[i].T;
        return dt <= 0 ? 0 : (last.Bytes - s[i].Bytes) * 8 / 1e6 / dt;
    }

    /// <summary>Função pura (testável): amostras acumuladas (tempo em s, bytes) → média, sustentado e pico. Null se nada foi transferido.</summary>
    internal static BandwidthResult? Summarize(IReadOnlyList<(double T, long Bytes)> s)
    {
        if (s.Count < 2) return null;
        var end = s[^1];
        if (end.Bytes <= 0 || end.T <= 0) return null;

        double avg = end.Bytes * 8 / 1e6 / end.T;

        // Pico: melhor janela com pelo menos 1 s (janelas menores exagerariam por ruído de amostragem).
        double peak = 0;
        int j = 0;
        for (int i = 0; i < s.Count; i++)
        {
            if (j < i) j = i;
            while (j < s.Count - 1 && s[j].T - s[i].T < 1.0) j++;
            double dt = s[j].T - s[i].T;
            if (dt >= 1.0) peak = Math.Max(peak, (s[j].Bytes - s[i].Bytes) * 8 / 1e6 / dt);
        }
        if (peak == 0) peak = avg; // medição curta demais para ter janela de 1 s

        // Sustentado: ignora a aceleração inicial (2 s, ou 25% do tempo se o teste for curto).
        double skip = Math.Min(2.0, end.T * 0.25);
        int k = 0;
        while (k < s.Count - 1 && s[k].T < skip) k++;
        double span = end.T - s[k].T;
        double sustained = span >= 1.0 ? (end.Bytes - s[k].Bytes) * 8 / 1e6 / span : avg;

        return new BandwidthResult(avg, sustained, Math.Max(peak, sustained), end.Bytes, end.T);
    }
}
