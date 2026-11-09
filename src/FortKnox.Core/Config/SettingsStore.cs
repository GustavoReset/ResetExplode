using System.Text.Json;
using System.Text.Json.Nodes;

namespace FortKnox.Core.Config;

/// <summary>Preferências de um módulo (uma seção do arquivo). T é uma classe simples com valores padrão no construtor.</summary>
public interface ISettings<T> where T : class, new()
{
    T Value { get; }
    /// <summary>Altera e grava na hora (escrita atômica).</summary>
    void Update(Action<T> change);
}

/// <summary>
/// Arquivo de preferências por módulo: <c>{ "schema": 2, "modules": { "vigia": {…}, "arena": {…} } }</c>.
/// Nesta fase convive com o settings.json antigo (AppSettings): cada módulo migra a sua parte quando passar a usar o núcleo
/// (Fase 3.3), com <see cref="Migrate{T}"/>. Gravação atômica: .tmp + File.Replace, o anterior vira .bak.
/// Arquivo corrompido não é sobrescrito às cegas: é guardado como .corrompido-AAAAMMDD-HHMMSS e começa um novo.
/// </summary>
public sealed class SettingsStore
{
    public const int Schema = 2;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly JsonObject _modules;
    private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);

    public SettingsStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log ?? NullLog.Instance;
        _modules = Load();
    }

    public string FilePath => _path;

    public ISettings<T> Section<T>(string moduleId) where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(moduleId)) throw new ArgumentException("Id do módulo vazio.", nameof(moduleId));
        lock (_gate)
        {
            if (_sections.TryGetValue(moduleId, out var existing))
                return existing as ISettings<T> ?? throw new InvalidOperationException($"A seção \"{moduleId}\" já foi aberta com outro tipo.");
            T value;
            try { value = _modules[moduleId]?.Deserialize<T>() ?? new T(); }
            catch (JsonException ex) { _log.Warn($"Seção \"{moduleId}\" ilegível; usando padrões", ex); value = new T(); }
            var s = new SectionImpl<T>(this, moduleId, value);
            _sections[moduleId] = s;
            return s;
        }
    }

    /// <summary>Se a seção ainda não existe, cria com o valor vindo do formato antigo. Devolve true se migrou.</summary>
    public bool Migrate<T>(string moduleId, Func<T?> fromLegacy) where T : class, new()
    {
        lock (_gate)
        {
            if (_modules.ContainsKey(moduleId)) return false;
            var legacy = fromLegacy();
            if (legacy == null) return false;
            _modules[moduleId] = JsonSerializer.SerializeToNode(legacy);
            SaveLocked();
            _sections.Remove(moduleId);
            return true;
        }
    }

    internal void Store<T>(string moduleId, T value)
    {
        lock (_gate)
        {
            _modules[moduleId] = JsonSerializer.SerializeToNode(value);
            SaveLocked();
        }
    }

    private JsonObject Load()
    {
        if (!File.Exists(_path)) return new JsonObject();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (root?["modules"] is JsonObject mods)
            {
                int schema = root["schema"]?.GetValue<int>() ?? 0;
                if (schema > Schema) _log.Warn($"Preferências de versão mais nova ({schema}); campos desconhecidos são preservados");
                return (JsonObject)mods.DeepClone();
            }
            throw new JsonException("Sem a seção \"modules\".");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            var bad = _path + $".corrompido-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(_path, bad); } catch (IOException) { }
            _log.Warn($"Preferências ilegíveis; cópia guardada em {Path.GetFileName(bad)}", ex);
            return new JsonObject();
        }
    }

    private void SaveLocked()
    {
        var root = new JsonObject { ["schema"] = Schema, ["modules"] = _modules.DeepClone() };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Json));
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, _path);
    }

    private sealed class SectionImpl<T>(SettingsStore store, string id, T value) : ISettings<T> where T : class, new()
    {
        private readonly object _gate = new();
        public T Value { get { lock (_gate) return value; } }

        public void Update(Action<T> change)
        {
            lock (_gate)
            {
                change(value);
                store.Store(id, value);
            }
        }
    }
}
