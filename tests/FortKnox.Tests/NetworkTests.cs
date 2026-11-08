using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace FortKnox.Tests;

// ====================================================================== Banda

public sealed class BandwidthMathTests
{
    /// <summary>Gera amostras a cada 250 ms: Mbps(t) em cada instante, acumulando bytes.</summary>
    private static List<(double T, long Bytes)> Series(double seconds, Func<double, double> mbps)
    {
        var s = new List<(double, long)> { (0, 0) };
        double bytes = 0;
        for (double t = 0.25; t <= seconds + 1e-9; t += 0.25)
        {
            bytes += mbps(t) * 1e6 / 8 * 0.25;
            s.Add((t, (long)bytes));
        }
        return s;
    }

    [Fact]
    public void SlowStart_AverageUnderestimates_SustainedDoesNot()
    {
        // 2 s de aceleração (20 Mbps) e depois 100 Mbps estáveis, em 10 s
        var r = BandwidthTest.Summarize(Series(10, t => t <= 2 ? 20 : 100))!;
        Assert.InRange(r.Sustained, 98, 102);
        Assert.True(r.Average < 90, $"a média deve sofrer com a aceleração: {r.Average}");
        Assert.True(r.Peak >= r.Sustained - 0.5);
        Assert.Equal(10, r.Seconds, 1);
    }

    [Fact]
    public void ConstantRate_AllThreeAgree()
    {
        var r = BandwidthTest.Summarize(Series(8, _ => 50))!;
        Assert.InRange(r.Average, 49, 51);
        Assert.InRange(r.Sustained, 49, 51);
        Assert.InRange(r.Peak, 49, 51);
    }

    [Fact]
    public void Peak_FindsBestOneSecondWindow()
    {
        // 10 Mbps, com uma rajada de 200 Mbps entre 4 s e 6 s
        var r = BandwidthTest.Summarize(Series(10, t => t > 4 && t <= 6 ? 200 : 10))!;
        Assert.InRange(r.Peak, 190, 201);
        Assert.True(r.Sustained < 60);
    }

    [Fact]
    public void ShortMeasurement_FallsBackToAverage()
    {
        var r = BandwidthTest.Summarize(Series(0.75, _ => 80))!;
        Assert.InRange(r.Sustained, 78, 82);
        Assert.InRange(r.Peak, 78, 82);
    }

    [Fact]
    public void NothingTransferred_IsNull()
    {
        Assert.Null(BandwidthTest.Summarize(new List<(double, long)> { (0, 0), (1, 0), (2, 0) }));
        Assert.Null(BandwidthTest.Summarize(new List<(double, long)> { (0, 0) }));
        Assert.Null(BandwidthTest.Summarize(new List<(double, long)>()));
    }

    [Fact]
    public void TrailingMbps_UsesLastSecond()
    {
        var s = Series(5, t => t <= 4 ? 10 : 80);
        Assert.InRange(BandwidthTest.TrailingMbps(s), 40, 80); // janela de 1 s cobre 0,75 s a 80 e 0,25 s a 10
        Assert.Equal(0, BandwidthTest.TrailingMbps(new List<(double, long)> { (0, 0) }));
    }

    [Fact]
    public void Text_ReportsAllThree()
    {
        var r = BandwidthTest.Summarize(Series(6, _ => 100))!;
        Assert.Contains("sustentado", r.Text);
        Assert.Contains("pico", r.Text);
        Assert.Contains("média", r.Text);
    }
}

