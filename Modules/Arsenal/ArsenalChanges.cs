using System.Text.Json;
using FortKnox.Core.Changes;

namespace FortKnox;

/// <summary>
/// Ferrolhos do ARSENAL (Fase 3.4e): ajustes do Windows e programas removidos da inicialização passam pelo Diário de Ferrolhos
/// (history.db) em vez do TweakBackup/StartupRemoved do settings.json. Cada mudança guarda o próprio desfazer, então
/// "Restaurar originais" e "Restaurar removidos" são só desfazer os ferrolhos do ARSENAL, do mais novo para o mais velho.
/// </summary>
public static class ArsenalChanges
{
    public const string ModuleId = "arsenal";
    public const string KindTweak = "arsenal.tweak";
    public const string KindTweakLegacy = "arsenal.tweak.legado";
    public const string KindStartup = "arsenal.startup";
    public const string KindStartupLegacy = "arsenal.startup.legado";

    public sealed record TweakPayload(string Id, bool On);

    /// <summary>O que identifica uma entrada de inicialização para removê-la.</summary>
    public sealed record StartupTarget(string Kind, bool Machine, string Name, string Command, string? FilePath);

    public static void Register(IChangeJournal journal, IReadOnlyList<Tweak> tweaks)
    {
        journal.Register(new TweakChange(tweaks));
        journal.Register(new LegacyTweakChange(tweaks));
        journal.Register(new StartupChange());
        journal.Register(new LegacyStartupChange());
    }

    public static string TweakTitle(Tweak t, bool on) => $"{t.Title}: {(on ? "ligado" : "desligado")}";

    public const string MachineSuffix = " · todos os usuários";

    /// <summary>O título diz se é de todos os usuários (restaurar esse exige administrador).</summary>
    public static string StartupTitle(string name, bool machine) => $"Removido da inicialização: {name}{(machine ? MachineSuffix : "")}";

    public static ChangeRequest TweakRequest(Tweak t, bool on) =>
        new(ModuleId, TweakTitle(t, on), KindTweak, JsonSerializer.Serialize(new TweakPayload(t.Id, on)));

    public static ChangeRequest StartupRequest(StartupEntry e) =>
        new(ModuleId, StartupTitle(e.Name, e.Machine), KindStartup,
            JsonSerializer.Serialize(new StartupTarget(e.Kind, e.Machine, e.Name, e.Command, e.FilePath)));

    /// <summary>Ajustes aplicados pelo Fort Knox que ainda podem ser desfeitos (mais novo primeiro).</summary>
    public static List<Ferrolho> AppliedTweaks(IChangeJournal j) =>
        j.List(ModuleId).Where(f => f.State == FerrolhoState.Applied && f.Kind is KindTweak or KindTweakLegacy).ToList();

    /// <summary>Programas removidos da inicialização que ainda podem voltar (mais novo primeiro).</summary>
    public static List<Ferrolho> RemovedStartup(IChangeJournal j) =>
        j.List(ModuleId).Where(f => f.State == FerrolhoState.Applied && f.Kind is KindStartup or KindStartupLegacy).ToList();

    /// <summary>Migração única do settings.json antigo: cada cópia vira um ferrolho já aplicado (aplicar não mexe em nada).</summary>
    public static async Task<int> MigrateLegacyAsync(IChangeJournal j, TweakBackup legacyTweaks, IReadOnlyList<StartupBackup> legacyStartup)
    {
        int n = 0;
        if (legacyTweaks.Count > 0)
        {
            await j.ApplyAsync(new ChangeRequest(ModuleId, "Ajustes do Windows feitos antes desta versão", KindTweakLegacy,
                                                 JsonSerializer.Serialize(legacyTweaks)));
            n++;
        }
        foreach (var b in legacyStartup)
        {
            await j.ApplyAsync(new ChangeRequest(ModuleId, StartupTitle(b.Name, b.Machine), KindStartupLegacy, JsonSerializer.Serialize(b)));
            n++;
        }
        return n;
    }

    /// <summary>Liga/desliga um ajuste; o desfazer é a cópia dos valores de antes, tirada na hora.</summary>
    private sealed class TweakChange(IReadOnlyList<Tweak> tweaks) : IChangeHandler
    {
        public string Kind => KindTweak;

        public Task<string> ApplyAsync(string payload, CancellationToken ct)
        {
            var p = JsonSerializer.Deserialize<TweakPayload>(payload) ?? throw new ArgumentException("Payload vazio.");
            var t = tweaks.FirstOrDefault(x => x.Id == p.Id) ?? throw new InvalidOperationException($"Ajuste desconhecido: {p.Id}");
            var before = new TweakBackup();
            t.Write(p.On, before);
            return Task.FromResult(JsonSerializer.Serialize(new { p.Id, Backup = before }));
        }

        public Task RevertAsync(string undoData, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(undoData);
            string id = doc.RootElement.GetProperty("Id").GetString()!;
            var backup = new TweakBackup(doc.RootElement.GetProperty("Backup").Deserialize<Dictionary<string, string?>>()!);
            (tweaks.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException($"Ajuste desconhecido: {id}")).Restore(backup);
            return Task.CompletedTask;
        }
    }

    /// <summary>TweakBackup antigo inteiro num ferrolho: desfazer devolve todos os ajustes ao que eram antes do Fort Knox.</summary>
    private sealed class LegacyTweakChange(IReadOnlyList<Tweak> tweaks) : IChangeHandler
    {
        public string Kind => KindTweakLegacy;
        public Task<string> ApplyAsync(string payload, CancellationToken ct) => Task.FromResult(payload);
        public Task RevertAsync(string undoData, CancellationToken ct)
        {
            WindowsTweaks.RestoreAll(tweaks, new TweakBackup(JsonSerializer.Deserialize<Dictionary<string, string?>>(undoData)!));
            return Task.CompletedTask;
        }
    }

    private sealed class StartupChange : IChangeHandler
    {
        public string Kind => KindStartup;

        public Task<string> ApplyAsync(string payload, CancellationToken ct)
        {
            var t = JsonSerializer.Deserialize<StartupTarget>(payload) ?? throw new ArgumentException("Payload vazio.");
            var backup = StartupManager.Remove(new StartupEntry { Kind = t.Kind, Machine = t.Machine, Name = t.Name, Command = t.Command, FilePath = t.FilePath });
            return Task.FromResult(JsonSerializer.Serialize(backup));
        }

        public Task RevertAsync(string undoData, CancellationToken ct)
        {
            StartupManager.Restore(JsonSerializer.Deserialize<StartupBackup>(undoData)!);
            return Task.CompletedTask;
        }
    }

    private sealed class LegacyStartupChange : IChangeHandler
    {
        public string Kind => KindStartupLegacy;
        public Task<string> ApplyAsync(string payload, CancellationToken ct) => Task.FromResult(payload);
        public Task RevertAsync(string undoData, CancellationToken ct)
        {
            StartupManager.Restore(JsonSerializer.Deserialize<StartupBackup>(undoData)!);
            return Task.CompletedTask;
        }
    }
}
