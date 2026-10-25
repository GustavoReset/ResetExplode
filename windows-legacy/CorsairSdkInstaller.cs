using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ResetExplode;

/// <summary>
/// Baixa o SDK oficial da Corsair (github.com/CorsairOfficial/cue-sdk, última release) e extrai SOMENTE o iCUESDK.x64_2019.dll
/// para %AppData%\ResetExplode. Só roda quando o usuário confirma. A assinatura digital da DLL é verificada pelo Windows:
/// assinatura inválida ou de outro fabricante apaga o arquivo e cancela; sem assinatura gera só um aviso.
/// </summary>
public static class CorsairSdkInstaller
{
    private const string Repo = "CorsairOfficial/cue-sdk";
    private const string DllName = "iCUESDK.x64_2019.dll";

    public static string TargetPath => Path.Combine(AppSettings.Folder, DllName);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ResetExplode");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Baixa, extrai e confere a DLL. Devolve um resumo para mostrar na tela; lança exceção com a explicação se falhar.</summary>
    public static async Task<string> InstallAsync(Action<string> progress, CancellationToken ct = default)
    {
        progress("Consultando a última versão do SDK no GitHub…");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "?" : "?";

        // Candidatos: anexos .zip da release (preferindo os que citam o SDK) e, por último, o código-fonte da versão.
        var urls = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && url.StartsWith("https://")) urls.Add((name, url));
            }
        urls = urls.OrderByDescending(x => x.Name.Contains("sdk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (root.TryGetProperty("zipball_url", out var z) && z.GetString() is string zip && zip.StartsWith("https://"))
            urls.Add(("código-fonte " + tag, zip));
        if (urls.Count == 0) throw new InvalidOperationException("A release não tem nenhum arquivo .zip para baixar.");

        string tmpDir = Path.Combine(Path.GetTempPath(), "ResetExplode-cue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            foreach (var (name, url) in urls)
            {
                string zipPath = Path.Combine(tmpDir, "sdk.zip");
                progress($"Baixando {name}…");
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zipPath);
                    var buf = new byte[81920];
                    long done = 0; int n; long lastReport = 0;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (done - lastReport > 1_000_000)
                        {
                            lastReport = done;
                            progress(total > 0 ? $"Baixando {name}… {done / 1_048_576.0:0.0} de {total / 1_048_576.0:0.0} MB" : $"Baixando {name}… {done / 1_048_576.0:0.0} MB");
                        }
                    }
                }

                string? extracted = ExtractDll(zipPath, tmpDir);
                if (extracted == null) continue; // não está neste pacote: tenta o próximo

                progress("Verificando a assinatura digital…");
                string verdict = Verify(extracted);
                Directory.CreateDirectory(AppSettings.Folder);
                File.Copy(extracted, TargetPath, true);
                return $"SDK {tag} instalado em {TargetPath}. {verdict}";
            }
            throw new InvalidOperationException($"Não achei o {DllName} dentro da última release ({tag}). Baixe o SDK manualmente em github.com/{Repo}/releases.");
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    private static string? ExtractDll(string zipPath, string tmpDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries
            .Where(e => string.Equals(e.Name, DllName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.FullName.Contains("x64", StringComparison.OrdinalIgnoreCase) && e.FullName.Contains("redist", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (entry == null) return null;
        string outPath = Path.Combine(tmpDir, DllName);
        entry.ExtractToFile(outPath, true); // só esta DLL é extraída; nenhum outro arquivo do pacote toca o disco
        return outPath;
    }

    /// <summary>Confere o arquivo; lança exceção se for inválido ou de outro fabricante, e devolve o aviso quando não tem assinatura.</summary>
    private static string Verify(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 100_000) throw new InvalidOperationException("O arquivo extraído é pequeno demais para ser a DLL do SDK.");
        using (var fs = File.OpenRead(path))
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidOperationException("O arquivo extraído não é uma DLL do Windows.");

        int trust = WinVerifyTrustFile(path);
        if (trust == unchecked((int)0x800B0100)) return "Aviso: a DLL não tem assinatura digital (origem: repositório oficial da Corsair no GitHub).";
        if (trust != 0) throw new InvalidOperationException($"A assinatura digital da DLL é inválida (0x{trust:X8}). O arquivo foi descartado.");

        string signer = "";
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
            signer = new X509Certificate2(cert).Subject;
#pragma warning restore SYSLIB0057
        }
        catch { }
        if (!signer.Contains("Corsair", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A DLL está assinada por outro fabricante ({signer}). O arquivo foi descartado.");
        return "Assinatura digital da Corsair válida.";
    }

    // ---- WinVerifyTrust (verificação de assinatura Authenticode feita pelo próprio Windows) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    internal static int WinVerifyTrustFile(string path)
    {
        var file = new WINTRUST_FILE_INFO { pcwszFilePath = path };
        file.cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,       // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10,      // WTD_REVOCATION_CHECK_NONE
            };
            data.cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
            return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
