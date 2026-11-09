using System.Text.Json;
using FortKnox.Core.Events;
using Microsoft.Data.Sqlite;

namespace FortKnox.Core.Changes;

/// <summary>Estado de um ferrolho. Pending = registrado mas a aplicação não confirmou (o app caiu no meio, por exemplo).</summary>
public enum FerrolhoState { Pending, Applied, Reverted, Failed }

/// <summary>Um ajuste reversível ("3 ferrolhos aplicados"). Tudo que muda o sistema passa por aqui.</summary>
public sealed record Ferrolho(Guid Id, string ModuleId, string Title, string Kind, FerrolhoState State,
                              DateTime AppliedAt, DateTime? RevertedAt, string? Error);

/// <summary>Pedido de mudança: Kind escolhe o handler; Payload (JSON) diz o que aplicar.</summary>
public sealed record ChangeRequest(string ModuleId, string Title, string Kind, string Payload);

/// <summary>
/// Sabe aplicar e desfazer um tipo de mudança. Apply devolve os dados de desfazer (JSON), que ficam guardados no banco:
/// assim dá para desfazer mesmo depois de fechar e abrir o app.
/// </summary>
public interface IChangeHandler
{
    string Kind { get; }
    Task<string> ApplyAsync(string payload, CancellationToken ct);
    Task RevertAsync(string undoData, CancellationToken ct);
}

public sealed record ChangeApplied(Ferrolho Ferrolho) : IFortKnoxEvent;
public sealed record ChangeReverted(Ferrolho Ferrolho) : IFortKnoxEvent;

public interface IChangeJournal
{
    void Register(IChangeHandler handler);
    Task<Ferrolho> ApplyAsync(ChangeRequest request, CancellationToken ct = default);
    Task<Ferrolho> RevertAsync(Guid id, CancellationToken ct = default);
    IReadOnlyList<Ferrolho> List(string? moduleId = null, bool includeReverted = false);
}

/// <summary>
/// Diário de Ferrolhos (docs/ARQUITETURA.md §7.4) em SQLite. O registro é gravado ANTES de aplicar (Pending) e confirmado
/// depois (Applied + dados de desfazer). Se a aplicação falhar, fica Failed com o motivo e nada mais é tocado.
/// </summary>
public sealed class ChangeJournal : IChangeJournal
{
    private readonly string _cs;
    private readonly IEventBus _bus;
    private readonly ILog _log;
    private readonly Dictionary<string, IChangeHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serial = new(1, 1); // uma mudança de cada vez: desfazer depende da ordem

    public ChangeJournal(string dbPath, IEventBus bus, ILog? log = null)
    {
        _bus = bus;
        _log = log ?? NullLog.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ferrolhos (
                id TEXT PRIMARY KEY, module TEXT NOT NULL, title TEXT NOT NULL, kind TEXT NOT NULL,
                payload TEXT NOT NULL, undo TEXT, state INTEGER NOT NULL,
                applied_at TEXT NOT NULL, reverted_at TEXT, error TEXT);
            CREATE INDEX IF NOT EXISTS ix_ferrolhos_module ON ferrolhos(module, applied_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Register(IChangeHandler handler) { lock (_handlers) _handlers[handler.Kind] = handler; }

    private IChangeHandler Handler(string kind)
    {
        lock (_handlers)
            return _handlers.TryGetValue(kind, out var h) ? h : throw new InvalidOperationException($"Nenhum handler para \"{kind}\".");
    }

    public async Task<Ferrolho> ApplyAsync(ChangeRequest r, CancellationToken ct = default)
    {
        var handler = Handler(r.Kind);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            Exec("INSERT INTO ferrolhos(id,module,title,kind,payload,state,applied_at) VALUES($id,$m,$t,$k,$p,$s,$a)",
                 ("$id", id.ToString()), ("$m", r.ModuleId), ("$t", r.Title), ("$k", r.Kind), ("$p", r.Payload),
                 ("$s", (int)FerrolhoState.Pending), ("$a", now.ToString("O")));
            try
            {
                string undo = await handler.ApplyAsync(r.Payload, ct).ConfigureAwait(false);
                Exec("UPDATE ferrolhos SET state=$s, undo=$u WHERE id=$id", ("$s", (int)FerrolhoState.Applied), ("$u", undo), ("$id", id.ToString()));
            }
            catch (Exception ex)
            {
                Exec("UPDATE ferrolhos SET state=$s, error=$e WHERE id=$id", ("$s", (int)FerrolhoState.Failed), ("$e", ex.Message), ("$id", id.ToString()));
                _log.Error($"Ferrolho \"{r.Title}\" não foi aplicado", ex);
                throw;
            }
            var f = Get(id)!;
            _bus.Publish(new ChangeApplied(f));
            return f;
        }
        finally { _serial.Release(); }
    }

    public async Task<Ferrolho> RevertAsync(Guid id, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (f, undo) = GetWithUndo(id) ?? throw new KeyNotFoundException("Ferrolho não encontrado.");
            if (f.State != FerrolhoState.Applied || undo == null)
                throw new InvalidOperationException(f.State == FerrolhoState.Reverted ? "Este ajuste já foi desfeito." : "Este ajuste não chegou a ser aplicado.");
            await Handler(f.Kind).RevertAsync(undo, ct).ConfigureAwait(false);
            Exec("UPDATE ferrolhos SET state=$s, reverted_at=$r WHERE id=$id",
                 ("$s", (int)FerrolhoState.Reverted), ("$r", DateTime.UtcNow.ToString("O")), ("$id", id.ToString()));
            var after = Get(id)!;
            _bus.Publish(new ChangeReverted(after));
            return after;
        }
        finally { _serial.Release(); }
    }

    public IReadOnlyList<Ferrolho> List(string? moduleId = null, bool includeReverted = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,module,title,kind,state,applied_at,reverted_at,error,undo FROM ferrolhos WHERE ($m IS NULL OR module=$m)"
                        + (includeReverted ? "" : " AND state<>" + (int)FerrolhoState.Reverted) + " ORDER BY applied_at DESC";
        cmd.Parameters.AddWithValue("$m", (object?)moduleId ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var list = new List<Ferrolho>();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    private Ferrolho? Get(Guid id) => GetWithUndo(id)?.F;

    private (Ferrolho F, string? Undo)? GetWithUndo(Guid id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,module,title,kind,state,applied_at,reverted_at,error,undo FROM ferrolhos WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (Read(rd), rd.IsDBNull(8) ? null : rd.GetString(8)) : null;
    }

    private static Ferrolho Read(SqliteDataReader rd) => new(
        Guid.Parse(rd.GetString(0)), rd.GetString(1), rd.GetString(2), rd.GetString(3), (FerrolhoState)rd.GetInt32(4),
        DateTime.Parse(rd.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        rd.IsDBNull(6) ? null : DateTime.Parse(rd.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        rd.IsDBNull(7) ? null : rd.GetString(7));

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private void Exec(string sql, params (string Name, object Value)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Atalho para montar o Payload a partir de um objeto.</summary>
    public static string ToPayload<T>(T value) => JsonSerializer.Serialize(value);
}
