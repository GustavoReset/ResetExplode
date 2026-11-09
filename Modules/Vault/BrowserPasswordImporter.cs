using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FortKnox;

public sealed record BrowserDefinition(string Id, string Name, string Letter, string[] ProfileFolderNames);

public sealed record BrowserImportResult(int CredentialsImported, int ProfilesRead, string? Warning);

public static class BrowserPasswordImporter
{
    public static readonly BrowserDefinition[] Browsers =
    {
        new("chrome", "Google Chrome", "C", new[] { "Google\\Chrome" }),
        new("edge", "Microsoft Edge", "E", new[] { "Microsoft\\Edge" }),
        new("firefox", "Mozilla Firefox", "F", new[] { "Mozilla\\Firefox" }),
        new("opera", "Opera", "O", new[] { "Opera Software" }),
        new("operagx", "Opera GX", "G", new[] { "Opera Software" }),
        new("brave", "Brave", "B", new[] { "BraveSoftware\\Brave-Browser" }),
    };

    public static string? FindExecutable(BrowserDefinition browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates = browser.Id switch
        {
            "chrome" => new[]
            {
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe")
            },
            "edge" => new[]
            {
                Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe")
            },
            "firefox" => new[]
            {
                Path.Combine(programFiles, @"Mozilla Firefox\firefox.exe"),
                Path.Combine(programFilesX86, @"Mozilla Firefox\firefox.exe"),
                Path.Combine(local, @"Mozilla Firefox\firefox.exe")
            },
            "opera" => new[]
            {
                Path.Combine(local, @"Programs\Opera\opera.exe"),
                Path.Combine(programFiles, @"Opera\opera.exe")
            },
            "operagx" => new[]
            {
                Path.Combine(local, @"Programs\Opera GX\opera.exe"),
                Path.Combine(local, @"Programs\Opera GX\opera.exe")
            },
            "brave" => new[]
            {
                Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe")
            },
            _ => Array.Empty<string>()
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> FindChromiumProfiles(BrowserDefinition browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? userData = browser.Id switch
        {
            "chrome" => Path.Combine(local, @"Google\Chrome\User Data"),
            "edge" => Path.Combine(local, @"Microsoft\Edge\User Data"),
            "brave" => Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"),
            "opera" => Path.Combine(roaming, @"Opera Software\Opera Stable"),
            "operagx" => Path.Combine(roaming, @"Opera Software\Opera GX Stable"),
            _ => null
        };
        if (userData == null || !Directory.Exists(userData)) return Array.Empty<string>();

        if (browser.Id is "opera" or "operagx")
            return File.Exists(Path.Combine(userData, "Login Data")) ? new[] { userData } : Array.Empty<string>();

        return Directory.EnumerateDirectories(userData)
            .Where(p => Path.GetFileName(p).Equals("Default", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(p).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            .Concat(new[] { userData })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(Path.Combine(p, "Login Data")))
            .ToList();
    }

    public static Task<IReadOnlyList<VaultEntry>> ImportChromiumAsync(BrowserDefinition browser, string? selectedExecutable = null, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<VaultEntry>>(() =>
        {
            var profiles = FindChromiumProfiles(browser);
            var entries = new List<VaultEntry>();
            if (profiles.Count == 0) throw new InvalidDataException($"Nenhum perfil com senhas foi encontrado para {browser.Name}.");

            string? userData = browser.Id switch
            {
                "opera" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Stable"),
                "operagx" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera GX Stable"),
                _ => profiles.Count > 0 ? Directory.GetParent(profiles.First(p => Path.GetFileName(p) != ""))
                    ?.FullName : null
            };
            if (browser.Id is not ("opera" or "operagx"))
                userData = Path.GetDirectoryName(profiles[0]);

            byte[]? masterKey = null;
            if (userData != null && File.Exists(Path.Combine(userData, "Local State")))
                masterKey = ReadChromiumMasterKey(Path.Combine(userData, "Local State"));

            foreach (string profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                string loginDb = Path.Combine(profile, "Login Data");
                string tempDb = Path.Combine(Path.GetTempPath(), $"fortknox-login-{Guid.NewGuid():N}.sqlite");
                try
                {
                    File.Copy(loginDb, tempDb, true);
                    CopyIfExists(loginDb + "-wal", tempDb + "-wal");
                    CopyIfExists(loginDb + "-shm", tempDb + "-shm");

                    using var db = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
                    db.Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins WHERE blacklisted_by_user = 0";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        string origin = reader.GetString(0);
                        string username = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(origin) || reader.IsDBNull(2)) continue;
                        byte[] encryptedPassword = reader.GetFieldValue<byte[]>(2);
                        string? password = DecryptChromiumPassword(encryptedPassword, masterKey);
                        if (password == null || password.Length == 0) continue;
                        entries.Add(new VaultEntry
                        {
                            Title = TryGetTitle(origin),
                            Username = username,
                            Password = password,
                            Url = origin,
                            Category = browser.Name
                        });
                    }
                }
                catch (SqliteException ex)
                {
                    throw new InvalidDataException($"Não foi possível ler o banco de senhas de {browser.Name}. Feche o navegador e tente novamente. {ex.Message}");
                }
                finally
                {
                    TryDelete(tempDb); TryDelete(tempDb + "-wal"); TryDelete(tempDb + "-shm");
                }
            }
            return entries;
        }, ct);

    private static byte[]? ReadChromiumMasterKey(string localState)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)
                || !osCrypt.TryGetProperty("encrypted_key", out var keyElement)) return null;
            string? keyText = keyElement.GetString();
            if (string.IsNullOrEmpty(keyText)) return null;
            byte[] encryptedKey = Convert.FromBase64String(keyText);
            if (!encryptedKey.AsSpan().StartsWith("DPAPI"u8.ToArray())) return null;
            return ProtectedData.Unprotect(encryptedKey.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    private static string? DecryptChromiumPassword(byte[] data, byte[]? masterKey)
    {
        try
        {
            if (data.AsSpan().StartsWith("v20"u8.ToArray()))
                throw new InvalidDataException("Este perfil usa proteção App-Bound do navegador e não pode ser aberto automaticamente.");
            if ((data.AsSpan().StartsWith("v10"u8.ToArray()) || data.AsSpan().StartsWith("v11"u8.ToArray())) && masterKey != null)
            {
                byte[] nonce = data.AsSpan(3, 12).ToArray();
                byte[] ciphertext = data.AsSpan(15, data.Length - 15).ToArray();
                byte[] plain = new byte[ciphertext.Length - 16];
                byte[] tag = ciphertext.AsSpan(ciphertext.Length - 16).ToArray();
                byte[] payload = ciphertext.AsSpan(0, ciphertext.Length - 16).ToArray();
                using var aes = new AesGcm(masterKey, 16);
                aes.Decrypt(nonce, payload, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
    }

    public static IReadOnlyList<VaultEntry> ImportCsv(string file)
    {
        string text = File.ReadAllText(file, new UTF8Encoding(false, true));
        if (text.Length >= 1 && text[0] == '\uFEFF') text = text[1..];
        var rows = ParseCsv(text);
        if (rows.Count == 0) throw new InvalidDataException("O arquivo CSV está vazio.");

        var header = rows[0].Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int name = Find(header, "name", "site", "title");
        int url = Find(header, "url", "origin_url", "hostname");
        int username = Find(header, "username", "user_name", "login", "email");
        int password = Find(header, "password", "senha");
        if (password < 0 || username < 0 || url < 0)
            throw new InvalidDataException("CSV inválido. Use o formato exportado pelo navegador: name,url,username,password.");

        var entries = new List<VaultEntry>();
        for (int i = 1; i < rows.Count; i++)
        {
            string Cell(int index) => index >= 0 && index < rows[i].Count ? rows[i][index].Trim() : "";
            string pass = Cell(password);
            if (pass.Length == 0) continue;
            string siteUrl = Cell(url);
            entries.Add(new VaultEntry
            {
                Title = name >= 0 && Cell(name).Length > 0 ? Cell(name) : TryGetTitle(siteUrl),
                Username = Cell(username),
                Password = pass,
                Url = siteUrl,
                Category = "Importado"
            });
        }
        return entries;
    }

    private static int Find(IReadOnlyList<string> header, params string[] names)
    {
        for (int i = 0; i < header.Count; i++)
            if (names.Contains(header[i], StringComparer.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (quoted || (cell.Length == 0)))
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((c == '\r' || c == '\n') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(v => v.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (row.Any(v => v.Length > 0)) rows.Add(row);
        }
        return rows;
    }

    private static string TryGetTitle(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.IsNullOrWhiteSpace(url) ? "Credencial importada" : url; }
    }

    private static void CopyIfExists(string source, string target)
    {
        if (File.Exists(source)) File.Copy(source, target, true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
