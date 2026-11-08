using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace FortKnox;

// =====================================================================================================
//  RESET FERRAMENTAS · PDF — camada de serviço (sem controles de tela).
//
//  Regras que valem para todas as operações:
//   • Tudo é assíncrono (Task.Run), cancelável e reporta progresso; nada roda na thread da interface.
//   • Todo PdfDocument / FileStream é aberto em `using`: o arquivo de origem fica livre assim que a operação termina.
//   • A saída é gravada em arquivo temporário e só então movida para o nome final (nunca fica um PDF pela metade).
//   • A saída NUNCA pode ser um dos arquivos de entrada (o original não é sobrescrito).
//   • PDF protegido por senha é recusado com mensagem clara (não há suporte a senha nesta versão).
// =====================================================================================================

public sealed record PdfProgress(string Text, double Fraction);
public sealed record PdfInfo(int Pages, long Bytes);
public enum CompressLevel { Low, Medium, High }
/// <summary>Resultado da compressão. Wrote = false quando não houve ganho (nenhum arquivo foi criado).</summary>
public sealed record CompressResult(long Before, long After, int ImagesRecompressed, int ImagesTotal, bool Wrote);
public sealed record TextResult(string Text, int Pages, int PagesWithoutText);

public static class PdfService
{
    // ------------------------------------------------------------------ Informação

