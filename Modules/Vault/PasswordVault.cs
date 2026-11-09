using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FortKnox;

// =====================================================================================================
//  COFRE · senhas — camada de dados (sem nenhuma referência a WPF).
//
//  Armazenamento: %AppData%\FortKnox\vault.dat
//      [ "FKV1" (4 bytes) ][ ProtectedData.Protect(JSON UTF-8, entropia do app, CurrentUser) ]
//  O arquivo INTEIRO é cifrado (título, usuário e URL também), então quem copiar o disco não vê nem os nomes.
//  Escrita atômica: grava em .tmp e troca com File.Replace (o arquivo anterior vira vault.dat.bak).
//
//  Se o DPAPI não conseguir abrir o arquivo (conta do Windows recriada, senha redefinida por um administrador,
//  arquivo copiado para outro PC…), o cofre entra em VaultState.Unreadable: NADA é sobrescrito e a UI oferece
//  restaurar a partir de um backup .fkvault, cifrado com uma senha escolhida pelo usuário (PBKDF2 + AES-GCM).
// =====================================================================================================

public enum VaultState { NotLoaded, Ready, Unreadable }

/// <summary>Registro completo (inclui a senha em texto). Só existe dentro do PasswordVault e no formulário de edição.</summary>
public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public VaultEntry Clone() => (VaultEntry)MemberwiseClone();
}

/// <summary>Linha exibida na grade: nunca carrega a senha, só uma máscara.</summary>
public sealed record VaultItem(Guid Id, string Title, string Username, string Url, string Category, DateTime Modified)
{
    public string PasswordMask => "••••••••";
    public string ModifiedText => Modified.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

/// <summary>CRUD do cofre. Toda E/S e criptografia rodam em Task.Run; a lista em memória é trocada de forma atômica (copy-on-write).</summary>
public sealed class PasswordVault
{
    private static readonly byte[] FileMagic = "FKV1"u8.ToArray();
    private static readonly byte[] BackupMagic = "FKB1"u8.ToArray();
    // Entropia fixa do app: não é segredo, só impede que outro programa chame Unprotect "às cegas" no mesmo blob.
    private static readonly byte[] Entropy = "FortKnox.Vault.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const int Pbkdf2Iterations = 600_000; // recomendação OWASP para PBKDF2-HMAC-SHA256
    private const int MinBackupPassphrase = 8;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1); // serializa Load/Add/Update/Delete/Import
    private volatile IReadOnlyList<VaultEntry> _entries = Array.Empty<VaultEntry>();
    private int _epoch; // sobe a cada Lock(); operações em andamento não "ressuscitam" a lista depois de bloqueado