/// <summary>Servidor HTTP mínimo em localhost para testar download/upload de ponta a ponta, sem internet.</summary>
internal sealed class TinyHttpServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public int StatusCode { get; set; } = 200;
    public int BodySize { get; set; } = 500_000;
    public string? ExtraHeader { get; set; } // ex.: "cf-ray: abc123-GRU"
    public long UploadedBytes;
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";

    public TinyHttpServer()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            try { while (!_cts.IsCancellationRequested) _ = Serve(await _l.AcceptTcpClientAsync(_cts.Token)); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    private async Task Serve(TcpClient client)
    {
        using var c = client;
        try
        {
            var st = c.GetStream();
            var body = new byte[500_000];
            while (!_cts.IsCancellationRequested)
            {
                // cabeçalhos
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n"))
                {
                    if (await st.ReadAsync(one, _cts.Token) == 0) return;
                    head.Append((char)one[0]);
                }
                string h = head.ToString();
                bool post = h.StartsWith("POST");
                if (post)
                {
                    int i = h.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
                    long len = long.Parse(h[(i + 15)..h.IndexOf("\r\n", i)].Trim());
                    var buf = new byte[65536];
                    while (len > 0)
                    {
                        int n = await st.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, len)), _cts.Token);
                        if (n == 0) return;
                        len -= n; Interlocked.Add(ref UploadedBytes, n);
                    }
                }
                if (StatusCode != 200)
                {
                    await st.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {StatusCode} Erro\r\nContent-Length: 0\r\n\r\n"), _cts.Token);
                    continue;
                }
                int size = post ? 0 : Math.Min(BodySize, body.Length);
                string extra = ExtraHeader != null ? ExtraHeader + "\r\n" : "";
                await st.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\n{extra}Content-Length: {size}\r\n\r\n"), _cts.Token);
                if (size > 0) await st.WriteAsync(body.AsMemory(0, size), _cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

public sealed class BandwidthLocalTests
{
    [Fact]
    public async Task Download_FromLocalServer_ReportsRateAndLiveSamples()
    {
        using var srv = new TinyHttpServer();
        var live = new List<double>();
        var o = await BandwidthTest.DownloadAsync(2, live.Add, CancellationToken.None, srv.Url("/down"), connections: 2);
        Assert.Null(o.Error);
        Assert.NotNull(o.Result);
        Assert.True(o.Result!.Bytes > 1_000_000, $"bytes={o.Result.Bytes}");
        Assert.True(o.Result.Sustained > 0 && o.Result.Peak >= o.Result.Sustained);
        Assert.InRange(o.Result.Seconds, 1.8, 3.5);
        Assert.True(live.Count >= 5, $"leituras ao vivo: {live.Count}");
        Assert.Contains(live, v => v > 0);
    }

    [Fact]
    public async Task Upload_ToLocalServer_CountsBytes()
    {
        using var srv = new TinyHttpServer();
        var o = await BandwidthTest.UploadAsync(2, null, CancellationToken.None, srv.Url("/up"), connections: 2);
        Assert.NotNull(o.Result);
        await Task.Delay(100);
        Assert.True(srv.UploadedBytes >= o.Result!.Bytes - 3_000_000); // o servidor recebeu (praticamente) o que o cliente contou
        Assert.True(o.Result.Bytes >= 1_000_000);
    }

    [Fact]
    public async Task ServerRefusing_GivesNullResultAndReason()
    {
        int port; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; } // porta fechada
        var o = await BandwidthTest.DownloadAsync(2, null, CancellationToken.None, $"http://127.0.0.1:{port}/", connections: 2);
        Assert.Null(o.Result);
        Assert.False(string.IsNullOrWhiteSpace(o.Error));
    }

    [Fact]
    public async Task HttpError_IsReported()
    {
        using var srv = new TinyHttpServer { StatusCode = 500 };
        var o = await BandwidthTest.DownloadAsync(2, null, CancellationToken.None, srv.Url(), connections: 1);
        Assert.Null(o.Result);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public async Task Cancel_Throws_AndReturnsPromptly()
    {
        using var srv = new TinyHttpServer();
        using var cts = new CancellationTokenSource(600);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BandwidthTest.DownloadAsync(30, null, cts.Token, srv.Url(), connections: 2));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"demorou {sw.Elapsed}");
    }
}

// ====================================================================== Fase de ping (estilo Speedtest)

public sealed class LatencyTests
{
    [Fact]
    public void Summarize_MinMedianJitter()
    {
        var r = BandwidthTest.SummarizeLatency(new[] { 20.0, 22, 21, 40, 21 }, "GRU")!;
        Assert.Equal(20, r.Min);
        Assert.Equal(21, r.Median);
        Assert.Equal((2 + 1 + 19 + 19) / 4.0, r.Jitter, 6);
        Assert.Equal(5, r.Samples);
        Assert.Equal("GRU", r.Server);
        Assert.Contains("mín 20", r.Text);
    }

