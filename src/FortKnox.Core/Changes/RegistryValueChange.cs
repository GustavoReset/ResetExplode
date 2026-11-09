using System.Text.Json;
using Microsoft.Win32;

namespace FortKnox.Core.Changes;

/// <summary>
/// Mudança de um valor do registro, com desfazer exato (inclusive "o valor não existia": desfazer apaga).
/// Generaliza o TweakBackup do ARSENAL. Só HKCU e HKLM; HKLM exige o app elevado.
/// </summary>
public sealed class RegistryValueChange : IChangeHandler
{
    public const string KindName = "registry.value";
    public string Kind => KindName;

    /// <param name="Hive">"HKCU" ou "HKLM".</param>
    /// <param name="Type">Nome de RegistryValueKind (DWord, String, QWord, ExpandString).</param>
    /// <param name="Value">Texto do valor (número em decimal para DWord/QWord). null = apagar o valor.</param>
    public sealed record Payload(string Hive, string Key, string Name, string Type, string? Value);

    private sealed record Undo(string Hive, string Key, string Name, bool Existed, string? Type, string? Value);

    public static ChangeRequest Request(string moduleId, string title, Payload p) =>
        new(moduleId, title, KindName, JsonSerializer.Serialize(p));

    public Task<string> ApplyAsync(string payload, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<Payload>(payload) ?? throw new ArgumentException("Payload vazio.");
        using var root = Root(p.Hive);
        using var key = root.CreateSubKey(p.Key, writable: true) ?? throw new InvalidOperationException($"Não foi possível abrir {p.Hive}\\{p.Key}.");
        object? old = key.GetValue(p.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var undo = new Undo(p.Hive, p.Key, p.Name, old != null, old == null ? null : key.GetValueKind(p.Name).ToString(), Format(old));
        Write(key, p.Name, p.Type, p.Value);
        return Task.FromResult(JsonSerializer.Serialize(undo));
    }

    public Task RevertAsync(string undoData, CancellationToken ct)
    {
        var u = JsonSerializer.Deserialize<Undo>(undoData) ?? throw new ArgumentException("Dados de desfazer vazios.");
        using var root = Root(u.Hive);
        using var key = root.CreateSubKey(u.Key, writable: true) ?? throw new InvalidOperationException($"Não foi possível abrir {u.Hive}\\{u.Key}.");
        if (u.Existed) Write(key, u.Name, u.Type!, u.Value);
        else key.DeleteValue(u.Name, throwOnMissingValue: false);
        return Task.CompletedTask;
    }

    private static RegistryKey Root(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => throw new ArgumentException($"Hive não suportado: {hive}"),
    };

    private static void Write(RegistryKey key, string name, string type, string? value)
    {
        if (value == null) { key.DeleteValue(name, throwOnMissingValue: false); return; }
        var kind = Enum.Parse<RegistryValueKind>(type, ignoreCase: true);
        object data = kind switch
        {
            RegistryValueKind.DWord => unchecked((int)uint.Parse(value)),
            RegistryValueKind.QWord => unchecked((long)ulong.Parse(value)),
            RegistryValueKind.String or RegistryValueKind.ExpandString => value,
            _ => throw new NotSupportedException($"Tipo de valor não suportado: {kind}"),
        };
        key.SetValue(name, data, kind);
    }

    private static string? Format(object? v) => v switch
    {
        null => null,
        int i => unchecked((uint)i).ToString(),
        long l => unchecked((ulong)l).ToString(),
        string s => s,
        _ => throw new NotSupportedException($"Valor atual de tipo não suportado ({v.GetType().Name}); nada foi alterado."),
    };
}
