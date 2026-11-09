using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FortKnox.UI;

namespace FortKnox;

/// <summary>OFICINA · Drivers seriais: CH340, CP210x, FTDI, Prolific, Arduino e Arduino IDE (antigo MainWindow.Maker.cs).</summary>
public partial class SerialDriversView : UserControl
{
    private bool _init, _busy;
    private List<SerialDeviceRow> _devices = new();

    public SerialDriversView() => InitializeComponent();

    private Window? Owner => Window.GetWindow(this);

    public void Activate()
    {
        if (_init) { RefreshDevices(); return; }
        _init = true;
        BtnMakerScan_Click(this, new RoutedEventArgs());
    }

    /// <summary>Placa plugada ou removida: atualiza só o diagnóstico (barato).</summary>
    public void OnDevicesChanged() { if (_init && !_busy) RefreshDevices(); }

    private void Busy(bool busy, string? status = null)
    {
        _busy = busy;
        BtnMakerScan.IsEnabled = BtnMakerBundleAll.IsEnabled = BtnMakerIde.IsEnabled = !busy;
        if (status != null) MakerStatus.Text = status;
    }

    private async void BtnMakerScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Busy(true, "Verificando placas, conversores e drivers…");
        RefreshDevices();
        await RefreshPackagesAsync();
        RefreshIde();
        Busy(false);
        int bad = _devices.Count(d => !d.Ok);
        MakerStatus.Text = _devices.Count == 0 ? "Nenhuma placa conectada agora. Os drivers instalados aparecem abaixo."
                         : bad == 0 ? $"{_devices.Count} conectados, todos funcionando."
                         : $"{bad} de {_devices.Count} com problema: veja a orientação em cada um.";
    }

    private void RefreshDevices()
    {
        try { _devices = MakerDrivers.ConnectedDevices(); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; _devices = new(); }
        MakerDevices.ItemsSource = _devices;
        MakerNoDevices.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshPackagesAsync()
    {
        List<SerialDriverPackage> pkgs;
        try { pkgs = await MakerDrivers.DriverPackagesAsync(_devices); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível ler os drivers: {ex.Message}"; pkgs = new(); }
        MakerPackages.ItemsSource = pkgs;
        MakerNoPackages.Visibility = pkgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MakerBundled.ItemsSource = MakerDrivers.Bundled(pkgs);
        BtnMakerRestore.IsEnabled = MakerDrivers.HasBackups;
    }

    private void RefreshIde()
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
        if (sender is not FrameworkElement { Tag: SerialDeviceRow row } || _busy) return;
        if (!AdminGuard.Require(this)) return;
        Busy(true, $"Reinstalando {row.Name}…");
        try { MakerStatus.Text = await MakerDrivers.ReinstallAsync(row.Device); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1500); // dá tempo do Windows terminar a instalação antes de reler
        RefreshDevices();
        Busy(false);
    }

    private void MakerOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SerialChip chip }) return;
        try { Process.Start(new ProcessStartInfo(chip.OfficialUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível abrir o navegador: {ex.Message}"; }
    }

    private async void MakerInstallBundled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BundledDriver b } || _busy) return;
        if (!AdminGuard.Require(this)) return;
        await InstallBundledAsync(new[] { b.Chip });
    }

    private async void BtnMakerBundleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !AdminGuard.Require(this)) return;
        await InstallBundledAsync(MakerDrivers.Chips.Where(c => c.Bundle != null).ToList());
    }

    private async Task InstallBundledAsync(IReadOnlyList<SerialChip> chips)
    {
        var results = new List<string>();
        foreach (var c in chips)
        {
            Busy(true, $"Instalando o driver do {c.Name}…");
            try { results.Add(await MakerDrivers.InstallBundledAsync(c)); }
            catch (Exception ex) { results.Add($"{c.Name}: falhou ({ex.Message})"); }
        }
        await Task.Delay(1000);
        RefreshDevices();
        await RefreshPackagesAsync();
        Busy(false, string.Join(" ", results.Where(r => !r.Contains("não está incluído"))));
    }

    private async void MakerRemovePackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SerialDriverPackage p } || _busy) return;
        if (!AdminGuard.Require(this)) return;
        string msg = (p.InUse ? "Ele está em uso por uma placa conectada: o Windows troca para outra versão guardada, " +
                                "ou a placa fica sem driver se esta for a única. " : "") +
                     "Guardamos uma cópia para você voltar quando quiser, em Restaurar removidos.";
        if (!FkDialog.Confirm(Owner, $"Remover o driver {p.Title} ({p.PublishedName})?", msg, "Remover", kind: DialogKind.Destructive)) return;
        Busy(true, $"Removendo {p.Title}…");
        try { MakerStatus.Text = await MakerDrivers.RemovePackageAsync(p); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1000);
        RefreshDevices();
        await RefreshPackagesAsync();
        Busy(false);
    }

    private async void BtnMakerRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !AdminGuard.Require(this)) return;
        if (!FkDialog.Confirm(Owner, "Restaurar os drivers removidos?", "Devolve ao Windows todos os drivers seriais removidos pela OFICINA.", "Restaurar")) return;
        Busy(true, "Restaurando drivers…");
        try { MakerStatus.Text = await MakerDrivers.RestoreBackupsAsync(); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        RefreshDevices();
        await RefreshPackagesAsync();
        Busy(false);
    }

    private async void BtnMakerIde_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!AppInstaller.WingetAvailable)
        {
            MakerIdeResult.Text = "O winget não foi encontrado: abra Arsenal › Instalar apps para ver como resolver, ou baixe a IDE no site.";
            return;
        }
        if (MakerDrivers.IdeRunning)
        {
            MakerIdeResult.Text = "Feche a Arduino IDE antes (salve o sketch) e clique de novo.";
            return;
        }
        bool installed = MakerDrivers.IdeVersion() != null;
        var app = new InstallableApp { Name = "Arduino IDE", WingetId = "ArduinoSA.IDE.stable" };
        Busy(true);
        MakerIdeResult.Text = installed ? "Atualizando a Arduino IDE… (pode levar alguns minutos)" : "Instalando a Arduino IDE… (pode levar alguns minutos)";
        try
        {
            var (ok, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None, upgrade: installed);
            // "upgrade" sem versão nova responde com o código de "nada para atualizar"
            MakerIdeResult.Text = installed && ok && msg != "Instalado." ? "A IDE já está na versão mais recente." : msg;
        }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
        RefreshIde();
        Busy(false);
    }

    private void BtnMakerHolders_Click(object sender, RoutedEventArgs e)
    {
        var procs = MakerDrivers.RunningPortHolders();
        if (procs.Count == 0) { MakerIdeResult.Text = "Nenhum programa conhecido prendendo portas COM."; RefreshIde(); return; }
        var names = procs.Select(p => p.ProcessName).Distinct().ToList();
        if (!FkDialog.Confirm(Owner, "Fechar estes programas?", string.Join(", ", names) + ". Trabalho não salvo neles pode ser perdido.",
                              "Fechar programas", kind: DialogKind.Destructive))
            return;
        int closed = 0;
        foreach (var p in procs)
            try { p.Kill(entireProcessTree: true); closed++; } catch { }
        MakerIdeResult.Text = $"{closed} programas fechados. Tente o upload de novo.";
        RefreshIde();
    }

    private void BtnMakerCache_Click(object sender, RoutedEventArgs e)
    {
        if (MakerDrivers.IdeRunning) { MakerIdeResult.Text = "Feche a Arduino IDE antes de limpar o cache."; return; }
        try { MakerIdeResult.Text = MakerDrivers.ClearIdeCache(); }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
    }
}
