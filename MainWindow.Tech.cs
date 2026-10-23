using System.Windows;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Técnico: reparo do Windows, rede, impressão, licença e atalhos.</summary>
public partial class MainWindow
{
    private bool _techBusy;

    private void InitTech()
    {
        if (TechShortcuts.ItemsSource == null) TechShortcuts.ItemsSource = TechTools.Shortcuts;
    }

    private void TechLog(string title, string text)
    {
        string block = $"[{DateTime.Now:HH:mm:ss}] {title}\n{text}".TrimEnd();
        TechOutput.Text = string.IsNullOrEmpty(TechOutput.Text) ? block : block + "\n\n" + TechOutput.Text;
    }

    private bool Confirm(string text)
        => MessageBox.Show(text, "Técnico", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Roda uma ação rápida sem janela e mostra a saída no registro.</summary>
    private async Task TechRunAsync(string title, Func<Task<(int Code, string Output)>> action)
    {
        if (_techBusy) return;
        _techBusy = true;
        TechStatus.Text = $"{title}…";
        try
        {
            var (code, output) = await action();
            TechLog(title + (code == 0 ? "" : $" (código {code})"), output);
            TechStatus.Text = $"{title}: concluído.";
        }
        catch (Exception ex) { TechLog(title, $"Falhou: {ex.Message}"); TechStatus.Text = $"{title}: falhou."; }
        _techBusy = false;
    }

    // ---------------- reparo do Windows ----------------

    private void BtnTechSfc_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        TechTools.RunConsole("SFC - verificar arquivos do sistema", "sfc /scannow");
        TechLog("SFC /scannow", "Aberto num console. Leva de 10 a 30 minutos. Se achar erros que não conseguiu corrigir, rode o DISM e depois o SFC de novo.");
    }

    private void BtnTechDism_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        TechTools.RunConsole("DISM - reparar imagem do Windows", "Dism.exe /Online /Cleanup-Image /RestoreHealth");
        TechLog("DISM /RestoreHealth", "Aberto num console. Precisa de internet (baixa os arquivos bons do Windows Update). Pode parar em 20% ou 62% por vários minutos: é normal.");
    }

    private void BtnTechRepairAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        TechTools.RunConsole("Reparo completo (DISM + SFC)",
            "Dism.exe /Online /Cleanup-Image /RestoreHealth & echo. & sfc /scannow");
        TechLog("Reparo completo", "DISM /RestoreHealth seguido de SFC /scannow, num console. Leva de 20 a 60 minutos.");
    }

    private void BtnTechChkdsk_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        if (!Confirm($"Agendar a verificação completa do disco {drive} (CHKDSK /f /r) na próxima reinicialização?\n\n" +
                     "No console, responda S (ou Y) quando ele perguntar. A verificação roda antes do Windows abrir e pode levar " +
                     "de 30 minutos a algumas horas em HD. Não desligue o PC durante a verificação.")) return;
        TechTools.RunConsole($"CHKDSK {drive}", $"chkdsk {drive} /f /r");
        TechLog("CHKDSK", $"Console aberto para agendar a verificação de {drive}. Reinicie o PC para ela rodar.");
    }

    private async void BtnTechRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || _techBusy) return;
        _techBusy = true;
        TechStatus.Text = "Criando ponto de restauração…";
        string r = await TechTools.CreateRestorePointAsync();
        TechLog("Ponto de restauração", r);
        TechStatus.Text = "";
        _techBusy = false;
    }

    // ---------------- rede ----------------

    private async void BtnTechFlushDns_Click(object sender, RoutedEventArgs e)
        => await TechRunAsync("Limpar cache de DNS", TechTools.FlushDnsAsync);

    private async void BtnTechRenewIp_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Renovar o IP? A internet cai por alguns segundos.")) return;
        await TechRunAsync("Renovar IP", TechTools.RenewIpAsync);
    }

    private async void BtnTechResetNet_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        if (!Confirm("Resetar a rede (Winsock, TCP/IP e DNS)?\n\nResolve \"conectado sem internet\" e erros depois de remover antivírus/VPN. " +
                     "IPs fixos configurados à mão voltam para automático. Precisa reiniciar o PC.")) return;
        await TechRunAsync("Resetar rede", TechTools.ResetNetworkAsync);
        TechLog("Resetar rede", "Reinicie o PC para terminar.");
    }

    // ---------------- impressão e Windows Update ----------------

    private async void BtnTechSpooler_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        if (!Confirm("Destravar a fila de impressão?\n\nTodos os documentos na fila (de todas as impressoras) são cancelados.")) return;
        await TechRunAsync("Destravar fila de impressão", TechTools.ClearPrintQueueAsync);
    }

    private async void BtnTechWuReset_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        if (!Confirm("Resetar o Windows Update?\n\nPara os serviços, renomeia as pastas SoftwareDistribution e catroot2 (ficam guardadas como .old) " +
                     "e liga tudo de novo. Resolve atualização travada ou com erro. O histórico de atualizações na tela some, " +
                     "mas as atualizações instaladas continuam.")) return;
        await TechRunAsync("Resetar Windows Update", TechTools.ResetWindowsUpdateAsync);
    }

    // ---------------- informações ----------------

    private async void BtnTechLicense_Click(object sender, RoutedEventArgs e)
    {
        TechStatus.Text = "Lendo licença…";
        string r = await Task.Run(TechTools.WindowsLicense);
        TechLog("Licença do Windows", r);
        TechStatus.Text = "";
    }

    private async void BtnTechPrograms_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Salvar lista de programas instalados",
            FileName = $"programas-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}",
            DefaultExt = ".csv",
            Filter = "Planilha CSV (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = await Task.Run(() => TechTools.ExportInstalledPrograms(dlg.FileName));
            TechLog("Programas instalados", $"{n} programas exportados para {dlg.FileName}");
        }
        catch (Exception ex) { TechLog("Programas instalados", $"Falhou: {ex.Message}"); }
    }

    // ---------------- atalhos ----------------

    private void TechShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WinTool tool }) return;
        try { TechTools.Open(tool); }
        catch (Exception ex) { TechStatus.Text = $"{tool.Name}: não foi possível abrir ({ex.Message})."; }
    }

    private void BtnTechClearLog_Click(object sender, RoutedEventArgs e) => TechOutput.Text = "";
}