    public PasswordVault(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(AppSettings.Folder, "vault.dat");

    public VaultState State { get; private set; } = VaultState.NotLoaded;
    /// <summary>Explicação em português quando State == Unreadable; também um aviso quando o cofre abriu a partir do .bak.</summary>
    public string? Problem { get; private set; }
    public int Count => _entries.Count;
    // ------------------------------------------------------------------ Carregar / bloquear

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            int epoch = _epoch;
            var (state, entries, problem) = await Task.Run(ReadFromDisk, ct);
            if (epoch != _epoch) return; // bloqueado enquanto lia
            _entries = entries;
            Problem = problem;
            State = state;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Descarta tudo da memória (senhas incluídas). Chamar ao sair da página e ao fechar o app.</summary>
    public void Lock()
    {
        Interlocked.Increment(ref _epoch);
        _entries = Array.Empty<VaultEntry>();
        State = VaultState.NotLoaded;
        Problem = null;
    }

    private (VaultState, IReadOnlyList<VaultEntry>, string?) ReadFromDisk()
    {
        if (!File.Exists(_path)) return (VaultState.Ready, Array.Empty<VaultEntry>(), null);

        var main = TryRead(_path);
        if (main.Status == ReadStatus.Ok) return (VaultState.Ready, main.Entries!, null);

        // Arquivo corrompido (queda de energia, disco)? A cópia anterior (.bak) pode estar íntegra. Um blob com bits trocados
        // também aparece como "não consigo decifrar", então a cópia é tentada nos dois casos; se o problema for o DPAPI de
        // verdade (outra conta), o .bak falha do mesmo jeito e o resultado abaixo continua sendo "ilegível".
        if (File.Exists(_path + ".bak"))
        {
            var bak = TryRead(_path + ".bak");
            if (bak.Status == ReadStatus.Ok)
                return (VaultState.Ready, bak.Entries!, "O arquivo principal estava danificado; o cofre foi aberto a partir da cópia anterior " +
                                                        "(.bak). Alterações mais recentes podem ter sido perdidas.");
        }

        return (VaultState.Unreadable, Array.Empty<VaultEntry>(), main.Status == ReadStatus.CannotDecrypt
            ? "O Windows não conseguiu abrir o cofre com a sua conta atual. Isso acontece quando a senha do Windows foi redefinida por " +
              "outro administrador, o perfil foi recriado ou o arquivo veio de outro PC/usuário. O arquivo NÃO foi alterado. " +
              "Se você tem um backup (.fkvault), restaure-o com a senha que escolheu ao exportar."
            : "O arquivo do cofre está corrompido e não há cópia íntegra. O arquivo NÃO foi alterado. Restaure um backup (.fkvault) se tiver.");
    }

    private enum ReadStatus { Ok, CannotDecrypt, Corrupt }
    private readonly record struct ReadResult(ReadStatus Status, List<VaultEntry>? Entries);

    private static ReadResult TryRead(string file)
    {
        byte[]? plain = null;
        try
        {
            var raw = File.ReadAllBytes(file);
            if (raw.Length <= FileMagic.Length || !raw.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
                return new(ReadStatus.Corrupt, null);
            plain = ProtectedData.Unprotect(raw.AsSpan(FileMagic.Length).ToArray(), Entropy, DataProtectionScope.CurrentUser);
            var model = JsonSerializer.Deserialize<VaultFile>(plain, Json);
            return model?.Entries == null ? new(ReadStatus.Corrupt, null) : new(ReadStatus.Ok, model.Entries);
        }
        catch (CryptographicException) { return new(ReadStatus.CannotDecrypt, null); }
        catch (JsonException) { return new(ReadStatus.Corrupt, null); }
        catch (IOException) { return new(ReadStatus.Corrupt, null); }
        finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public List<VaultEntry> Entries { get; set; } = new();
    }

    // ------------------------------------------------------------------ Consulta (memória, síncrona e barata)

    /// <summary>Filtra por título, usuário, URL, categoria e notas. Todos os termos digitados precisam aparecer.</summary>
    public List<VaultItem> Search(string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _entries
            .Where(e => terms.All(t => Contains(e.Title, t) || Contains(e.Username, t) || Contains(e.Url, t)
                                       || Contains(e.Category, t) || Contains(e.Notes, t)))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new VaultItem(e.Id, e.Title, e.Username, e.Url, e.Category, e.Modified))
            .ToList();

        static bool Contains(string s, string t) => s.Contains(t, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Cópia do registro (para o formulário de edição). Alterar a cópia não muda o cofre.</summary>
    public VaultEntry? Get(Guid id) => _entries.FirstOrDefault(e => e.Id == id)?.Clone();

    public string? GetPassword(Guid id) => _entries.FirstOrDefault(e => e.Id == id)?.Password;
    public string? GetUsername(Guid id) => _entries.FirstOrDefault(e => e.Id == id)?.Username;

    // ------------------------------------------------------------------ CRUD

    public Task<Guid> AddAsync(VaultEntry entry, CancellationToken ct = default)
        => MutateAsync(list =>
        {
            var e = Normalize(entry.Clone());
            e.Id = Guid.NewGuid();
            e.Created = e.Modified = DateTime.UtcNow;
            list.Add(e);
            return e.Id;
        }, ct);

    public async Task<bool> UpdateAsync(VaultEntry entry, CancellationToken ct = default)
        => await MutateAsync(list =>
        {
            int i = list.FindIndex(x => x.Id == entry.Id);
            if (i < 0) return false;
            var e = Normalize(entry.Clone());
            e.Created = list[i].Created;
            e.Modified = DateTime.UtcNow;
            list[i] = e;
            return true;
        }, ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await MutateAsync(list => list.RemoveAll(x => x.Id == id) > 0, ct);

    /// <summary>
    /// Importa registros externos, mesclando por URL + usuário e preservando a versão mais recentemente
    /// alterada. Os dados são gravados uma única vez.
    /// </summary>
    public async Task<int> ImportEntriesAsync(IEnumerable<VaultEntry> entries, CancellationToken ct = default)
    {
        var imported = entries.Select(Normalize).ToList();
        return await MutateAsync(list =>
        {
            int changed = 0;
            foreach (var importedEntry in imported)
            {
                if (string.IsNullOrWhiteSpace(importedEntry.Password)) continue;

                int existing = list.FindIndex(existingEntry =>
                    string.Equals(existingEntry.Url.Trim().TrimEnd('/'), importedEntry.Url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existingEntry.Username, importedEntry.Username, StringComparison.OrdinalIgnoreCase));

                if (existing >= 0)
                {
                    if (string.Equals(list[existing].Password, importedEntry.Password, StringComparison.Ordinal)) continue;
                    var merge = list[existing].Clone();
                    merge.Password = importedEntry.Password;
                    merge.Title = string.IsNullOrWhiteSpace(merge.Title) ? importedEntry.Title : merge.Title;
                    merge.Category = string.IsNullOrWhiteSpace(merge.Category) ? importedEntry.Category : merge.Category;
                    merge.Modified = DateTime.UtcNow;
                    list[existing] = merge;
                    changed++;
                }
                else
                {
                    var e = importedEntry.Clone();
                    e.Id = Guid.NewGuid();
                    e.Created = e.Modified = DateTime.UtcNow;
                    list.Add(e);
                    changed++;
                }
            }
            return changed;
        }, ct);
    }

    private static VaultEntry Normalize(VaultEntry e)
    {
        e.Title = e.Title.Trim();
        if (e.Title.Length == 0) throw new ArgumentException("Dê um nome ao registro.");
        e.Username = e.Username.Trim();
        e.Url = e.Url.Trim();
        e.Category = e.Category.Trim();
        return e;
    }

    /// <summary>Muda uma CÓPIA da lista, grava em disco e só então publica a cópia — se a gravação falhar, a memória continua igual ao disco.</summary>
    private async Task<T> MutateAsync<T>(Func<List<VaultEntry>, T> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireReady();
            int epoch = _epoch;
            var next = _entries.ToList();
            var result = change(next);
            await Task.Run(() => WriteToDisk(next), ct);
            if (epoch == _epoch) { _entries = next; Problem = null; } // gravou: o arquivo principal voltou a ser válido
            return result;
        }
        finally { _gate.Release(); }
    }

    private void RequireReady()
    {
        if (State == VaultState.Unreadable)
            throw new InvalidOperationException("O cofre não pôde ser aberto; por segurança nada será gravado. Restaure um backup.");
        if (State != VaultState.Ready)
            throw new InvalidOperationException("O cofre está bloqueado. Abra a página do Vault de novo.");
    }

    private void WriteToDisk(List<VaultEntry> list)
    {
        byte[]? plain = null;
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(new VaultFile { Entries = list }, Json);
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(FileMagic);
                fs.Write(blob);
                fs.Flush(flushToDisk: true); // força ir ao disco antes da troca
            }
            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
            else File.Move(tmp, _path);
        }
        finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    // ------------------------------------------------------------------ Backup com senha própria (plano B do DPAPI)

    /// <summary>Exporta tudo para um arquivo .fkvault cifrado com AES-256-GCM, chave derivada da senha por PBKDF2.</summary>
    public async Task ExportBackupAsync(string file, string passphrase, CancellationToken ct = default)
    {
        CheckPassphrase(passphrase);
        await _gate.WaitAsync(ct);
        try
        {
            RequireReady();
            var snapshot = _entries.ToList();
            await Task.Run(() =>
            {
                byte[]? plain = null;
                try
                {
                    plain = JsonSerializer.SerializeToUtf8Bytes(new VaultFile { Entries = snapshot }, Json);
                    File.WriteAllBytes(file, EncryptBackup(plain, passphrase));
                }
                finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
            }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Importa um .fkvault. Cofre normal: mescla por Id (vence o mais recente). Cofre ilegível: o arquivo antigo é
    /// guardado como vault.dat.ilegivel-AAAAMMDDHHMMSS e um cofre novo é criado com o conteúdo do backup.
    /// </summary>
    public async Task<int> ImportBackupAsync(string file, string passphrase, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State == VaultState.NotLoaded) throw new InvalidOperationException("O cofre está bloqueado. Abra a página do Vault de novo.");
            int epoch = _epoch;

            var imported = await Task.Run(() =>
            {
                byte[]? plain = null;
                try
                {
                    plain = DecryptBackup(File.ReadAllBytes(file), passphrase);
                    return JsonSerializer.Deserialize<VaultFile>(plain, Json)?.Entries
                           ?? throw new InvalidDataException("O backup está vazio ou corrompido.");
                }
                catch (JsonException) { throw new InvalidDataException("O backup está corrompido."); }
                finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
            }, ct);

            var next = State == VaultState.Ready ? _entries.ToList() : new List<VaultEntry>();
            foreach (var e in imported)
            {
                int i = next.FindIndex(x => x.Id == e.Id);
                if (i < 0) next.Add(e);
                else if (e.Modified > next[i].Modified) next[i] = e;
            }

            bool wasUnreadable = State == VaultState.Unreadable;
            await Task.Run(() =>
            {
                if (wasUnreadable && File.Exists(_path))
                    File.Move(_path, _path + ".ilegivel-" + DateTime.Now.ToString("yyyyMMddHHmmss")); // preserva, nunca apaga
                WriteToDisk(next);
            }, ct);

            if (epoch == _epoch)
            {
                _entries = next;
                State = VaultState.Ready;
                Problem = null;
            }
            return imported.Count;
        }
        finally { _gate.Release(); }
    }

    private static void CheckPassphrase(string p)
    {
        if (p.Length < MinBackupPassphrase)
            throw new ArgumentException($"Use uma senha de backup com pelo menos {MinBackupPassphrase} caracteres.");
    }

    // Formato: "FKB1" | salt(16) | nonce(12) | tag(16) | ciphertext. O cabeçalho entra como dado autenticado (AAD).
    private static byte[] EncryptBackup(byte[] plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, BackupMagic);

            var output = new byte[4 + 16 + 12 + 16 + cipher.Length];
            BackupMagic.CopyTo(output, 0);
            salt.CopyTo(output, 4);
            nonce.CopyTo(output, 20);
            tag.CopyTo(output, 32);
            cipher.CopyTo(output, 48);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] DecryptBackup(byte[] data, string passphrase)
    {
        if (data.Length < 48 || !data.AsSpan(0, 4).SequenceEqual(BackupMagic))
            throw new InvalidDataException("Este não é um backup do Vault (.fkvault).");
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, data.AsSpan(4, 16), Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var plain = new byte[data.Length - 48];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(20, 12), data.AsSpan(48), data.AsSpan(32, 16), plain, BackupMagic);
            return plain;
        }
        catch (CryptographicException) { throw new InvalidDataException("Senha do backup incorreta ou arquivo corrompido."); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

/// <summary>Gerador de senhas com RandomNumberGenerator (criptograficamente seguro; sem System.Random).</summary>
public static class PasswordGenerator
{
    private const string Lower = "abcdefghijkmnopqrstuvwxyz"; // sem 'l'
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // sem 'I' e 'O'
    private const string Digits = "23456789";                   // sem 0 e 1
    private const string Symbols = "!@#$%&*?-_=+";

    public static string Generate(int length = 20)
    {
        length = Math.Clamp(length, 8, 128);
        string[] sets = { Lower, Upper, Digits, Symbols };
        string all = string.Concat(sets);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = i < sets.Length ? Pick(sets[i]) : Pick(all); // garante ao menos um de cada tipo
        for (int i = length - 1; i > 0; i--)                         // Fisher–Yates: mistura as posições
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);

        static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];
    }
}
