using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>Aba RESET DRIVER: inventário, NVIDIA (verifica e instala), AMD/placa-mãe (página oficial), Windows Update e backup.</summary>
public partial class MainWindow
{
    private bool _drvInit, _drvBusy;
    private List<GpuDriverStatus> _drvGpus = new();
    private BoardInfo _drvBoardInfo = new();
    private List<WuDriver> _drvWu = new();
    private CancellationTokenSource? _drvCts;

    private void InitDriver()
    {
        if (_drvInit) return;
        _drvInit = true;
        BtnDrvScan_Click(this, new RoutedEventArgs());
    }

    private void DrvSet(string text, double? frac = null)
    {
        DrvStatus.Text = text;
        if (frac.HasValue) DrvProgress.ScaleX = Math.Clamp(frac.Value, 0, 1);
    }

    private void DrvBusy(bool busy)
    {
        _drvBusy = busy;
        BtnDrvScan.IsEnabled = !busy;
        BtnDrvBackup.IsEnabled = !busy;
        BtnDrvWuSearch.IsEnabled = !busy;
        BtnDrvInstall.IsEnabled = !busy && _drvGpus.Any(g => g.CanAutoInstall);
        BtnDrvWuInstall.IsEnabled = !busy && _drvWu.Count > 0;
    }

    private async void BtnDrvScan_Click(object sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            DrvSet("Lendo drivers instalados…", 0.1);
            var inv = await Task.Run(DriverManager.Inventory);
            DrvList.ItemsSource = inv;

            DrvSet("Lendo placa de vídeo e placa-mãe…", 0.3);
            _drvGpus = await Task.Run(DriverManager.ReadGpus);
            _drvBoardInfo = await Task.Run(DriverManager.ReadBoard);
            ShowBoard();

            bool studio = DrvStudio.IsChecked == true;
            int i = 0;
            foreach (var g in _drvGpus)
            {
                DrvSet($"Consultando {g.Vendor.ToUpperInvariant()} para {g.GpuName}…", 0.4 + 0.5 * i / Math.Max(1, _drvGpus.Count));
                if (g.Vendor == "NVIDIA") await DriverManager.CheckNvidiaAsync(g, studio, _drvCts.Token);
                else if (g.Vendor == "AMD") await DriverManager.CheckAmdAsync(g, _drvCts.Token);
                else if (g.Vendor == "Intel") g.Message = $"{g.Vendor}: use a página oficial para ver a versão mais recente (instalado: {g.Installed}).";
                else g.Message = "Fabricante não reconhecido.";
                i++;
            }
            DrvGpus.ItemsSource = null;
            DrvGpus.ItemsSource = _drvGpus;
            DrvSet($"Pronto às {DateTime.Now:HH:mm:ss}.", 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private void ShowBoard()
    {
        var b = _drvBoardInfo;
        DrvBoard.Text = $"{b.Maker} {b.Model}\nBIOS: {(b.Bios.Length > 0 ? b.Bios : "—")}" +
                        (b.Brand.Length > 0 ? $"\nFabricante reconhecido: {b.Brand}" : "\nFabricante não reconhecido");
        BtnDrvBoardPage.IsEnabled = b.SupportUrl.Length > 0;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void BtnDrvGpuPage_Click(object sender, RoutedEventArgs e)
    {
        var g = _drvGpus.FirstOrDefault(x => x.OfficialPage.Length > 0 && x.Vendor != "Intel") ?? _drvGpus.FirstOrDefault(x => x.OfficialPage.Length > 0);
        OpenUrl(g?.OfficialPage ?? "https://www.nvidia.com/Download/index.aspx");
    }

    private void BtnDrvBoardPage_Click(object sender, RoutedEventArgs e) => OpenUrl(_drvBoardInfo.SupportUrl);

    private async void BtnDrvInstall_Click(object sender, RoutedEventArgs e)
    {
        var g = _drvGpus.FirstOrDefault(x => x.CanAutoInstall);
        if (g == null || _drvBusy) return;
        string msg = g.Vendor == "AMD"
            ? $"Baixar e abrir o instalador AMD Software {g.Latest}?\nO arquivo vem do servidor oficial da AMD."
            : g.UpdateAvailable
            ? $"Instalar o driver NVIDIA {g.Latest} ({g.SizeText})?\nO instalador oficial será baixado e aberto."
            : $"Você já está com a versão {g.Installed}. Baixar e abrir o instalador {g.Latest} mesmo assim (reinstalação)?";
        if (MessageBox.Show(this, msg, "RESET DRIVER", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            string r = await DriverManager.DownloadAndLaunchAsync(g, (t, f) => Dispatcher.BeginInvoke(new Action(() => DrvSet(t, f))), _drvCts.Token);
            DrvSet(r, 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvWuSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        DrvSet("Procurando drivers no Windows Update (pode levar 1 minuto)…", 0.3);
        try
        {
            _drvWu = await DriverManager.SearchWindowsUpdateAsync();
            DrvWuList.Text = _drvWu.Count == 0
                ? "Nenhum driver novo no Windows Update."
                : string.Join("\n", _drvWu.Select(w => "• " + w.Title + (w.SizeBytes > 0 ? $" ({w.SizeBytes / 1048576.0:F0} MB)" : "")));
            DrvSet($"{_drvWu.Count} driver(s) encontrados.", 1);
        }
        catch (Exception ex) { DrvWuList.Text = "Falhou: " + ex.Message; DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvWuInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_drvBusy || _drvWu.Count == 0) return;
        if (MessageBox.Show(this, $"Instalar {_drvWu.Count} driver(s) do Windows Update? Pode ser preciso reiniciar.", "RESET DRIVER",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DrvBusy(true);
        try
        {
            string r = await DriverManager.InstallWindowsUpdateAsync(_drvWu, t => Dispatcher.BeginInvoke(new Action(() => DrvSet(t, 0.5))));
            DrvSet(r, 1);
            _drvWu = new();
            DrvWuList.Text = r;
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            string r = await DriverManager.BackupAsync(t => Dispatcher.BeginInvoke(new Action(() => DrvSet(t, 0.5))), _drvCts.Token);
            DrvSet(r, 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }
}
