using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>Aba RESET MAKER · Drivers seriais: CH340, CP210x, FTDI, Prolific, Arduino e Arduino IDE.</summary>
public partial class MainWindow
{
    private bool _makerInit, _makerBusy;
    private List<SerialDeviceRow> _makerDevices = new();

    private void InitMakerDrivers()
    {
        if (_makerInit) { RefreshMakerDevices(); return; }
        _makerInit = true;
        BtnMakerScan_Click(this, new RoutedEventArgs());
    }

    private void MakerBusy(bool busy, string? status = null)
    {
        _makerBusy = busy;
        BtnMakerScan.IsEnabled = BtnMakerBundleAll.IsEnabled = BtnMakerIde.IsEnabled = !busy;
        if (status != null) MakerStatus.Text = status;
    }

    private async void BtnMakerScan_Click(object sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        MakerBusy(true, "Verificando placas, conversores e drivers…");
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        RefreshMakerIde();
        MakerBusy(false);
        int bad = _makerDevices.Count(d => !d.Ok);
        MakerStatus.Text = _makerDevices.Count == 0 ? "Nenhuma placa conectada agora. Os drivers instalados aparecem abaixo."
                         : bad == 0 ? $"{_makerDevices.Count} conectados, todos funcionando."
                         : $"{bad} de {_makerDevices.Count} com problema: veja a orientação em cada um.";
    }