    [Fact]
    public void Summarize_EvenCountMedian_SingleSample_Empty()
    {
        Assert.Equal(25, BandwidthTest.SummarizeLatency(new[] { 10.0, 20, 30, 40 })!.Median);
        var one = BandwidthTest.SummarizeLatency(new[] { 7.0 })!;
        Assert.Equal(0, one.Jitter); Assert.Equal(7, one.Min);
        Assert.Null(BandwidthTest.SummarizeLatency(Array.Empty<double>()));
    }

    [Theory]
    [InlineData("8f2a1b3c4d5e6f70-GRU", "GRU")]
    [InlineData("abc-def-gru", "GRU")]
    [InlineData("abc-", null)]
    [InlineData("semtraco", null)]
    public void ColoFrom_ParsesCfRay(string ray, string? expected)
    {
        using var resp = new HttpResponseMessage();
        resp.Headers.TryAddWithoutValidation("cf-ray", ray);
        Assert.Equal(expected, BandwidthTest.ColoFrom(resp));
    }

    [Fact]
    public void ColoFrom_NoHeader_IsNull()
    {
        using var resp = new HttpResponseMessage();
        Assert.Null(BandwidthTest.ColoFrom(resp));
    }

    [Fact]
    public async Task Latency_AgainstLocalServer_Measures10Samples_AndReadsServer()
    {
        using var srv = new TinyHttpServer { BodySize = 0, ExtraHeader = "cf-ray: 9a8b7c-GRU" };
        var r = await BandwidthTest.LatencyAsync(CancellationToken.None, srv.Url("/ping"), samples: 10);
        Assert.NotNull(r);
        Assert.Equal(10, r!.Samples);
        Assert.Equal("GRU", r.Server);
        Assert.True(r.Min > 0 && r.Min <= r.Median && r.Jitter >= 0);
        Assert.True(r.Min < 1000, "loopback deve responder em bem menos de 1 s: " + r.Min);
    }

    [Fact]
    public async Task Latency_ServerDown_OrError_IsNull_NotAnException()
    {
        int port; using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        Assert.Null(await BandwidthTest.LatencyAsync(CancellationToken.None, $"http://127.0.0.1:{port}/", samples: 3));
        using var srv = new TinyHttpServer { StatusCode = 500 };
        Assert.Null(await BandwidthTest.LatencyAsync(CancellationToken.None, srv.Url(), samples: 3));
    }

    [Fact]
    public async Task Latency_Cancelled_Throws()
    {
        using var srv = new TinyHttpServer { BodySize = 0 };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BandwidthTest.LatencyAsync(cts.Token, srv.Url(), samples: 10));
    }
}

// ====================================================================== Traceroute

public sealed class TraceRouteTests
{
    private static TraceHop H(int ttl, string? addr, double avg, int lost = 0, bool dest = false, int sent = 3) =>
        new(ttl, addr, null, sent, lost, avg, avg, avg, dest);

    private static TraceResult R(bool reached, params TraceHop[] hops) => new("alvo", "1.2.3.4", hops, reached);

    [Fact]
    public async Task Loopback_ReachesDestinationInOneHop()
    {
        var r = await TraceRoute.RunAsync("127.0.0.1", resolveNames: false);
        Assert.True(r.Reached);
        Assert.Single(r.Hops);
        Assert.True(r.Hops[0].IsDestination);
        Assert.Equal("127.0.0.1", r.Hops[0].Address);
        Assert.Equal(0, r.Hops[0].LossPct);
    }

    [Fact]
    public async Task Localhost_NameIsResolved_AndProgressIsReported()
    {
        var reports = new List<int>();
        var r = await TraceRoute.RunAsync("localhost", resolveNames: true, progress: new SyncProgress<IReadOnlyList<TraceHop>>(h => reports.Add(h.Count)));
        Assert.True(r.Reached);
        Assert.NotEmpty(reports);
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("a b")] [InlineData("com espaco.com")]
    public async Task InvalidInput_Throws(string host)
        => await Assert.ThrowsAsync<ArgumentException>(() => TraceRoute.RunAsync(host));

