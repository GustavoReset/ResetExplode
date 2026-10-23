using System.Windows;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Limpeza: temporários, cache do Windows Update, lixeira, logs etc.</summary>
public partial class MainWindow
{
    private List<CleanupItem>? _cleanItems;
    private bool _cleanBusy;

    private void InitClean()
    {
        if (_cleanItems != null) return;
        _cleanItems = CleanupManager.Catalog();
        foreach (var i in _cleanItems) i.Selected = i.PreSelect;
        CleanList.ItemsSource = _cleanItems;
        _ = AnalyzeCleanAsync();
    }

    private async Task AnalyzeCleanAsync()
    {
        if (_cleanItems == null || _cleanBusy) return;
        _cleanBusy = true;
        SetCleanButtons(false);
        CleanStatus.Text = "Analisando…";
        foreach (var i in _cleanItems) { i.Size = -1; i.Status = ""; }
        foreach (var i in _cleanItems)
        {
            i.Status = "Medindo…";
            try { i.Size = await Task.Run(() => CleanupManager.Measure(i)); i.Status = ""; }
            catch (Exception ex) { i.Status = $"Não foi possível medir: {ex.Message}"; }
        }
        _cleanBusy = false;
        SetCleanButtons(true);
        UpdateCleanTotal();
    }

    private void UpdateCleanTotal()
    {
        if (_cleanItems == null) return;
        long all = _cleanItems.Where(i => i.Size > 0).Sum(i => i.Size);
        long sel = _cleanItems.Where(i => i.Selected && i.Size > 0).Sum(i => i.Size);
        CleanTotal.Text = CleanupManager.FormatBytes(sel);
        CleanStatus.Text = $"{CleanupManager.FormatBytes(all)} encontrados no total · {CleanupManager.FormatBytes(sel)} nos itens marcados." +
                           (SensorMonitor.IsElevated ? "" : " Sem administrador, as pastas do Windows e de outros usuários ficam de fora.");
    }

    private void SetCleanButtons(bool on)
    {
        BtnCleanRun.IsEnabled = on;
        BtnCleanAnalyze.IsEnabled = on;
    }

    private void CleanItem_Click(object sender, RoutedEventArgs e) => UpdateCleanTotal();

    private async void BtnCleanAnalyze_Click(object sender, RoutedEventArgs e) => await AnalyzeCleanAsync();

    private async void BtnCleanRun_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanItems == null || _cleanBusy) return;
        var sel = _cleanItems.Where(i => i.Selected).ToList();
        if (sel.Count == 0) { CleanStatus.Text = "Nenhum item marcado."; return; }
        if (sel.Any(i => i.NeedsAdmin) && !RequireAdmin()) return;

        string names = string.Join("\n", sel.Select(i => $"  • {i.Name} ({i.SizeText})"));
        var r = MessageBox.Show($"Apagar estes itens?\n\n{names}\n\nArquivos em uso são pulados. Não dá para desfazer.",
                                "Limpeza", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        _cleanBusy = true;
        SetCleanButtons(false);
        CleanProgress.ScaleX = 0;
        long freed = 0;
        int done = 0;
        foreach (var i in sel)
        {
            CleanStatus.Text = $"Limpando {i.Name} ({done + 1} de {sel.Count})…";
            i.Status = "Limpando…";
            try
            {
                long n = await Task.Run(() => CleanupManager.Clean(i));
                freed += n;
                i.Status = $"{CleanupManager.FormatBytes(n)} liberados.";
                i.Size = await Task.Run(() => CleanupManager.Measure(i));
                if (i.Size > 0) i.Status += $" Sobrou {i.SizeText} (em uso ou recente).";
            }
            catch (Exception ex) { i.Status = $"Falhou: {ex.Message}"; }
            done++;
            CleanProgress.ScaleX = (double)done / sel.Count;
        }
        _cleanBusy = false;
        SetCleanButtons(true);
        UpdateCleanTotal();
        CleanStatus.Text = $"Pronto: {CleanupManager.FormatBytes(freed)} liberados. " + CleanStatus.Text;
    }

    private void BtnCleanMgr_Click(object sender, RoutedEventArgs e)
    {
        // Limpeza de Disco do Windows em modo "arquivos do sistema" (inclui Windows.old e atualizações antigas).
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cleanmgr.exe", "/d C") { UseShellExecute = true }); }
        catch (Exception ex) { CleanStatus.Text = $"Não foi possível abrir a Limpeza de Disco: {ex.Message}"; }
    }

    private void BtnCleanComponents_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var r = MessageBox.Show("Limpar a pasta de componentes do Windows (WinSxS) com o DISM?\n\n" +
                                "Remove versões antigas de atualizações já substituídas. Leva de 5 a 30 minutos e abre uma janela de console. " +
                                "Depois disso, as atualizações instaladas não podem mais ser desinstaladas.",
                                "Limpeza", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        TechTools.RunConsole("Limpeza de componentes (WinSxS)", "Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
    }
}
