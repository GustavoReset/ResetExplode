using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace FortKnox.Tests;

public sealed class PdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fkpdf-" + Guid.NewGuid().ToString("N"));
    public PdfTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    // ---------------------------------------------------------------- Geração de PDFs de teste (escritos à mão, sem depender das libs testadas)

    /// <summary>PDF válido com N páginas de texto "Texto da pagina {prefixo}{n}" (fonte Helvetica padrão) e, opcionalmente, uma imagem JPEG na página 1.</summary>
    private string MakePdf(string name, int pages, string prefix = "", byte[]? jpeg = null, int jpegW = 0, int jpegH = 0, bool gray = false, bool textOnPages = true)
    {
        var objs = new List<byte[]>(); // objeto i+1
        void Add(string s) => objs.Add(Encoding.Latin1.GetBytes(s));
        // 1 = catálogo, 2 = páginas, 3 = fonte, 4 = imagem (ou vazio), 5.. = (página, conteúdo)
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{5 + i * 2} 0 R"));
        Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        if (jpeg != null)
        {
            var head = Encoding.Latin1.GetBytes($"<< /Type /XObject /Subtype /Image /Width {jpegW} /Height {jpegH} /ColorSpace /{(gray ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            objs.Add(head.Concat(jpeg).Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray());
        }
        else Add("null");
        for (int i = 0; i < pages; i++)
        {
            string xo = jpeg != null && i == 0 ? " /XObject << /Im1 4 0 R >>" : "";
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >>{xo} >> /Contents {6 + i * 2} 0 R >>");
            string text = textOnPages ? $"BT /F1 24 Tf 50 700 Td (Texto da pagina {prefix}{i + 1}) Tj ET" : "";
            string img = jpeg != null && i == 0 ? " q 400 0 0 300 50 300 cm /Im1 Do Q" : "";
            string content = text + img;
            Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        using var ms = new MemoryStream();
        void W(byte[] b) => ms.Write(b);
        W(Encoding.Latin1.GetBytes("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n")); W(objs[i]); W(Encoding.Latin1.GetBytes("\nendobj\n"));
        }
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        W(Encoding.Latin1.GetBytes(sb.ToString()));
        string path = P(name);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>JPEG grande e "ruidoso" (difícil de comprimir) para dar margem à recompressão.</summary>
    private static byte[] NoisyJpeg(int w, int h, bool gray)
    {
        var rnd = new Random(42);
        int bpp = gray ? 1 : 3;
        var pix = new byte[w * h * bpp];
        rnd.NextBytes(pix);
        for (int i = 0; i < pix.Length; i++) pix[i] = (byte)((pix[i] & 0xC0) | ((i / (w * bpp / 8)) & 0x3F)); // mistura ruído + gradiente
        var bmp = BitmapSource.Create(w, h, 96, 96, gray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, pix, w * bpp);
        var enc = new JpegBitmapEncoder { QualityLevel = 100 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream(); enc.Save(ms);
        return ms.ToArray();
    }

    private static int Pages(string path) { using var d = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly); return d.PageCount; }
    private static void AssertNotLocked(params string[] files) { foreach (var f in files) File.Delete(f); Assert.All(files, f => Assert.False(File.Exists(f))); }

    // ---------------------------------------------------------------- ParseRanges

    [Theory]
    [InlineData("1-3,5", 10, "1-3|5-5")]
    [InlineData(" 2 ; 4-5 ", 10, "2-2|4-5")]
    [InlineData("8-", 10, "8-10")]
    [InlineData("1,1", 3, "1-1|1-1")]
    public void ParseRanges_Valid(string text, int pages, string expected)
    {
        var r = PdfService.ParseRanges(text, pages).Select(x => $"{x.From}-{x.To}");
        Assert.Equal(expected, string.Join("|", r));
    }

    [Theory]
    [InlineData("")] [InlineData(",,")] [InlineData("a")] [InlineData("1-b")] [InlineData("0")] [InlineData("11")]
    [InlineData("5-3")] [InlineData("1-2-3")] [InlineData("-3")] [InlineData("1-11")]
    public void ParseRanges_Invalid(string text) => Assert.Throws<ArgumentException>(() => PdfService.ParseRanges(text, 10));

    // ---------------------------------------------------------------- Inspect / erros

    [Fact]
    public async Task Inspect_ReportsPagesAndSize()
    {
        var f = MakePdf("a.pdf", 3);
        var info = await PdfService.InspectAsync(f);
        Assert.Equal(3, info.Pages);
        Assert.Equal(new FileInfo(f).Length, info.Bytes);
        AssertNotLocked(f);
    }

    [Fact]
    public async Task NotAPdf_GivesFriendlyError()
    {
        var f = P("x.pdf"); File.WriteAllText(f, "isto não é um pdf");
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfService.InspectAsync(f));
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfService.ExtractTextAsync(f));
        AssertNotLocked(f);
    }

    [Fact]
    public async Task TruncatedPdf_NeverCrashes_FailsCleanlyOrOpens()
    {
        var whole = File.ReadAllBytes(MakePdf("ok.pdf", 3));
        foreach (int cut in new[] { 10, 60, whole.Length / 2, whole.Length - 30 })
        {
            var f = P($"cut{cut}.pdf");
            File.WriteAllBytes(f, whole[..cut]);
            try { await PdfService.InspectAsync(f); }
            catch (InvalidDataException) { /* erro amigável esperado */ }
            AssertNotLocked(f);
        }
    }

    [Fact]
    public async Task MissingFile_Throws()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => PdfService.InspectAsync(P("nao-existe.pdf")));

    [Fact]
    public async Task PasswordProtected_IsRefusedClearly()
    {
        var f = P("senha.pdf");
        using (var d = new PdfDocument())
        {
            d.AddPage();
            d.SecuritySettings.UserPassword = "segredo";
            d.SecuritySettings.OwnerPassword = "dono";
            d.Save(f);
        }
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PdfService.InspectAsync(f));
        Assert.Contains("senha", ex.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfService.ExtractTextAsync(f));
        AssertNotLocked(f);
    }

    // ---------------------------------------------------------------- Mesclar

    [Fact]
    public async Task Merge_CombinesInOrder_AndReleasesFiles()
    {
        var a = MakePdf("a.pdf", 3, "A"); var b = MakePdf("b.pdf", 2, "B");
        var outp = P("m.pdf");
        var reports = new List<PdfProgress>();
        await PdfService.MergeAsync(new[] { a, b }, outp, new Progress<PdfProgress>(reports.Add));
        Assert.Equal(5, Pages(outp));
        var text = (await PdfService.ExtractTextAsync(outp)).Text;
        Assert.True(text.IndexOf("pagina A3") < text.IndexOf("pagina B1"));
        Assert.Contains("pagina B2", text);
        AssertNotLocked(a, b, outp);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Merge_ThreeFiles_SameFileTwice()
    {
        var a = MakePdf("a.pdf", 2);
        var outp = P("m.pdf");
        await PdfService.MergeAsync(new[] { a, a, a }, outp);
        Assert.Equal(6, Pages(outp));
    }

    [Fact]
    public async Task Merge_RejectsOutputEqualToInput_AndTooFew()
    {
        var a = MakePdf("a.pdf", 1); var b = MakePdf("b.pdf", 1);
        var before = File.ReadAllBytes(a);
        await Assert.ThrowsAsync<ArgumentException>(() => PdfService.MergeAsync(new[] { a, b }, a));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfService.MergeAsync(new[] { a, b }, a.ToUpperInvariant()));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfService.MergeAsync(new[] { a }, P("o.pdf")));
        Assert.Equal(before, File.ReadAllBytes(a));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Merge_BadInput_FailsWithoutCreatingOutput()
    {
        var a = MakePdf("a.pdf", 1); var bad = P("bad.pdf"); File.WriteAllText(bad, "lixo");
        var outp = P("m.pdf");
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfService.MergeAsync(new[] { a, bad }, outp));
        Assert.False(File.Exists(outp));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        AssertNotLocked(a, bad);
    }

    [Fact]
    public async Task Merge_Cancelled_LeavesNothingBehind()
    {
        var a = MakePdf("a.pdf", 3); var b = MakePdf("b.pdf", 3);
        var outp = P("m.pdf");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfService.MergeAsync(new[] { a, b }, outp, null, cts.Token));
        Assert.False(File.Exists(outp));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        AssertNotLocked(a, b);
    }

    [Fact]
    public async Task Merge_ExistingOutput_IsReplacedAtomically()
    {
        var a = MakePdf("a.pdf", 1); var b = MakePdf("b.pdf", 1);
        var outp = P("m.pdf"); File.WriteAllText(outp, "antigo");
        await PdfService.MergeAsync(new[] { a, b }, outp);
        Assert.Equal(2, Pages(outp));
    }

    // ---------------------------------------------------------------- Dividir

    [Fact]
    public async Task Split_ByRanges()
    {
        var src = MakePdf("doc.pdf", 6, "P");
        var outDir = P("saida");
        var files = await PdfService.SplitAsync(src, outDir, "1-2,5");
        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { 2, 1 }, files.Select(Pages));
        Assert.Contains("pagina P5", (await PdfService.ExtractTextAsync(files[1])).Text);
        Assert.Equal("doc_p1-2.pdf", Path.GetFileName(files[0]));
        Assert.Equal("doc_p5.pdf", Path.GetFileName(files[1]));
        AssertNotLocked(src);
        AssertNotLocked(files.ToArray());
    }

    [Fact]
    public async Task Split_EveryPage_AndNeverOverwrites()
    {
        var src = MakePdf("doc.pdf", 3);
        var outDir = P("saida");
        var first = await PdfService.SplitAsync(src, outDir, null);
        Assert.Equal(3, first.Count);
        var second = await PdfService.SplitAsync(src, outDir, "  ");
        Assert.Empty(first.Intersect(second));                 // nomes novos "(2)", nada sobrescrito
        Assert.Equal(6, Directory.GetFiles(outDir, "*.pdf").Length);
    }

    [Fact]
    public async Task Split_OpenEndedRange_AndInvalid()
    {
        var src = MakePdf("doc.pdf", 4);
        var files = await PdfService.SplitAsync(src, P("o"), "3-");
        Assert.Equal(2, Pages(files[0]));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfService.SplitAsync(src, P("o2"), "1-9"));
        Assert.False(Directory.Exists(P("o2")) && Directory.GetFiles(P("o2")).Length > 0);
    }

    [Fact]
    public async Task Split_Cancelled_RemovesPartialParts()
    {
        var src = MakePdf("doc.pdf", 30);
        var outDir = P("saida");
        using var cts = new CancellationTokenSource();
        var progress = new Progress<PdfProgress>(_ => { });
        var task = PdfService.SplitAsync(src, outDir, null, new SyncProgress(r => { if (r.Text.Contains("parte 5 ")) cts.Cancel(); }), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.Exists(outDir) ? Directory.GetFiles(outDir) : Array.Empty<string>());
        AssertNotLocked(src);
    }

    private sealed class SyncProgress(Action<PdfProgress> a) : IProgress<PdfProgress> { public void Report(PdfProgress v) => a(v); }

    // ---------------------------------------------------------------- Extrair texto

    [Fact]
    public async Task ExtractText_ReturnsTextPerPage()
    {
        var f = MakePdf("t.pdf", 3, "Z");
        var r = await PdfService.ExtractTextAsync(f);
        Assert.Equal(3, r.Pages);
        Assert.Equal(0, r.PagesWithoutText);
        Assert.Contains("Texto da pagina Z1", r.Text);
        Assert.Contains("Texto da pagina Z3", r.Text);
        AssertNotLocked(f);
    }

    [Fact]
    public async Task ExtractText_CountsPagesWithoutText()
    {
        var f = MakePdf("vazio.pdf", 2, textOnPages: false);
        var r = await PdfService.ExtractTextAsync(f);
        Assert.Equal(2, r.PagesWithoutText);
    }

    // ---------------------------------------------------------------- Comprimir

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compress_RecompressesJpeg_AndKeepsDocumentValid(bool gray)
    {
        var jpeg = NoisyJpeg(2400, 1600, gray);
        var f = MakePdf("img.pdf", 2, "I", jpeg, 2400, 1600, gray);
        var outp = P("img_c.pdf");
        var r = await PdfService.CompressAsync(f, outp, CompressLevel.Medium);
        Assert.True(r.Wrote);
        Assert.Equal(1, r.ImagesTotal); Assert.Equal(1, r.ImagesRecompressed);
        Assert.True(r.After < r.Before * 0.8, $"esperava ganho real: {r.Before} -> {r.After}");
        Assert.Equal(new FileInfo(outp).Length, r.After);
        Assert.Equal(2, Pages(outp));
        Assert.Contains("Texto da pagina I2", (await PdfService.ExtractTextAsync(outp)).Text); // texto intacto

        // a imagem continua um JPEG decodificável, reduzida e com o mesmo nº de componentes
        using var d = PdfReader.Open(outp, PdfDocumentOpenMode.Import);
        var img = d.Internals.GetAllObjects().OfType<PdfDictionary>().Single(o => o.Elements.GetName("/Subtype") == "/Image");
        Assert.Equal("/DCTDecode", img.Elements.GetName("/Filter"));
        Assert.Equal(2000, img.Elements.GetInteger("/Width"));
        Assert.InRange(img.Elements.GetInteger("/Height"), 1332, 1334);
        var bytes = img.Stream.Value;
        Assert.Equal(gray ? 1 : 3, PdfService.JpegComponents(bytes));
        using var ms = new MemoryStream(bytes);
        var frame = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(2000, frame.PixelWidth);
        AssertNotLocked(f);
    }

    [Fact]
    public async Task Compress_HigherLevel_IsSmaller()
    {
        var jpeg = NoisyJpeg(2400, 1600, false);
        var f = MakePdf("img.pdf", 1, "", jpeg, 2400, 1600);
        var low = await PdfService.CompressAsync(f, P("low.pdf"), CompressLevel.Low);
        var high = await PdfService.CompressAsync(f, P("high.pdf"), CompressLevel.High);
        Assert.True(low.Wrote && high.Wrote);
        Assert.True(high.After < low.After);
    }

    [Fact]
    public async Task Compress_NoGain_WritesNothing()
    {
        var f = MakePdf("texto.pdf", 3);
        var outp = P("c.pdf");
        var r = await PdfService.CompressAsync(f, outp, CompressLevel.High);
        if (!r.Wrote) Assert.False(File.Exists(outp)); else Assert.True(r.After < r.Before);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Compress_RejectsOutputEqualToInput_AndPreservesOriginal()
    {
        var f = MakePdf("a.pdf", 1);
        var before = File.ReadAllBytes(f);
        await Assert.ThrowsAsync<ArgumentException>(() => PdfService.CompressAsync(f, f, CompressLevel.Medium));
        Assert.Equal(before, File.ReadAllBytes(f));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Compress_Cancelled_LeavesNothing()
    {
        var jpeg = NoisyJpeg(1200, 800, false);
        var f = MakePdf("img.pdf", 1, "", jpeg, 1200, 800);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfService.CompressAsync(f, P("o.pdf"), CompressLevel.High, null, cts.Token));
        Assert.False(File.Exists(P("o.pdf")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        AssertNotLocked(f);
    }

    [Fact]
    public void JpegComponents_DetectsGrayRgbAndGarbage()
    {
        Assert.Equal(3, PdfService.JpegComponents(NoisyJpeg(64, 64, false)));
        Assert.Equal(1, PdfService.JpegComponents(NoisyJpeg(64, 64, true)));
        Assert.Equal(0, PdfService.JpegComponents(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));
        Assert.Equal(0, PdfService.JpegComponents(Array.Empty<byte>()));
    }
}