    [Fact]
    public async Task OverlongHost_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => TraceRoute.RunAsync(new string('a', 300) + ".com"));

    [Fact]
    public async Task UnresolvableName_GivesFriendlyError()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => TraceRoute.RunAsync("host-que-nao-existe.invalid"));
        Assert.Contains("resolver", ex.Message);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TraceRoute.RunAsync("127.0.0.1", ct: cts.Token));
    }

    [Fact]
    public void Trim_DropsHopsAfterDestination_AndSorts()
    {
        var t = TraceRoute.Trim(new[] { H(3, "c", 5, dest: true), H(1, "a", 1), H(5, "e", 5, dest: true), H(2, null, 0, lost: 3), H(4, "d", 5, dest: true) });
        Assert.Equal(new[] { 1, 2, 3 }, t.Select(h => h.Ttl));
        var none = TraceRoute.Trim(new[] { H(2, "b", 1), H(1, "a", 1) });
        Assert.Equal(new[] { 1, 2 }, none.Select(h => h.Ttl));
    }

    [Fact]
    public void Display_ShowsNameAndAddress_OrStar()
    {
        Assert.Equal("* (sem resposta)", new TraceHop(1, null, null, 3, 3, 0, 0, 0, false).Display);
        Assert.Equal("1.1.1.1", H(1, "1.1.1.1", 1).Display);
        Assert.Equal("one.one.one.one (1.1.1.1)", (H(1, "1.1.1.1", 1) with { HostName = "one.one.one.one" }).Display);
    }

    [Fact]
    public void Analyze_HealthyPath()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "192.168.0.1", 2), H(2, "10.0.0.1", 9), H(3, "1.1.1.1", 12, dest: true)));
        Assert.DoesNotContain(f, x => x.IsProblem);
        Assert.Contains(f, x => x.Text.Contains("saudável"));
    }

    [Fact]
    public void Analyze_LossAtMiddleHopOnly_IsNotAProblem()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "a", 2), H(2, "b", 10, lost: 2), H(3, "c", 12), H(4, "d", 14, dest: true)));
        Assert.DoesNotContain(f, x => x.IsProblem);
        Assert.Contains(f, x => x.Text.Contains("limitando respostas"));
    }

    [Fact]
    public void Analyze_LossThatPersistsToDestination_IsAProblem()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "a", 2), H(2, "b", 10, lost: 1), H(3, "c", 12, lost: 1), H(4, "d", 14, lost: 2, dest: true)));
        Assert.Contains(f, x => x.IsProblem && x.Text.Contains("real a partir do salto #2"));
    }

    [Fact]
    public void Analyze_PersistentLatencyJump_IsReportedWithCaveat()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "a", 2), H(2, "b", 10), H(3, "c", 140), H(4, "d", 145, dest: true)));
        var jump = Assert.Single(f, x => x.Text.Contains("pula de"));
        Assert.Contains("#2", jump.Text); Assert.Contains("#3", jump.Text);
        Assert.Contains("cabo submarino", jump.Text);
    }

    [Fact]
    public void Analyze_LatencySpikeThatDoesNotPersist_IsIgnored()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "a", 2), H(2, "b", 200), H(3, "c", 12), H(4, "d", 14, dest: true)));
        Assert.DoesNotContain(f, x => x.Text.Contains("pula de"));
    }

    [Fact]
    public void Analyze_DestinationUnreachable_IsAFriendlyNote()
    {
        var f = TraceRoute.Analyze(R(false, H(1, "a", 2), H(2, "b", 10), H(3, null, 0, lost: 3)));
        var n = Assert.Single(f);
        Assert.False(n.IsProblem);
        Assert.Contains("bloqueiam ICMP", n.Text); Assert.Contains("#2", n.Text);
    }

    [Fact]
    public void Analyze_NothingAnswered_IsAProblem()
    {
        var f = TraceRoute.Analyze(R(false, H(1, null, 0, lost: 3), H(2, null, 0, lost: 3)));
        Assert.Contains(f, x => x.IsProblem);
    }

    [Fact]
    public void Analyze_HighDestinationLatency_IsAProblem()
    {
        var f = TraceRoute.Analyze(R(true, H(1, "a", 2), H(2, "b", 180, dest: true)));
        Assert.Contains(f, x => x.IsProblem && x.Text.Contains("180"));
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
}

