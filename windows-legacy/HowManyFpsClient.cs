using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResetExplode;

/// <summary>
/// Cliente da API da HowManyFPS (POST howmanyfps.com/api → FPS médio e 1% low medidos em sessões reais).
/// Credenciais são pedidas por e-mail (info@howmanyfps.com). Como o plano tem cota de requisições,
/// as respostas ficam em cache em disco por 7 dias.
/// </summary>
public sealed class HowManyFpsClient
{
    private const string Endpoint = "https://howmanyfps.com/api";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static string CachePath => Path.Combine(AppSettings.Folder, "fps-cache.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, CacheEntry> _cache;

    public string? ApiKey { get; set; }
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    public sealed record Result(double AvgFps, double Min1Fps);
    private sealed record CacheEntry(double AvgFps, double Min1Fps, DateTime Fetched);

    private sealed class Response
    {
        [JsonPropertyName("avgFps")] public double? AvgFps { get; set; }
        [JsonPropertyName("min1Fps")] public double? Min1Fps { get; set; }
    }

    public HowManyFpsClient()
    {
        try
        {
            _cache = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? new()
                : new();
        }
        catch { _cache = new(); }
    }

    /// <summary>Consulta (ou devolve do cache). Lança exceção com mensagem legível em caso de erro.</summary>
    public async Task<Result> GetAsync(string cpu, string gpu, string game, string resolution, string upscaling,
                                       CancellationToken ct = default)
    {
        string key = $"{cpu}|{gpu}|{game}|{resolution}|{upscaling}";
        if (_cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.Fetched < CacheTtl)
            return new Result(c.AvgFps, c.Min1Fps);
        if (!Configured) throw new InvalidOperationException("Chave da API não configurada.");

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { cpu, gpu, game, resolution, upscaling })
        };
        // O formato exato de autenticação vem com as credenciais; enviamos os dois cabeçalhos mais comuns.
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = (int)resp.StatusCode switch
            {
                401 or 403 => "chave recusada pela API",
                404 => "jogo ou hardware não encontrado na base",
                429 => "cota de requisições esgotada",
                _ => $"HTTP {(int)resp.StatusCode}"
            };
            throw new HttpRequestException(msg);
        }

        var data = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct)
                   ?? throw new HttpRequestException("resposta vazia");
        if (data.AvgFps is not double avg) throw new HttpRequestException("resposta sem avgFps");
        var result = new Result(avg, data.Min1Fps ?? 0);

        _cache[key] = new CacheEntry(result.AvgFps, result.Min1Fps, DateTime.UtcNow);
        SaveCache();
        return result;
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache));
        }
        catch { }
    }
}
