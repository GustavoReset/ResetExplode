using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FortKnox.Muralha;

public enum Verdict { Limpo, Suspeito, Malicioso }

/// <summary>Resultado de um arquivo: veredito, a camada que decidiu e o motivo em linguagem clara.</summary>
public sealed record ScanResult(string Path, Verdict Verdict, string Layer, string Reason, string Sha256, long Size);

/// <summary>Regra de conteúdo: todos os grupos precisam bater; dentro de um grupo, basta uma alternativa.</summary>
public sealed record ContentRule(string Name, Verdict Verdict, IReadOnlySet<string>? Extensions, IReadOnlyList<IReadOnlyList<string>> AllOf);

/// <summary>
/// Base de assinaturas da MURALHA em formato próprio (docs/ARQUITETURA.md §9), em texto:
/// <c>hash &lt;sha256&gt; &lt;nome&gt;</c> e <c>rule &lt;nome&gt; | &lt;Suspeito|Malicioso&gt; | &lt;.ext,.ext ou *&gt; | &lt;padrão || alternativa&gt; &amp;&amp; &lt;padrão&gt;</c>.
/// A base embutida começa pequena de propósito (só o que dá para justificar); a segunda opinião do Defender completa.
/// </summary>
public sealed class SignatureBase
{
    public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ContentRule> Rules { get; } = new();
    public string Version { get; private set; } = "";

    public static SignatureBase Parse(string text)
    {
        var b = new SignatureBase();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("# versão", StringComparison.OrdinalIgnoreCase)) b.Version = line[8..].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("hash ", StringComparison.Ordinal))
            {
                var parts = line[5..].Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts[0].Length == 64) b.Hashes[parts[0]] = parts.Length > 1 ? parts[1] : "Hash conhecido";
            }
            else if (line.StartsWith("rule ", StringComparison.Ordinal))
            {
                var f = line[5..].Split(" | ", 4, StringSplitOptions.TrimEntries);
                if (f.Length < 4 || !Enum.TryParse<Verdict>(f[1], out var v)) continue;
                var exts = f[2] == "*" ? null : f[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var groups = f[3].Split(" && ", StringSplitOptions.TrimEntries)
                                 .Select(g => (IReadOnlyList<string>)g.Split(" || ", StringSplitOptions.TrimEntries).Select(Unescape).ToList())
                                 .ToList();
                b.Rules.Add(new ContentRule(f[0], v, exts, groups));
            }
        }
        return b;
    }

    /// <summary>\x41 vira o byte 0x41 (para padrões que não podem aparecer inteiros no código-fonte, como o EICAR).</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains("\\x")) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' &&
                int.TryParse(s.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out int c))
            { sb.Append((char)c); i += 3; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Base embutida no app.</summary>
    public static SignatureBase Embedded() => Parse(EmbeddedText);

    // O EICAR é o arquivo de teste padrão da indústria (inofensivo). O padrão vai com um byte escapado (\x58 = X) para este
    // arquivo-fonte não ser confundido com o próprio arquivo de teste.
    public const string EmbeddedText = """
        # versão 2026.11.1
        # Hashes SHA-256
        hash 275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f Arquivo de teste EICAR (inofensivo)
        # Regras de conteúdo
        rule Arquivo de teste EICAR (inofensivo) | Malicioso | * | \x585O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*
        rule PowerShell escondido com comando codificado | Suspeito | .ps1,.bat,.cmd,.vbs,.vbe,.js,.jse,.wsf,.hta | powershell || pwsh && -enc || -encodedcommand && -w hidden || -windowstyle hidden || -win hidden
        rule Baixa e executa código da internet | Suspeito | .ps1,.bat,.cmd,.vbs,.vbe,.js,.jse,.wsf,.hta | downloadstring || downloadfile || invoke-webrequest || net.webclient || start-bitstransfer && iex || invoke-expression
        rule Desliga a proteção do Windows Defender | Suspeito | .ps1,.bat,.cmd,.vbs,.vbe,.js,.jse,.wsf,.hta | set-mppreference || add-mppreference || disableantispyware && disable || exclusion
        rule Apaga as cópias de sombra (comum em ransomware) | Suspeito | .ps1,.bat,.cmd,.vbs,.vbe,.js,.jse,.wsf,.hta | vssadmin delete shadows || wmic shadowcopy delete || wbadmin delete catalog
        """;
}

/// <summary>
/// Motor de varredura da MURALHA (Fase 3.5a). Sem WPF: roda no app agora e no serviço Guarda depois.
/// Camadas, da mais barata à mais cara: 1 assinatura digital válida → limpo; 2 hash conhecido; 3 regras de conteúdo;
/// 4 heurística de executável sem assinatura; 5 segunda opinião do Microsoft Defender para o que ficou suspeito.
/// Nunca apaga nada: só diz o veredito e o motivo. Complementa o Defender, não o substitui.
/// </summary>
public sealed class MuralhaEngine(SignatureBase signatures)
{
    public const long MaxFileSize = 64L * 1024 * 1024;
    private const int ContentBytes = 2 * 1024 * 1024;

