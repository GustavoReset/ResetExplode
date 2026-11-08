using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FortKnox;

/// <summary>Um salto do caminho. <see cref="Address"/> nulo = nenhum dos pacotes foi respondido ("*").</summary>
public sealed record TraceHop(int Ttl, string? Address, string? HostName, int Sent, int Lost, double Min, double Avg, double Max, bool IsDestination, bool Unreachable = false)
{
    public bool Responded => Address != null;
    public double LossPct => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    /// <summary>Nome (se o DNS reverso achou) e IP, ou "*" quando o salto não respondeu.</summary>
    public string Display => Address == null ? "* (sem resposta)" : HostName != null ? $"{HostName} ({Address})" : Address;
}

public sealed record TraceResult(string Target, string TargetAddress, IReadOnlyList<TraceHop> Hops, bool Reached);

/// <summary>
/// Traceroute por ICMP: manda pings com TTL 1, 2, 3… e cada roteador que descarta o pacote responde "tempo excedido",
/// revelando o caminho. Os saltos são sondados em lotes paralelos (um caminho de 15 saltos leva segundos, não minutos) e a
/// busca para no primeiro lote em que o destino responde. Não precisa de administrador.
/// </summary>
public static class TraceRoute
{
    private const int BatchSize = 6;

    public static async Task<TraceResult> RunAsync(string host, int maxHops = 30, int probes = 3, int timeoutMs = 1500,
        bool resolveNames = true, IProgress<IReadOnlyList<TraceHop>>? progress = null, CancellationToken ct = default)
    {
        host = host.Trim();
        if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException("Digite um endereço válido (ex.: 1.1.1.1 ou google.com).");
        maxHops = Math.Clamp(maxHops, 1, 64);
        probes = Math.Clamp(probes, 1, 10);

        IPAddress dest;
        try
        {
            var all = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
            dest = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                   ?? throw new ArgumentException($"Não foi possível resolver \"{host}\".");
        }
        catch (SocketException) { throw new ArgumentException($"Não foi possível resolver \"{host}\". Confira o nome e a conexão."); }

        var hops = new List<TraceHop>();
        bool reached = false;
        for (int first = 1; first <= maxHops && !reached; first += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var ttls = Enumerable.Range(first, Math.Min(BatchSize, maxHops - first + 1)).ToList();
            var batch = await Task.WhenAll(ttls.Select(t => ProbeHopAsync(dest, t, probes, timeoutMs, ct)));
            hops.AddRange(batch);
            reached = batch.Any(h => h.IsDestination);
            progress?.Report(Trim(hops));
        }

        var final = Trim(hops);
        if (resolveNames)
        {
            var named = await Task.WhenAll(final.Select(async h =>
                h.Address != null ? h with { HostName = await ReverseAsync(h.Address, ct) } : h));
            final = named.ToList();
            progress?.Report(final);
        }
        return new TraceResult(host, dest.ToString(), final, reached);
    }

    /// <summary>Descarta o que vier depois do primeiro salto que chegou ao destino (lotes paralelos sondam saltos a mais).</summary>
    internal static List<TraceHop> Trim(IEnumerable<TraceHop> hops)
    {
        var sorted = hops.OrderBy(h => h.Ttl).ToList();
        int dest = sorted.FindIndex(h => h.IsDestination);
        return dest >= 0 ? sorted.Take(dest + 1).ToList() : sorted;
    }

    private static async Task<TraceHop> ProbeHopAsync(IPAddress dest, int ttl, int probes, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping(); // Ping não é seguro para uso paralelo: um por salto
        var options = new PingOptions(ttl, dontFragment: true);
        var buffer = new byte[32];
        var rtts = new List<double>();
        string? address = null;
        bool isDest = false, unreachable = false;
        int lost = 0;

        for (int p = 0; p < probes; p++)
        {
            ct.ThrowIfCancellationRequested();
            PingReply reply;
            try { reply = await ping.SendPingAsync(dest, TimeSpan.FromMilliseconds(timeoutMs), buffer, options, ct); }
            catch (PingException) { lost++; continue; }
            catch (InvalidOperationException) { lost++; continue; }

            switch (reply.Status)
            {
                case IPStatus.TtlExpired or IPStatus.TimeExceeded:
                    address ??= reply.Address?.ToString();
                    rtts.Add(Math.Max(1, reply.RoundtripTime));
                    break;
                case IPStatus.Success:
                    address ??= reply.Address?.ToString();
                    isDest = true;
                    rtts.Add(Math.Max(1, reply.RoundtripTime));
                    break;
                case IPStatus.TimedOut:
                    lost++;
                    break;
                default: // rede/host inalcançável etc.: o roteador respondeu, mas o caminho acaba aqui
                    address ??= reply.Address?.ToString();
                    unreachable = address != null;
                    if (address == null) lost++; else rtts.Add(Math.Max(1, reply.RoundtripTime));
                    break;
            }
        }

        return rtts.Count == 0
            ? new TraceHop(ttl, null, null, probes, lost, 0, 0, 0, false)
            : new TraceHop(ttl, address, null, probes, lost, rtts.Min(), rtts.Average(), rtts.Max(), isDest, unreachable);
    }