    private void RefreshMakerDevices()
    {
        try { _makerDevices = MakerDrivers.ConnectedDevices(); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; _makerDevices = new(); }
        MakerDevices.ItemsSource = _makerDevices;
        MakerNoDevices.Visibility = _makerDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshMakerPackagesAsync()
    {
        List<SerialDriverPackage> pkgs;
        try { pkgs = await MakerDrivers.DriverPackagesAsync(_makerDevices); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível ler os drivers: {ex.Message}"; pkgs = new(); }
        MakerPackages.ItemsSource = pkgs;
        MakerNoPackages.Visibility = pkgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MakerBundled.ItemsSource = MakerDrivers.Bundled(pkgs);
        BtnMakerRestore.IsEnabled = MakerDrivers.HasBackups;
    }

    private void RefreshMakerIde()
    {
        string? v = MakerDrivers.IdeVersion();
        MakerIde.Text = v != null ? $"Arduino IDE {v} instalada{(MakerDrivers.IdeRunning ? " · aberta agora" : "")}." : "Arduino IDE não instalada.";
        BtnMakerIde.Content = v != null ? "ATUALIZAR A IDE" : "INSTALAR A IDE";
        var holders = MakerDrivers.RunningPortHolders();
        MakerHolders.Text = holders.Count == 0 ? "Nenhum programa conhecido prendendo portas COM agora."
                          : "Podem estar prendendo a porta: " + string.Join(", ", holders.Select(p => p.ProcessName).Distinct()) + ".";
    }

    private async void MakerReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SerialDeviceRow row } || _makerBusy) return;
        if (!RequireAdmin()) return;
        MakerBusy(true, $"Reinstalando {row.Name}…");
        try { MakerStatus.Text = await MakerDrivers.ReinstallAsync(row.Device); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1500); // dá tempo do Windows terminar a instalação antes de reler
        RefreshMakerDevices();
        MakerBusy(false);
    }

    private void MakerOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SerialChip chip }) return;
        try { Process.Start(new ProcessStartInfo(chip.OfficialUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível abrir o navegador: {ex.Message}"; }
    }

    private async void MakerInstallBundled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BundledDriver b } || _makerBusy) return;
        if (!RequireAdmin()) return;
        await InstallBundledAsync(new[] { b.Chip });
    }

    private async void BtnMakerBundleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_makerBusy || !RequireAdmin()) return;
        var chips = MakerDrivers.Chips.Where(c => c.Bundle != null).ToList();
        await InstallBundledAsync(chips);
    }

    private async Task InstallBundledAsync(IReadOnlyList<SerialChip> chips)
    {
        var results = new List<string>();
        foreach (var c in chips)
        {
            MakerBusy(true, $"Instalando o driver do {c.Name}…");
            try { results.Add(await MakerDrivers.InstallBundledAsync(c)); }
            catch (Exception ex) { results.Add($"{c.Name}: falhou ({ex.Message})"); }
        }
        await Task.Delay(1000);
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false, string.Join(" ", results.Where(r => !r.Contains("não está incluído"))));
    }

    private async void MakerRemovePackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SerialDriverPackage p } || _makerBusy) return;
        if (!RequireAdmin()) return;
        var r = MessageBox.Show($"Remover o driver {p.Title} ({p.PublishedName}) do Windows?\n\n" +
                                (p.InUse ? "Ele está EM USO por uma placa conectada: o Windows troca para outra versão guardada, " +
                                           "ou a placa fica sem driver se esta for a única.\n\n" : "") +
                                "Um backup é feito antes; dá para desfazer em \"Restaurar removidos\".",
                                "RESET MAKER", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        MakerBusy(true, $"Removendo {p.Title}…");
        try { MakerStatus.Text = await MakerDrivers.RemovePackageAsync(p); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1000);
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false);
    }

    private async void BtnMakerRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_makerBusy || !RequireAdmin()) return;
        var r = MessageBox.Show("Devolver ao Windows todos os drivers seriais removidos pelo RESET MAKER?", "RESET MAKER",
                                MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        MakerBusy(true, "Restaurando drivers…");
        try { MakerStatus.Text = await MakerDrivers.RestoreBackupsAsync(); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false);
    }

    private async void BtnMakerIde_Click(object sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        if (!AppInstaller.WingetAvailable)
        {
            MakerIdeResult.Text = "O winget não foi encontrado: abra RESET FERRAMENTAS › Instalar apps para ver como resolver, ou baixe a IDE no site.";
            return;
        }
        if (MakerDrivers.IdeRunning)
        {
            MakerIdeResult.Text = "Feche a Arduino IDE antes (salve o sketch) e clique de novo.";
            return;
        }
        bool installed = MakerDrivers.IdeVersion() != null;
        var app = new InstallableApp { Name = "Arduino IDE", WingetId = "ArduinoSA.IDE.stable" };
        MakerBusy(true);
        MakerIdeResult.Text = installed ? "Atualizando a Arduino IDE… (pode levar alguns minutos)" : "Instalando a Arduino IDE… (pode levar alguns minutos)";
        try
        {
            var (ok, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None, upgrade: installed);
            // "upgrade" sem versão nova responde com o código de "nada para atualizar"
            MakerIdeResult.Text = installed && ok && msg != "Instalado." ? "A IDE já está na versão mais recente." : msg;
        }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
        RefreshMakerIde();
        MakerBusy(false);
    }

    private void BtnMakerHolders_Click(object sender, RoutedEventArgs e)
    {
        var procs = MakerDrivers.RunningPortHolders();
        if (procs.Count == 0) { MakerIdeResult.Text = "Nenhum programa conhecido prendendo portas COM."; RefreshMakerIde(); return; }
        var names = procs.Select(p => p.ProcessName).Distinct().ToList();
        var r = MessageBox.Show("Fechar estes programas? Trabalho não salvo neles pode ser perdido.\n\n" + string.Join("\n", names.Select(n => "  • " + n)),
                                "RESET MAKER", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        int closed = 0;
        foreach (var p in procs)
            try { p.Kill(entireProcessTree: true); closed++; } catch { }
        MakerIdeResult.Text = $"{closed} programas fechados. Tente o upload de novo.";
        RefreshMakerIde();
    }

    private void BtnMakerCache_Click(object sender, RoutedEventArgs e)
    {
        if (MakerDrivers.IdeRunning) { MakerIdeResult.Text = "Feche a Arduino IDE antes de limpar o cache."; return; }
        try { MakerIdeResult.Text = MakerDrivers.ClearIdeCache(); }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
    }
}
