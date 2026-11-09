using System.IO;
using System.IO.Compression;
using dnYara;
using YaraResult = dnYara.ScanResult;
using Scanner = dnYara.Scanner;

namespace FortKnox.Muralha;

/// <summary>
/// Camada YARA da MURALHA (Fase 3.5b, D4 do docs/ARQUITETURA.md): libyara (BSD-3) pelo binding dnYara (Apache-2.0).
/// As regras são próprias do Fort Knox (Modules/Muralha/Regras/*.yar). Cada regra declara em <c>meta</c>:
/// <c>veredito</c> (Suspeito|Malicioso), <c>descricao</c> (texto para a pessoa) e, se quiser, <c>extensoes</c>
/// (".ps1,.bat"; sem isso vale para qualquer tipo).
/// O contexto do libyara é global e fica vivo enquanto o processo existir; as regras compiladas podem ser usadas por
/// várias varreduras ao mesmo tempo.
/// </summary>
public sealed class YaraLayer : IDisposable
{
    private static readonly object InitLock = new();
    private static YaraContext? _context;

    private readonly CompiledRules _rules;
    private readonly Dictionary<string, (Verdict Verdict, string Description, IReadOnlySet<string>? Extensions)> _meta = new();

    public int RuleCount => _meta.Count;

    public YaraLayer(string rulesSource)
    {
        lock (InitLock) _context ??= new YaraContext();
        using var compiler = new Compiler();
        compiler.AddRuleString(rulesSource);
        _rules = compiler.Compile();
        foreach (var r in _rules.Rules)
        {
            var metas = r.Metas ?? new Dictionary<string, object>();
            string Meta(string k) => metas.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
            var verdict = Enum.TryParse<Verdict>(Meta("veredito"), true, out var v) ? v : Verdict.Suspeito;
            string desc = Meta("descricao") is { Length: > 0 } d ? d : r.Identifier;
            string exts = Meta("extensoes");
            IReadOnlySet<string>? set = exts.Length == 0 ? null
                : exts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _meta[r.Identifier] = (verdict, desc, set);
        }
    }

    /// <summary>
    /// Regras embutidas no app. Vão comprimidas (GZip, ver FortKnox.csproj) para o texto das regras não aparecer dentro do
    /// próprio FortKnox.dll e o Fort Knox não acusar a si mesmo.
    /// </summary>
    public static string EmbeddedRules()
    {
        var asm = typeof(YaraLayer).Assembly;
        var sb = new System.Text.StringBuilder();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Muralha/Regras/", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            using var rd = new StreamReader(gz);
            sb.AppendLine(rd.ReadToEnd());
        }
        return sb.ToString();
    }

    /// <summary>Tenta carregar a camada; null se o libyara não estiver disponível (o resto da MURALHA segue funcionando).</summary>
    public static YaraLayer? TryCreate(string rulesSource, out string? error)
    {
        error = null;
        try { return new YaraLayer(rulesSource); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        { error = "motor YARA indisponível neste PC"; }
        catch (dnYara.Exceptions.CompilationException e) { error = "regras YARA com erro: " + string.Join("; ", e.Errors); }
        catch (Exception e) { error = e.Message; }
        return null;
    }

    public IReadOnlyList<YaraHit> ScanFile(string path)
    {
        var scanner = new Scanner();
        return Translate(scanner.ScanFile(path, _rules), Path.GetExtension(path));
    }

    public IReadOnlyList<YaraHit> ScanBytes(byte[] data, string ext)
    {
        var scanner = new Scanner();
        return Translate(scanner.ScanMemory(ref data, _rules), ext);
    }

    private List<YaraHit> Translate(IEnumerable<YaraResult> results, string ext)
    {
        var hits = new List<YaraHit>();
        foreach (var r in results)
        {
            string id = r.MatchingRule.Identifier;
            if (!_meta.TryGetValue(id, out var m)) continue;
            if (m.Extensions != null && !m.Extensions.Contains(ext)) continue;
            hits.Add(new YaraHit(id, m.Verdict, m.Description));
        }
        return hits;
    }

    public void Dispose() => _rules.Dispose();
}