// ====================================================================== Consumo por processo (contabilidade)

public sealed class NetUsageAggregatorTests
{
    [Fact]
    public void Rates_AreBytesPerSecondSinceLastSnapshot_TotalsAccumulate()
    {
        var a = new NetUsageAggregator();
        a.Add(100, 2_000_000, received: true);
        a.Add(100, 500_000, received: false);
        var s1 = a.Snapshot(2.0).Single();
        Assert.Equal(1_000_000, s1.DownBps, 3);
        Assert.Equal(250_000, s1.UpBps, 3);
        Assert.Equal(2_000_000, s1.TotalDown);

        a.Add(100, 1_000_000, received: true);
        var s2 = a.Snapshot(1.0).Single();
        Assert.Equal(1_000_000, s2.DownBps, 3);
        Assert.Equal(0, s2.UpBps, 3);                 // nada enviado neste intervalo
        Assert.Equal(3_000_000, s2.TotalDown);        // total acumulado preservado
        Assert.Equal(500_000, s2.TotalUp);
    }

    [Fact]
    public void SortedByBusiestFirst_IdleProcessesStayWithTotals()
    {
        var a = new NetUsageAggregator();
        a.Add(1, 1000, true); a.Add(2, 9_000_000, true); a.Add(3, 50, false);
        a.Snapshot(1);
        a.Add(1, 5_000, true);          // só o 1 trafega no 2º intervalo
        var s = a.Snapshot(1);
        Assert.Equal(1, s[0].Pid);
        Assert.Equal(3, s.Count);       // 2 e 3 continuam na lista, taxa zero, total mantido
        Assert.Equal(9_000_000, s.Single(x => x.Pid == 2).TotalDown);
        Assert.Equal(0, s.Single(x => x.Pid == 2).DownBps);
    }

    [Fact]
    public void IgnoresZeroNegativeAndInvalidPid()
    {
        var a = new NetUsageAggregator();
        a.Add(5, 0, true); a.Add(5, -10, true); a.Add(-1, 100, true);
        Assert.Equal(0, a.Count);
        Assert.Empty(a.Snapshot(1));
    }

    [Fact]
    public void ZeroOrNegativeElapsed_DoesNotDivideByZero()
    {
        var a = new NetUsageAggregator(); a.Add(1, 1000, true);
        var s = a.Snapshot(0).Single();
        Assert.True(double.IsFinite(s.DownBps));
    }

    [Fact]
    public void ParallelAdds_AreAllCounted()
    {
        var a = new NetUsageAggregator();
        Parallel.For(0, 8, w => { for (int i = 0; i < 10_000; i++) a.Add(w % 4, 10, i % 2 == 0); });
        var s = a.Snapshot(1);
        Assert.Equal(4, s.Count);
        Assert.Equal(8 * 10_000 * 10L, s.Sum(x => x.TotalDown + x.TotalUp));
    }

    [Fact]
    public void EntryCount_IsCapped_DroppingTheOldest()
    {
        var a = new NetUsageAggregator();
        for (int pid = 1; pid <= 1200; pid++) { a.Add(pid, 10, true); if (pid % 100 == 0) a.Snapshot(1); }
        Assert.True(a.Count <= 1000, $"count={a.Count}");
        a.Add(1_000_000, 10, true);     // o mais recente sobrevive
        Assert.Contains(a.Snapshot(1), x => x.Pid == 1_000_000);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var a = new NetUsageAggregator(); a.Add(1, 100, true); a.Clear();
        Assert.Equal(0, a.Count);
    }

    [Fact]
    public void Monitor_WithoutAdmin_FailsWithClearMessage_AndNeverThrows()
    {
        // Em sessão elevada isto inicia de verdade; sem elevação deve falhar com mensagem, sem exceção.
        using var m = new ProcessBandwidthMonitor();
        m.Start();
        Assert.True(m.Running || !string.IsNullOrEmpty(m.Error));
        m.Stop();
        Assert.False(m.Running);
    }
}