    public static Task<PdfInfo> InspectAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var doc = OpenPdf(path, PdfDocumentOpenMode.Import); // (InformationOnly está obsoleto no PDFsharp 6: é apelido de Import)
            return new PdfInfo(doc.PageCount, new FileInfo(path).Length);
        }, ct);

    // ------------------------------------------------------------------ Mesclar

    public static Task MergeAsync(IReadOnlyList<string> inputs, string output, IProgress<PdfProgress>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            // Validação dentro da tarefa: todo erro chega pelo mesmo caminho (await), nunca como exceção síncrona.
            if (inputs.Count < 2) throw new ArgumentException("Escolha pelo menos 2 PDFs para mesclar.");
            RejectOutputIsInput(output, inputs);
            using var result = new PdfDocument();
            for (int i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new($"Lendo {Path.GetFileName(inputs[i])} ({i + 1} de {inputs.Count})…", (double)i / inputs.Count * 0.9));
                using var src = OpenPdf(inputs[i], PdfDocumentOpenMode.Import);
                for (int p = 0; p < src.PageCount; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    result.AddPage(src.Pages[p]); // as páginas são copiadas para o documento novo: dá para fechar a origem em seguida
                }
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Gravando o PDF final…", 0.95));
            SaveAtomic(output, fs => result.Save(fs, closeStream: false));
            progress?.Report(new("Concluído", 1));
        }, ct);
    }

    // ------------------------------------------------------------------ Dividir

    /// <summary>
    /// Divide em arquivos dentro de <paramref name="outputDir"/>. Com <paramref name="ranges"/> ("1-3,5,8-") gera um arquivo por
    /// intervalo; vazio = um arquivo por página. Devolve os caminhos criados (nomes nunca colidem com arquivos existentes).
    /// </summary>
    public static Task<IReadOnlyList<string>> SplitAsync(string input, string outputDir, string? ranges,
        IProgress<PdfProgress>? progress = null, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            using var src = OpenPdf(input, PdfDocumentOpenMode.Import);
            var groups = string.IsNullOrWhiteSpace(ranges)
                ? Enumerable.Range(1, src.PageCount).Select(n => (n, n)).ToList()
                : ParseRanges(ranges, src.PageCount);
            if (groups.Count > 1000) throw new ArgumentException("Isso geraria mais de 1000 arquivos. Use intervalos maiores.");

            Directory.CreateDirectory(outputDir);
            string baseName = Path.GetFileNameWithoutExtension(input);
            var created = new List<string>();
            try
            {
                for (int g = 0; g < groups.Count; g++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (a, b) = groups[g];
                    progress?.Report(new($"Criando parte {g + 1} de {groups.Count} (páginas {a}–{b})…", (double)g / groups.Count));
                    using var part = new PdfDocument();
                    for (int p = a; p <= b; p++) part.AddPage(src.Pages[p - 1]);
                    string name = a == b ? $"{baseName}_p{a}.pdf" : $"{baseName}_p{a}-{b}.pdf";
                    string target = UniquePath(Path.Combine(outputDir, name));
                    SaveAtomic(target, fs => part.Save(fs, closeStream: false));
                    created.Add(target);
                }
            }
            catch (OperationCanceledException)
            {
                foreach (var f in created) try { File.Delete(f); } catch { } // cancelou: não deixa metade das partes
                throw;
            }
            progress?.Report(new("Concluído", 1));
            return created;
        }, ct);

    /// <summary>"1-3,5,8-" → [(1,3),(5,5),(8,fim)]. Páginas começam em 1. Lança ArgumentException com mensagem em português.</summary>
    public static List<(int From, int To)> ParseRanges(string text, int pageCount)
    {
        var list = new List<(int, int)>();
        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length > 2) throw new ArgumentException($"Intervalo inválido: \"{raw}\".");
            if (!int.TryParse(parts[0], out int from)) throw new ArgumentException($"Intervalo inválido: \"{raw}\".");
            int to = from;
            if (parts.Length == 2)
            {
                if (parts[1].Length == 0) to = pageCount;                       // "8-" = até o fim
                else if (!int.TryParse(parts[1], out to)) throw new ArgumentException($"Intervalo inválido: \"{raw}\".");
            }
            if (from < 1 || to > pageCount) throw new ArgumentException($"\"{raw}\" fora do documento (ele tem {pageCount} páginas).");
            if (from > to) throw new ArgumentException($"Intervalo invertido: \"{raw}\".");
            list.Add((from, to));
        }
        if (list.Count == 0) throw new ArgumentException("Informe ao menos um intervalo, ex.: 1-3,5.");
        return list;
    }

    // ------------------------------------------------------------------ Comprimir

    /// <summary>
    /// Regrava o PDF com compressão máxima e recomprime as imagens JPEG (a parte que realmente reduz o tamanho).
    /// Só troca uma imagem se a nova for menor. Não mexe em texto, fontes nem vetores. Se não houver ganho, não cria arquivo.
    /// </summary>
    public static Task<CompressResult> CompressAsync(string input, string output, CompressLevel level,
        IProgress<PdfProgress>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            RejectOutputIsInput(output, new[] { input });
            long before = new FileInfo(input).Length;
            using var doc = OpenPdf(input, PdfDocumentOpenMode.Modify);
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;

            var (quality, maxSide) = level switch
            {
                CompressLevel.Low => (85, int.MaxValue),
                CompressLevel.Medium => (70, 2000),
                _ => (55, 1400),
            };

            var images = doc.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .Where(d => d.Elements.GetName("/Subtype") == "/Image" && d.Elements.GetName("/Filter") == "/DCTDecode" && d.Stream != null)
                .ToList();

            int done = 0;
            for (int i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new($"Recomprimindo imagem {i + 1} de {images.Count}…", images.Count == 0 ? 0 : 0.05 + 0.8 * i / images.Count));
                if (TryRecompressJpeg(images[i], quality, maxSide)) done++;
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Gravando…", 0.9));
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            long after = ms.Length;
            if (after >= before) return new CompressResult(before, before, done, images.Count, Wrote: false);

            ct.ThrowIfCancellationRequested();
            SaveAtomic(output, fs => { ms.Position = 0; ms.CopyTo(fs); });
            progress?.Report(new("Concluído", 1));
            return new CompressResult(before, after, done, images.Count, Wrote: true);
        }, ct);
    }

    private static bool TryRecompressJpeg(PdfDictionary img, int quality, int maxSide)
    {
        try
        {
            byte[] original = img.Stream.Value;
            int comps = JpegComponents(original);
            if (comps is not (1 or 3)) return false;                  // CMYK etc.: deixa como está
            if (img.Elements.ContainsKey("/Mask")) return false;       // máscara por cor/stencil depende dos pixels originais
            if (!ColorSpaceMatches(img, comps)) return false;

            BitmapSource frame;
            using (var input = new MemoryStream(original, writable: false))
                frame = new JpegBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

            BitmapSource src = new FormatConvertedBitmap(frame, comps == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
            int longSide = Math.Max(src.PixelWidth, src.PixelHeight);
            if (longSide > maxSide)
            {
                double k = (double)maxSide / longSide;
                src = new TransformedBitmap(src, new ScaleTransform(k, k));
            }

            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            byte[] result = outMs.ToArray();

            if (result.Length >= original.Length) return false;        // só troca se ficar menor
            if (JpegComponents(result) != comps) return false;         // o codificador mudou o espaço de cor: não arrisca

            img.Stream.Value = result;
            img.Elements.SetInteger("/Width", src.PixelWidth);
            img.Elements.SetInteger("/Height", src.PixelHeight);
            img.Elements.Remove("/DecodeParms");
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
        {
            return false; // imagem fora do comum: mantém a original
        }
    }

    private static bool ColorSpaceMatches(PdfDictionary img, int comps)
    {
        var cs = img.Elements["/ColorSpace"];
        if (cs is PdfSharp.Pdf.Advanced.PdfReference r) cs = r.Value;
        if (cs is PdfName n) return n.Value == (comps == 1 ? "/DeviceGray" : "/DeviceRGB");
        if (cs is PdfArray a && a.Elements.Count > 0 && a.Elements[0] is PdfName first) return first.Value == "/ICCBased";
        return false;
    }

    /// <summary>Número de componentes de cor de um JPEG (1 = cinza, 3 = RGB, 4 = CMYK) lendo o marcador SOF; 0 se não achar.</summary>
    internal static int JpegComponents(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return 0;
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is 0x01 or (>= 0xD0 and <= 0xD8)) { i += 2; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            bool sof = m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (sof) return d[i + 4] == 8 ? d[i + 9] : 0;              // precisão de 8 bits; byte 9 = nº de componentes
            i += 2 + len;
        }
        return 0;
    }

    // ------------------------------------------------------------------ Extrair texto

    public static Task<TextResult> ExtractTextAsync(string input, IProgress<PdfProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                using var doc = PdfPigDocument.Open(input);
                var sb = new StringBuilder();
                int total = doc.NumberOfPages, empty = 0;
                for (int n = 1; n <= total; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new($"Lendo página {n} de {total}…", (double)(n - 1) / total));
                    string text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(doc.GetPage(n)).Trim();
                    if (text.Length == 0) empty++;
                    sb.Append("--- Página ").Append(n).AppendLine(" ---").AppendLine(text).AppendLine();
                }
                progress?.Report(new("Concluído", 1));
                return new TextResult(sb.ToString(), total, empty);
            }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
            {
                throw new InvalidDataException("Este PDF é protegido por senha. Remova a senha antes de extrair o texto.");
            }
            catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException))
            {
                // Mesma regra do OpenPdf: falha de parse (PdfPig usa vários tipos) vira mensagem amigável.
                throw new InvalidDataException($"Não foi possível ler \"{Path.GetFileName(input)}\" como PDF: {ex.Message}");
            }
        }, ct);

    // ------------------------------------------------------------------ Utilitários

    private static PdfDocument OpenPdf(string path, PdfDocumentOpenMode mode)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo não encontrado: " + path);
        try { return PdfReader.Open(path, mode); }
        // O PDFsharp sinaliza arquivo inválido/protegido/truncado com tipos variados (até System.Exception puro), conforme onde o
        // parser falha. Então: erro de E/S (travado, sem permissão) e cancelamento passam como são; o resto é "PDF ilegível".
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw new InvalidDataException(
                $"Não foi possível abrir \"{Path.GetFileName(path)}\": o PDF está protegido por senha, corrompido ou não é um PDF. ({ex.Message})");
        }
    }

    private static void RejectOutputIsInput(string output, IEnumerable<string> inputs)
    {
        string full = Path.GetFullPath(output);
        if (inputs.Any(i => string.Equals(Path.GetFullPath(i), full, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("O arquivo de saída não pode ser um dos arquivos de entrada. Escolha outro nome.");
    }

    /// <summary>Grava em temporário e troca pelo nome final; se algo falhar, o temporário é apagado e o destino fica intacto.</summary>
    private static void SaveAtomic(string output, Action<Stream> write)
    {
        string full = Path.GetFullPath(output);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
