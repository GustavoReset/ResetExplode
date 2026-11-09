using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FortKnox.Muralha;

/// <summary>Um item na quarentena (os metadados ficam ao lado do arquivo cifrado).</summary>
public sealed record QuarantineItem(Guid Id, string OriginalPath, string Sha256, long Size, DateTime QuarantinedAt, Verdict Verdict, string Reason);

/// <summary>
/// Quarentena da MURALHA (docs/ARQUITETURA.md §9.1). O arquivo é cifrado com AES-256-GCM e só então o original é apagado:
/// cifrado ele não roda nem é aberto por engano. A chave é aleatória, guardada protegida por DPAPI (escopo da máquina) e
/// com entropia própria. Sempre dá para restaurar para o lugar de origem ou excluir de vez.
/// </summary>
public sealed class Quarantine
{
    private static readonly byte[] Magic = "FKQ1"u8.ToArray();
    private static readonly byte[] KeyEntropy = "FortKnox.Muralha.Quarentena.v1"u8.ToArray();
    private readonly string _folder;
    private byte[]? _key;

    public Quarantine(string folder) { _folder = folder; }

    /// <summary>%ProgramData%\FortKnox\Quarentena (fica fora do perfil: vale para todas as contas do PC).</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FortKnox", "Quarentena");

    public string Folder => _folder;

    private string DataPath(Guid id) => Path.Combine(_folder, id.ToString("N") + ".fkq");
    private string MetaPath(Guid id) => Path.Combine(_folder, id.ToString("N") + ".json");

    private byte[] Key()
    {
        if (_key != null) return _key;
        Directory.CreateDirectory(_folder);
        string file = Path.Combine(_folder, "chave.bin");
        if (File.Exists(file))
            _key = ProtectedData.Unprotect(File.ReadAllBytes(file), KeyEntropy, DataProtectionScope.LocalMachine);
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(file, ProtectedData.Protect(_key, KeyEntropy, DataProtectionScope.LocalMachine));
        }
        return _key;
    }

    /// <summary>Cifra o arquivo na quarentena e apaga o original. Se não der para apagar, desfaz e avisa.</summary>
    public QuarantineItem Add(string path, Verdict verdict, string reason)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("O arquivo não existe mais.", path);
        if (info.Length > MuralhaEngine.MaxFileSize) throw new InvalidOperationException("Arquivo grande demais para a quarentena.");
        byte[] plain = File.ReadAllBytes(path);
        var item = new QuarantineItem(Guid.NewGuid(), info.FullName, Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant(),
                                      plain.Length, DateTime.Now, verdict, reason);
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(item.Id.ToString("N")));
        CryptographicOperations.ZeroMemory(plain);

        using (var fs = File.Create(DataPath(item.Id)))
        {
            fs.Write(Magic); fs.Write(nonce); fs.Write(tag); fs.Write(cipher);
        }
        File.WriteAllText(MetaPath(item.Id), JsonSerializer.Serialize(item));
        try
        {
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
            info.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Remove(item.Id);
            throw new IOException($"Não foi possível tirar o arquivo do lugar ({e.Message}). Ele pode estar aberto ou exigir administrador.", e);
        }
        return item;
    }

    public List<QuarantineItem> List()
    {
        if (!Directory.Exists(_folder)) return new();
        var list = new List<QuarantineItem>();
        foreach (var f in Directory.GetFiles(_folder, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<QuarantineItem>(File.ReadAllText(f)) is { } it && File.Exists(DataPath(it.Id))) list.Add(it); }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return list.OrderByDescending(i => i.QuarantinedAt).ToList();
    }

    /// <summary>Devolve o arquivo ao lugar de origem. Se já existir um arquivo lá, restaura ao lado com "(restaurado)" no nome.</summary>
    public string Restore(Guid id)
    {
        var item = JsonSerializer.Deserialize<QuarantineItem>(File.ReadAllText(MetaPath(id))) ?? throw new InvalidOperationException("Item inválido.");
        byte[] blob = File.ReadAllBytes(DataPath(id));
        if (blob.Length < 32 || !blob.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Arquivo da quarentena corrompido.");
        var nonce = blob.AsSpan(4, 12); var tag = blob.AsSpan(16, 16); var cipher = blob.AsSpan(32);
        byte[] plain = new byte[cipher.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(id.ToString("N")));

        string target = item.OriginalPath;
        if (File.Exists(target))
            target = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)} (restaurado){Path.GetExtension(target)}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, plain);
        CryptographicOperations.ZeroMemory(plain);
        Remove(id);
        return target;
    }

    /// <summary>Exclui de vez (a tela confirma antes: não tem volta).</summary>
    public void Delete(Guid id) => Remove(id);

    private void Remove(Guid id)
    {
        try { File.Delete(DataPath(id)); } catch (IOException) { }
        try { File.Delete(MetaPath(id)); } catch (IOException) { }
    }
}
