using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FortKnox.UI;

namespace FortKnox;

/// <summary>ARSENAL · Instalar apps: essenciais pós-formatação pelo winget (antigo MainWindow.Apps.cs).</summary>
public partial class AppsView : UserControl
{
    private List<InstallableApp>? _apps;
    private bool _busy, _stop;

    public AppsView() => InitializeComponent();

    /// <summary>Instalando agora? (o módulo não deixa a instalação morrer escondida ao sair da página; só ao fechar o app).</summary>
    public bool IsBusy => _busy;

    public void Activate()
    {
        if (_busy) return;
        if (_apps == null)
        {
            _apps = AppInstaller.Catalog();
            AppsList.ItemsSource = _apps;
            AppInstaller.RefreshInstalled(_apps);
            foreach (var a in _apps) a.Selected = a.PreSelect && !a.Installed;
        }
        else AppInstaller.RefreshInstalled(_apps);

        bool winget = AppInstaller.WingetAvailable;
        BtnAppsStore.Visibility = winget ? Visibility.Collapsed : Visibility.Visible;
        BtnAppsInstall.IsEnabled = winget;
        int installed = _apps.Count(a => a.Installed);
        AppsStatus.Text = winget
            ? $"{installed} de {_apps.Count} já instalados. Marque o que quer e clique em INSTALAR SELECIONADOS."
            : "O winget não foi encontrado. Em PC recém-formatado ele aparece depois que a Microsoft Store atualiza o " +
              "\"Instalador de Aplicativo\": use o botão abaixo, espere atualizar e abra esta página de novo.";
    }

    /// <summary>Ao fechar o app: para depois do app atual (o winget não é interrompido no meio de uma instalação).</summary>
    public void RequestStop() => _stop = true;

    private void BtnAppsAll_Click(object sender, RoutedEventArgs e)
    {
        if (_apps == null || _busy) return;
        foreach (var a in _apps) a.Selected = !a.Installed;
    }

    private void BtnAppsNone_Click(object sender, RoutedEventArgs e)
    {
        if (_apps == null || _busy) return;
        foreach (var a in _apps) a.Selected = false;
    }

    private void BtnAppsStore_Click(object sender, RoutedEventArgs e)
    {
        // Página do "Instalador de Aplicativo" (App Installer), que traz o winget.
        try { Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NBLGGH4NNS1") { UseShellExecute = true }); }
        catch (Exception ex) { AppsStatus.Text = $"Não foi possível abrir a Microsoft Store: {ex.Message}"; }
    }

    private async void BtnAppsInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_apps == null) return;
        if (_busy) { _stop = true; BtnAppsInstall.IsEnabled = false; AppsStatus.Text = "Parando depois do app atual…"; return; }

        var sel = _apps.Where(a => a.Selected && !a.Installed).ToList();
        if (sel.Count == 0) { AppsStatus.Text = "Nenhum app selecionado."; return; }
        if (sel.Any(a => a.WingetId == "RARLab.WinRAR") && sel.Any(a => a.WingetId == "7zip.7zip")
            && !FkDialog.Confirm(Window.GetWindow(this), "Instalar WinRAR e 7-Zip?", "Os dois fazem a mesma coisa. Instalar os dois mesmo assim?", "Instalar os dois"))
            return;

        _busy = true; _stop = false;
        BtnAppsInstall.Content = "PARAR";
        foreach (var a in sel) a.Status = "Na fila.";
        int ok = 0, done = 0;
        var failed = new List<string>();
        AppsProgress.ScaleX = 0;

        foreach (var app in sel)
        {
            if (_stop) { app.Status = "Não instalado (parado)."; continue; }
            AppsStatus.Text = $"Instalando {app.Name} ({done + 1} de {sel.Count})… baixando do site oficial, pode levar alguns minutos.";
            app.Status = "Instalando…";
            try
            {
                var (success, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None);
                app.Status = msg;
                if (success) { ok++; app.Selected = false; }
                else failed.Add(app.Name);
            }
            catch (Exception ex) { app.Status = $"Falhou: {ex.Message}"; failed.Add(app.Name); }
            done++;
            AppsProgress.ScaleX = (double)done / sel.Count;
        }

        AppInstaller.RefreshInstalled(_apps);
        _busy = false;
        BtnAppsInstall.Content = "INSTALAR SELECIONADOS";
        BtnAppsInstall.IsEnabled = true;
        AppsStatus.Text = $"{ok} de {sel.Count} instalados." +
                          (failed.Count > 0 ? $" Falharam: {string.Join(", ", failed)} (veja o motivo na lista)." : "") +
                          (_stop ? " Instalação interrompida." : "");
    }
}