    private static async Task<string?> ReverseAsync(string ip, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip, ct);
            var done = await Task.WhenAny(lookup, Task.Delay(1500, ct));
            if (done != lookup) return null;
            string name = (await lookup).HostName;
            return string.Equals(name, ip, StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ Análise (função pura, testável)

    public sealed record TraceFinding(bool IsProblem, string Text);

    /// <summary>
    /// Interpreta o caminho. Duas armadilhas clássicas que um leigo confunde com "problema":
    /// roteadores intermediários que limitam respostas ICMP (perda num salto que NÃO continua nos seguintes) e destinos
    /// que bloqueiam ping. Só é perda real quando ela persiste do salto em diante até o destino.
    /// </summary>
    public static List<TraceFinding> Analyze(TraceResult r)
    {
        var f = new List<TraceFinding>();
        var hops = r.Hops;
        var answered = hops.Where(h => h.Responded).ToList();
        if (answered.Count == 0)
        {
            f.Add(new(true, "Nenhum salto respondeu. Sem conexão, ou o ICMP (ping) está bloqueado na sua rede."));
            return f;
        }

        if (!r.Reached)
        {
            var last = answered[^1];
            f.Add(new(false, $"O destino não respondeu ao ping (muitos servidores bloqueiam ICMP, isso não significa que está fora do ar). O último salto que respondeu foi o #{last.Ttl} ({last.Display})."));
        }

        // Perda real: um salto com perda alta E todos os seguintes que responderam também com perda alta.
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            if (!h.Responded || h.Sent < 3 || h.LossPct < 20) continue;
            var later = hops.Skip(i + 1).Where(x => x.Responded).ToList();
            bool persists = later.Count > 0 && later.All(x => x.LossPct >= 20);
            if (persists) { f.Add(new(true, $"Perda de pacotes real a partir do salto #{h.Ttl} ({h.Display}): {h.LossPct:0}% e continua até o destino.")); break; }
        }
        var transient = hops.Where(h => h.Responded && !h.IsDestination && h.Sent >= 3 && h.LossPct >= 20).ToList();
        if (transient.Count > 0 && !f.Any(x => x.Text.StartsWith("Perda de pacotes real")))
            f.Add(new(false, $"Perda no(s) salto(s) {string.Join(", ", transient.Select(h => "#" + h.Ttl))} que não aparece nos seguintes: é o roteador limitando respostas de ping, não um problema real."));

        // Salto de latência que se mantém até o destino.
        for (int i = 1; i < answered.Count; i++)
        {
            var prev = answered[i - 1];
            var cur = answered[i];
            if (cur.Avg - prev.Avg < 40) continue;
            var rest = answered.Skip(i).ToList();
            if (rest.All(x => x.Avg >= prev.Avg + 30))
            {
                f.Add(new(false, $"A latência pula de {prev.Avg:0} ms (salto #{prev.Ttl}) para {cur.Avg:0} ms (salto #{cur.Ttl}, {cur.Display}) e fica assim até o fim. " +
                                 "Se o destino é em outro país, isso é normal (cabo submarino); se é perto, o gargalo está nesse trecho."));
                break;
            }
        }

        var dest = answered.FirstOrDefault(h => h.IsDestination);
        if (dest != null)
        {
            if (dest.Avg > 150) f.Add(new(true, $"Latência alta até o destino ({dest.Avg:0} ms)."));
            else if (!f.Any(x => x.IsProblem)) f.Add(new(false, $"Caminho saudável: {hops.Count} salto(s), destino a {dest.Avg:0} ms."));
        }
        return f;
    }
}
