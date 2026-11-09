using System.Diagnostics;
using System.IO;
using System.Text;

namespace FortKnox;

/// <summary>Um programa com versão nova no winget.</summary>
public sealed record AppUpdate(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// ARSENAL · Atualizar apps (Fase 3.4d): lê "winget upgrade" e atualiza pelo próprio winget, que baixa do site oficial do
/// fabricante e confere o hash do instalador. Sem WPF (testável): o parser recebe o texto da saída.
/// </summary>
public static class UpdateService
{
    public static string WingetPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    public static bool WingetAvailable => File.Exists(WingetPath);

    private const string Common = "--accept-source-agreements --disable-interactivity";

    /// <summary>Lista as atualizações disponíveis (só os pacotes com versão conhecida, como o winget mostra por padrão).</summary>
    public static async Task<List<AppUpdate>> ListAsync(CancellationToken ct = default)
    {
        var (_, output) = await RunAsync($"upgrade {Common}", ct);
        return Parse(output);
    }

    /// <summary>
    /// Lê a tabela do winget. As colunas vêm traduzidas (Nome/Name, Versão/Version…), por isso as posições saem do cabeçalho
    /// que fica logo acima da linha de traços, na ordem Nome, ID, Versão, Disponível, Origem. A tabela acaba na primeira
    /// linha que não cabe nas colunas (o rodapé "N atualizações disponíveis").
    /// </summary>
    public static List<AppUpdate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => { int cr = l.TrimEnd('\r').LastIndexOf('\r'); return cr >= 0 ? l[(cr + 1)..] : l.TrimEnd('\r'); })
            .ToList();
        var result = new List<AppUpdate>();
        int dash = lines.FindIndex(l => l.Length >= 10 && l.Trim().All(c => c == '-'));
        if (dash < 1) return result;

        string header = lines[dash - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 4) return result;

        for (int r = dash + 1; r < lines.Count; r++)
        {
            string row = lines[r];
            if (row.Trim().Length == 0) break;
            if (row.Length <= starts[3]) break;                       // rodapé ou outra mensagem
            string Col(int c)
            {
                int a = starts[c], b = c + 1 < starts.Count ? starts[c + 1] : row.Length;
                if (a >= row.Length) return "";
                return row[a..Math.Min(b, row.Length)].Trim();
            }
            // Os campos não podem ter espaço depois do ID; se o texto antes da coluna não termina em espaço, a linha não é da tabela.
            if (row[starts[1] - 1] != ' ') break;
            var u = new AppUpdate(Col(0), Col(1), Col(2), Col(3), starts.Count > 4 ? Col(4) : "");
            if (u.Id.Length == 0 || u.Available.Length == 0 || u.Id.Contains(' ')) break;
            if (u.Id.EndsWith('…')) continue;                         // ID cortado: não dá para atualizar por ele
            result.Add(u);
        }
        return result;
    }

    /// <summary>Atualiza um pacote em silêncio. Retorna (ok, mensagem curta).</summary>
    public static async Task<(bool Ok, string Message)> UpgradeAsync(AppUpdate app, CancellationToken ct)
    {
        var (code, output) = await RunAsync($"upgrade --id {app.Id} -e --silent --accept-package-agreements {Common}", ct);
        return Describe(code, output);
    }

    /// <summary>Texto para o código de saída do winget.</summary>
    public static (bool Ok, string Message) Describe(int code, string output) => code switch
    {
        0 => (true, "Atualizado."),
        unchecked((int)0x8A15002B) => (true, "Já estava na versão mais nova."),
        unchecked((int)0x8A150014) => (false, "Pacote não encontrado no winget."),
        unchecked((int)0x8A150008) => (false, "Falha no download: confira a internet e tente de novo."),
        unchecked((int)0x8A150109) or unchecked((int)0x8A15010B) => (true, "Atualizado. Reinicie o PC para terminar."),
        unchecked((int)0x8A150101) => (false, "O programa está aberto: feche e tente de novo."),
        unchecked((int)0x8A150011) => (false, "O hash do instalador não confere. Nada foi instalado."),
        _ => (false, $"Falhou (código 0x{code:X8}). {LastLine(output)}".Trim()),
    };

    private static async Task<(int Code, string Output)> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(WingetPath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await outTask + await errTask);
    }

    /// <summary>Última linha útil da saída (sem barras de progresso).</summary>
    internal static string LastLine(string output) =>
        output.Split('\n', '\r').Select(l => l.Trim())
              .LastOrDefault(l => l.Length > 3 && !l.All(c => "-\\|/█▒ ".Contains(c)) && !l.Contains("KB /") && !l.Contains("MB /")) ?? "";
}