    /// <summary>Tipos que a varredura olha (executáveis, scripts, instaladores, documentos com macro).</summary>
    public static readonly IReadOnlySet<string> RiskyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".com", ".pif", ".sys", ".cpl", ".msi", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".hta", ".jar", ".docm", ".xlsm", ".pptm",
    };

    private static readonly IReadOnlySet<string> PeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".scr", ".com", ".pif", ".sys", ".cpl" };

    private static readonly IReadOnlySet<string> SignableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".scr", ".sys", ".cpl", ".msi", ".ps1", ".psm1" };

    public SignatureBase Signatures => signatures;

    /// <summary>Usa o Defender como segunda opinião (desligável para testes).</summary>
    public bool UseDefender { get; init; } = true;

    /// <summary>Verifica um arquivo do disco.</summary>
    public async Task<ScanResult> ScanFileAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        string ext = info.Extension;
        if (info.Length > MaxFileSize) return new(path, Verdict.Limpo, "tamanho", "Arquivo grande demais para esta varredura (pulado).", "", info.Length);

        // 1. Assinatura digital válida: o editor se responsabiliza pelo arquivo.
        if (SignableExtensions.Contains(ext) && Trust.IsSignedAndValid(path))
            return new(path, Verdict.Limpo, "assinatura", "Assinatura digital válida.", "", info.Length);

        string sha;
        byte[] head;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
        {
            sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            fs.Position = 0;
            head = new byte[(int)Math.Min(ContentBytes, info.Length)];
            int read = 0;
            while (read < head.Length)
            {
                int n = await fs.ReadAsync(head.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
        }

        var r = Evaluate(path, ext, sha, head, info.Length);
        if (r.Verdict == Verdict.Suspeito && UseDefender && Defender.Available)
        {
            bool? threat = await Defender.ScanFileAsync(path, ct);
            if (threat == true) r = r with { Verdict = Verdict.Malicioso, Layer = "defender", Reason = r.Reason + " O Microsoft Defender também acusou ameaça." };
            else if (threat == false) r = r with { Reason = r.Reason + " O Microsoft Defender não encontrou ameaça conhecida." };
            else r = r with { Reason = r.Reason + " A segunda opinião do Defender não está disponível (ele pode estar desligado por outro antivírus)." };
        }
        return r;
    }

    /// <summary>Camadas 2 a 4 sobre o conteúdo já lido (testável sem disco).</summary>
    public ScanResult Evaluate(string path, string ext, string sha256, ReadOnlySpan<byte> head, long size)
    {
        // 2. Hash conhecido.
        if (signatures.Hashes.TryGetValue(sha256, out var hashName))
            return new(path, Verdict.Malicioso, "hash", $"{hashName}: o arquivo é idêntico a um item da base.", sha256, size);

        // 3. Regras de conteúdo (texto Latin-1, sem diferenciar maiúsculas).
        string text = Encoding.Latin1.GetString(head);
        ContentRule? hit = null;
        foreach (var rule in signatures.Rules)
        {
            if (rule.Extensions != null && !rule.Extensions.Contains(ext)) continue;
            if (rule.AllOf.All(g => g.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                if (hit == null || rule.Verdict > hit.Verdict) hit = rule;
                if (hit.Verdict == Verdict.Malicioso) break;
            }
        }
        if (hit != null) return new(path, hit.Verdict, "regra", $"{hit.Name}.", sha256, size);

        // 4. Heurística de executável sem assinatura (precisa de dois sinais para não acusar à toa).
        if (PeExtensions.Contains(ext) || (head.Length > 2 && head[0] == 'M' && head[1] == 'Z'))
        {
            var signs = PeHeuristics.Signs(head, path);
            if (signs.Count >= 2)
                return new(path, Verdict.Suspeito, "heurística", "Executável sem assinatura: " + string.Join("; ", signs) + ".", sha256, size);
        }
        return new(path, Verdict.Limpo, "", "", sha256, size);
    }

    // ------------------------------------------------------------------ o que varrer

    /// <summary>Varredura rápida: onde malware costuma cair ou se instalar.</summary>
    public static IReadOnlyList<string> QuickRoots()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string user = F(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(user, "Downloads"), F(Environment.SpecialFolder.DesktopDirectory), Path.GetTempPath(),
            F(Environment.SpecialFolder.Startup), F(Environment.SpecialFolder.CommonStartup),
            F(Environment.SpecialFolder.ApplicationData), Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Arquivos dos tipos de risco nas pastas, sem atravessar junctions/symlinks e sem baixar arquivos que estão só na nuvem
    /// (OneDrive). maxDepth limita a descida (AppData é enorme).
    /// </summary>
    public static IEnumerable<string> Enumerate(IEnumerable<string> roots, int maxDepth, CancellationToken ct)
    {
        const FileAttributes CloudOnly = (FileAttributes)0x400000 | FileAttributes.Offline; // RECALL_ON_DATA_ACCESS
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = stack.Pop();
                if (!seen.Add(dir)) continue;
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var e in entries)
                {
                    if (e.Attributes.HasFlag(FileAttributes.ReparsePoint) || (e.Attributes & CloudOnly) != 0) continue;
                    if (e is DirectoryInfo d) { if (depth < maxDepth) stack.Push((d.FullName, depth + 1)); }
                    else if (RiskyExtensions.Contains(e.Extension)) yield return e.FullName;
                }
            }
        }
    }
}

