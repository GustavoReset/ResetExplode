using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using DragEventArgs = System.Windows.DragEventArgs;

namespace FortKnox;

/// <summary>Linha da lista de mesclagem. Páginas/tamanho são preenchidos em segundo plano depois de adicionar.</summary>
public sealed class PdfEntry : INotifyPropertyChanged
{
    private string _pages = "lendo…";
    private int _order;
    public PdfEntry(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try { Size = PdfView.FormatBytes(new FileInfo(path).Length); } catch { Size = "—"; }
    }
    public string Path { get; }
    public string Name { get; }
    public string Size { get; }
    public int Order { get => _order; set { _order = value; Raise(nameof(Order)); } }
    public string Pages { get => _pages; set { _pages = value; Raise(nameof(Pages)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Página "PDF". Só cuida de tela e de arquivos escolhidos: o trabalho pesado está no PdfService (Task.Run + CancellationToken).
/// O Progress&lt;T&gt; é criado aqui, na thread da interface, então os relatórios chegam já nela (sem Dispatcher manual).
/// </summary>
public partial class PdfView : UserControl
{
    private readonly ObservableCollection<PdfEntry> _merge = new();
    private string? _file;                 // arquivo das abas dividir / comprimir / extrair
    private CompressLevel _level = CompressLevel.Medium;
    private string _tab = "merge";
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _revealPath;
    private string _fullText = "";
    private const int PreviewChars = 200_000;

    public PdfView()
    {
        InitializeComponent();
        PMergeGrid.ItemsSource = _merge;
        PLevel_Checked(this, new RoutedEventArgs());
        UpdateMergeHint();
    }

    // ------------------------------------------------------------------ Ciclo de vida (MainWindow.Pdf.cs)

    public void Activate() { /* nada pesado ao abrir: as bibliotecas só carregam quando uma operação roda */ }

    /// <summary>Cancela a operação em andamento (ao fechar o app).</summary>
    public void CancelAll() => _cts?.Cancel();

    // ------------------------------------------------------------------ Abas

    private void PTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelMerge == null) return; // durante InitializeComponent
        _tab = (string)((RadioButton)sender).Tag;
        PanelMerge.Visibility = _tab == "merge" ? Visibility.Visible : Visibility.Collapsed;
        PanelSplit.Visibility = _tab == "split" ? Visibility.Visible : Visibility.Collapsed;
        PanelCompress.Visibility = _tab == "compress" ? Visibility.Visible : Visibility.Collapsed;
        PanelText.Visibility = _tab == "text" ? Visibility.Visible : Visibility.Collapsed;
        PFileCard.Visibility = _tab == "merge" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (PLevelHint == null) return;
        var chip = sender as RadioButton;
        if (chip?.Tag is string t) _level = t switch { "low" => CompressLevel.Low, "high" => CompressLevel.High, _ => CompressLevel.Medium };
        PLevelHint.Text = _level switch
        {
            CompressLevel.Low => "Leve: imagens regravadas com qualidade 85, mesmo tamanho em pixels. Quase sem perda visível.",
            CompressLevel.Medium => "Médio: qualidade 70 e imagens limitadas a 2000 px no lado maior. Bom para enviar por e-mail.",
            _ => "Forte: qualidade 55 e imagens limitadas a 1400 px. Menor arquivo; fotos perdem detalhe.",
        };
    }

    // ------------------------------------------------------------------ Arrastar e soltar

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (_running || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (pdfs.Count == 0) { PStatus.Text = "Solte arquivos .pdf."; return; }
        if (_tab == "merge") AddToMerge(pdfs); else SetFile(pdfs[0]);
    }

    // ------------------------------------------------------------------ Arquivo único (dividir / comprimir / extrair)

    private void PChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf", Title = "Escolher PDF" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) SetFile(dlg.FileName);
    }

    private async void SetFile(string path)
    {
        _file = path;
        PFilePath.Text = path;
        PFileInfo.Text = "Lendo…";
        if (string.IsNullOrWhiteSpace(PSplitDir.Text)) PSplitDir.Text = Path.GetDirectoryName(path) ?? "";
        PText.Clear(); _fullText = ""; PBtnSaveTxt.IsEnabled = PBtnCopyTxt.IsEnabled = false;
        try
        {
            var info = await PdfService.InspectAsync(path);
            if (_file == path) PFileInfo.Text = $"{info.Pages} página(s) · {FormatBytes(info.Bytes)}";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (_file == path) { PFileInfo.Text = ex.Message; }
        }
    }

    private bool RequireFile()
    {
        if (!string.IsNullOrEmpty(_file) && File.Exists(_file)) return true;
        PStatus.Text = "Escolha um PDF primeiro.";
        return false;
    }

    // ------------------------------------------------------------------ Mesclar

    private void PMergeAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf", Title = "Adicionar PDFs", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddToMerge(dlg.FileNames);
    }

    private void AddToMerge(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var entry = new PdfEntry(p);
            _merge.Add(entry);
            _ = FillPagesAsync(entry);
        }
        Renumber();
    }

    private static async Task FillPagesAsync(PdfEntry entry)
    {
        try { entry.Pages = (await PdfService.InspectAsync(entry.Path)).Pages.ToString(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { entry.Pages = "ilegível"; }
    }

    private void Renumber()
    {
        for (int i = 0; i < _merge.Count; i++) _merge[i].Order = i + 1;
        UpdateMergeHint();
    }

    private void UpdateMergeHint() => PMergeEmpty.Visibility = _merge.Count < 2 ? Visibility.Visible : Visibility.Collapsed;

    private void PMergeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PMergeGrid.SelectedItem is PdfEntry en) { _merge.Remove(en); Renumber(); }
    }

    private void PMergeUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void PMergeDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        int i = PMergeGrid.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _merge.Count) return;
        _merge.Move(i, j);
        PMergeGrid.SelectedIndex = j;
        Renumber();
    }

    private void PMergeClear_Click(object sender, RoutedEventArgs e) { _merge.Clear(); Renumber(); }

    private async void PMerge_Click(object sender, RoutedEventArgs e)
    {
        if (_merge.Count < 2) { PStatus.Text = "Adicione pelo menos 2 PDFs."; return; }
        var inputs = _merge.Select(m => m.Path).ToList();
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf", Title = "Salvar PDF mesclado", FileName = "mesclado.pdf",
            InitialDirectory = Path.GetDirectoryName(inputs[0]), OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string output = dlg.FileName;

        await RunAsync(async (progress, ct) =>
        {
            await PdfService.MergeAsync(inputs, output, progress, ct);
            _revealPath = output;
            var info = await PdfService.InspectAsync(output, ct);
            return $"Pronto: {Path.GetFileName(output)} · {info.Pages} página(s) · {FormatBytes(info.Bytes)}";
        });
    }

    // ------------------------------------------------------------------ Dividir

    private void PSplitDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pasta de saída" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) PSplitDir.Text = dlg.FolderName;
    }

    private async void PSplit_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile()) return;
        string input = _file!;
        string dir = string.IsNullOrWhiteSpace(PSplitDir.Text) ? (Path.GetDirectoryName(input) ?? "") : PSplitDir.Text.Trim();
        string? ranges = PRanges.Text;

        await RunAsync(async (progress, ct) =>
        {
            var files = await PdfService.SplitAsync(input, dir, ranges, progress, ct);
            _revealPath = files.Count > 0 ? files[0] : dir;
            return $"Pronto: {files.Count} arquivo(s) criado(s) em {dir}";
        });
    }

    // ------------------------------------------------------------------ Comprimir

    private async void PCompress_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile()) return;
        string input = _file!;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf", Title = "Salvar PDF comprimido", OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(input) + "_comprimido.pdf", InitialDirectory = Path.GetDirectoryName(input),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string output = dlg.FileName;
        var level = _level;

        await RunAsync(async (progress, ct) =>
        {
            var r = await PdfService.CompressAsync(input, output, level, progress, ct);
            if (!r.Wrote)
                return r.ImagesTotal == 0
                    ? "Nada a reduzir: este PDF não tem imagens JPEG, e o tamanho vem de texto e fontes. Nenhum arquivo foi criado."
                    : "Sem ganho: o resultado não ficou menor que o original. Nenhum arquivo foi criado. Tente um nível mais forte.";
            _revealPath = output;
            double pct = 100.0 * (r.Before - r.After) / r.Before;
            return $"Pronto: {FormatBytes(r.Before)} → {FormatBytes(r.After)} (−{pct:0}%) · {r.ImagesRecompressed} de {r.ImagesTotal} imagem(ns) recomprimida(s)";
        });
    }

    // ------------------------------------------------------------------ Extrair texto

    private async void PExtract_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile()) return;
        string input = _file!;
        await RunAsync(async (progress, ct) =>
        {
            var r = await PdfService.ExtractTextAsync(input, progress, ct);
            _fullText = r.Text;
            PText.Text = r.Text.Length > PreviewChars ? r.Text[..PreviewChars] + "\n\n… (prévia truncada; use SALVAR para o texto completo)" : r.Text;
            PBtnSaveTxt.IsEnabled = PBtnCopyTxt.IsEnabled = r.Text.Length > 0;
            string warn = r.PagesWithoutText == 0 ? "" :
                $" · {r.PagesWithoutText} página(s) sem texto (provavelmente imagem escaneada; isso exigiria OCR, que não está incluído)";
            return $"Pronto: {r.Pages} página(s) lidas, {r.Text.Length:N0} caracteres{warn}";
        });
    }

    private async void PSaveTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_fullText.Length == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Texto (*.txt)|*.txt", Title = "Salvar texto extraído", OverwritePrompt = true,
            FileName = (string.IsNullOrEmpty(_file) ? "texto" : Path.GetFileNameWithoutExtension(_file)) + ".txt",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, _fullText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _revealPath = dlg.FileName;
            PBtnReveal.Visibility = Visibility.Visible;
            PStatus.Text = "Texto salvo em " + dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PStatus.Text = "Não foi possível salvar: " + ex.Message; }
    }

    private void PCopyTxt_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_fullText); PStatus.Text = "Texto copiado."; }
        catch (System.Runtime.InteropServices.ExternalException) { PStatus.Text = "A área de transferência está ocupada por outro programa. Tente de novo."; }
    }

    // ------------------------------------------------------------------ Execução comum (progresso, cancelar, erros)

    /// <summary>
    /// Roda uma operação com tela travada, barra de progresso e botão CANCELAR. Nenhum erro escapa: esta página é chamada de
    /// handlers async void, e uma exceção solta ali derrubaria o aplicativo inteiro.
    /// </summary>
    private async Task RunAsync(Func<IProgress<PdfProgress>, CancellationToken, Task<string>> work)
    {
        if (_running) return;
        _running = true;
        _revealPath = null;
        _cts = new CancellationTokenSource();
        SetRunningUi(true);
        var progress = new Progress<PdfProgress>(p =>
        {
            if (!_running) return; // relatório atrasado depois do fim não sobrescreve o resultado
            PStatus.Text = p.Text;
            PBar.ScaleX = Math.Clamp(p.Fraction, 0, 1);
        });
        try
        {
            string message = await work(progress, _cts.Token);
            PStatus.Text = message;
            PBar.ScaleX = 1;
        }
        catch (OperationCanceledException) { PStatus.Text = "Cancelado. Nenhum arquivo foi alterado."; PBar.ScaleX = 0; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        { PStatus.Text = ex.Message; PBar.ScaleX = 0; }
        catch (OutOfMemoryException) { PStatus.Text = "Memória insuficiente para este PDF. Feche outros programas ou use um arquivo menor."; PBar.ScaleX = 0; }
        catch (Exception ex) { PStatus.Text = "Erro inesperado: " + ex.Message; PBar.ScaleX = 0; }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
            SetRunningUi(false);
        }
    }

    private void SetRunningUi(bool running)
    {
        PBtnCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PBtnReveal.Visibility = !running && _revealPath != null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new Button[] { PBtnMerge, PBtnSplit, PBtnCompress, PBtnExtract }) b.IsEnabled = !running;
        if (running) PBar.ScaleX = 0;
        PdfRoot.Cursor = running ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void PCancel_Click(object sender, RoutedEventArgs e)
    {
        PStatus.Text = "Cancelando…";
        _cts?.Cancel();
    }

    private void PReveal_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_revealPath)) return;
        try
        {
            if (File.Exists(_revealPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_revealPath}\"") { UseShellExecute = true });
            else if (Directory.Exists(_revealPath)) Process.Start(new ProcessStartInfo(_revealPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { PStatus.Text = "Não foi possível abrir a pasta: " + ex.Message; }
    }

    internal static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };
}