/// <summary>Sinais de executável empacotado ou fora do lugar. Só lê o cabeçalho PE já carregado.</summary>
public static class PeHeuristics
{
    private static readonly string[] PackerSections = { "UPX0", "UPX1", ".aspack", ".adata", ".MPRESS1", ".MPRESS2", ".petite", ".nsp0", ".nsp1", ".themida", ".vmp0", ".vmp1" };

    public static List<string> Signs(ReadOnlySpan<byte> pe, string path)
    {
        var signs = new List<string>();
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return signs;
        int lfanew = BitConverter.ToInt32(pe[0x3C..]);
        if (lfanew <= 0 || lfanew + 24 > pe.Length || pe[lfanew] != 'P' || pe[lfanew + 1] != 'E') return signs;
        int sections = BitConverter.ToUInt16(pe[(lfanew + 6)..]);
        int optSize = BitConverter.ToUInt16(pe[(lfanew + 20)..]);
        int table = lfanew + 24 + optSize;
        bool packed = false, highEntropy = false, writableCode = false;
        for (int i = 0; i < Math.Min(sections, 96); i++)
        {
            int s = table + i * 40;
            if (s + 40 > pe.Length) break;
            string name = Encoding.ASCII.GetString(pe.Slice(s, 8)).TrimEnd('\0');
            uint rawSize = BitConverter.ToUInt32(pe[(s + 16)..]), rawPtr = BitConverter.ToUInt32(pe[(s + 20)..]);
            uint flags = BitConverter.ToUInt32(pe[(s + 36)..]);
            if (PackerSections.Contains(name, StringComparer.OrdinalIgnoreCase)) packed = true;
            bool exec = (flags & 0x20000000) != 0, write = (flags & 0x80000000) != 0;
            if (exec && write) writableCode = true;
            if (exec && rawSize >= 4096 && rawPtr + rawSize <= pe.Length && Entropy(pe.Slice((int)rawPtr, (int)rawSize)) > 7.2) highEntropy = true;
        }
        if (packed) signs.Add("empacotado com compressor de executáveis");
        if (highEntropy) signs.Add("código cifrado ou comprimido (entropia alta)");
        if (writableCode) signs.Add("seção de código que se modifica");
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.StartsWith(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            dir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StringComparison.OrdinalIgnoreCase) ||
            dir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StringComparison.OrdinalIgnoreCase))
            signs.Add("está numa pasta temporária ou de inicialização");
        return signs;
    }

    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (byte b in data) counts[b]++;
        double e = 0;
        foreach (int c in counts)
        {
            if (c == 0) continue;
            double p = (double)c / data.Length;
            e -= p * Math.Log2(p);
        }
        return e;
    }
}

/// <summary>Assinatura Authenticode (WinVerifyTrust), sem consultar revogação na internet.</summary>
public static class Trust
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
        public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
        public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsSignedAndValid(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1,
                pFile = pFile, dwStateAction = 0, dwProvFlags = 0x10 | 0x1000, // sem revogação, só cache de URL (offline)
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data) == 0;
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}

/// <summary>Segunda opinião do Microsoft Defender (MpCmdRun), só para o que já ficou suspeito. Não remove nada.</summary>
public static class Defender
{
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
    public static bool Available => File.Exists(Path);

    /// <summary>
    /// Lê a resposta do MpCmdRun. O código de saída sozinho não basta: 2 também sai quando o Defender está desligado
    /// (outro antivírus ativo) ou sem permissão. Por isso só vale o texto "found no threats" ou "found N threats".
    /// </summary>
    public static bool? Interpret(string output)
    {
        if (output.Contains("found no threats", StringComparison.OrdinalIgnoreCase)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(output, @"found (\d+) threats?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value) > 0;
        return null;
    }

    /// <returns>true = ameaça, false = limpo, null = não deu para perguntar.</returns>
    public static async Task<bool?> ScanFileAsync(string file, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-Scan", "-ScanType", "3", "-File", file, "-DisableRemediation" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var outTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; return null; }
            return Interpret(await outTask);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }
}
