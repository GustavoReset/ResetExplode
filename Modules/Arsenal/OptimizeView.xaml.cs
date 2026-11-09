using System.Windows;
using System.Windows.Controls;
using FortKnox.UI;

namespace FortKnox;

/// <summary>
/// ARSENAL · Otimização (Fase 3.3k; antes no MainWindow.xaml.cs): limpa a memória, fecha programas em segundo plano
/// e liga/desliga ajustes do Windows com "Restaurar originais". O backup dos ajustes continua em AppSettings.TweakBackup.
/// </summary>
public partial class OptimizeView : UserControl
{
    private List<BackgroundApp> _apps = new();
    private readonly List<Tweak> _tweaks = WindowsTweaks.All(); // só as definições; o estado é lido ao abrir a página
    private bool _optInit;
    private AppSettings _settings = null!;
    private Action _save = () => { };

    public OptimizeView() => InitializeComponent();

    public void Attach(AppSettings settings, Action save) { _settings = settings; _save = save; }

    /// <summary>Definições dos ajustes do Windows (o PAINEL lê Modo de Jogo e Game DVR para a saúde do PC).</summary>
    public IReadOnlyList<Tweak> Tweaks => _tweaks;

    /// <summary>A cada 1 s com a página aberta: uso da RAM.</summary>
    public void Tick() { if (IsVisible) UpdateRamStatus(); }

    private Window? Owner => Window.GetWindow(this);

    /// <summary>Carrega a página ARSENAL · OTIMIZAÇÃO quando ela é aberta (os ajustes do Windows só na primeira vez).</summary>
    public void Activate()
    {
        if (!_optInit)
        {
            _optInit = true;
            foreach (var t in _tweaks)
                try { t.Refresh(); } catch (Exception ex) { t.Status = $"Não foi possível ler: {ex.Message}"; }
            OptTweaks.ItemsSource = _tweaks;
        }
        RefreshApps();
        UpdateRamStatus();
    }

    private void UpdateRamStatus()
    {
        var (total, avail) = Optimizer.MemoryStatus();
        if (total == 0) return;
        double used = total - avail;
        OptRamText.Text = $"{used / 1073741824.0:0.0} de {total / 1073741824.0:0.0} GB · {used / total:P0}";
        OptRamBar.ScaleX = used / total;
    }

    private void RefreshApps()
    {
        _apps = Optimizer.ListApps();
        OptApps.ItemsSource = _apps;
    }

    private void BtnRefreshApps_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private async void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        var toClose = _apps.Where(a => a.Selected).ToList();
        string list = toClose.Count == 0 ? "nenhum programa marcado"
                                         : string.Join(", ", toClose.Select(a => a.Title));
        if (!FkDialog.Confirm(Owner, "Otimizar agora?",
                "O ARSENAL vai limpar a memória em espera, enxugar a RAM dos programas e fechar: " + list +
                ". Salve o que estiver aberto nesses programas antes.", "Otimizar"))
            return;

        BtnOptimize.IsEnabled = false;
        BtnOptimize.Content = "OTIMIZANDO…";
        OptResult.Text = "";
        var closeResult = await Task.Run(() => Optimizer.CloseApps(toClose));
        var mem = await Task.Run(Optimizer.CleanMemory); // depois de fechar, para contar a memória liberada por eles também
        BtnOptimize.IsEnabled = true;
        BtnOptimize.Content = "OTIMIZAR AGORA";

        var parts = new List<string> { $"Memória liberada: {mem.FreedBytes / 1048576.0:N0} MB" };
        parts.Add(mem.StandbyPurged ? "memória em espera limpa" : $"memória em espera não limpa ({mem.StandbyError})");
        if (closeResult.Closed.Count > 0) parts.Add($"fechados: {string.Join(", ", closeResult.Closed)}");
        if (closeResult.NotClosed.Count > 0)
            parts.Add($"não fecharam (podem ter algo não salvo ou pedir confirmação): {string.Join(", ", closeResult.NotClosed)}");
        OptResult.Text = string.Join(" · ", parts) + ".";
        RefreshApps();
        UpdateRamStatus();
    }

    private void Tweak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Tweak t } box) return;
        bool turnOn = box.IsChecked == true;
        try
        {
            t.Write(turnOn, _settings);
            _save();
            t.Refresh();
            t.Status = t.IsOn == turnOn
                ? (turnOn ? "Aplicado." : "Desligado.") + (t.NeedsRestart ? " Reinicie o PC para valer." : "")
                : "O Windows não aceitou a mudança (pode exigir administrador).";
        }
        catch (Exception ex)
        {
            t.Refresh();
            t.Status = $"Falhou: {ex.Message}";
        }
        box.IsChecked = t.IsOn; // o binding é só leitura: volta ao estado real
    }

    private void BtnRestoreTweaks_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.TweakBackup.Count == 0)
        {
            FkDialog.Info(Owner, "Nada para restaurar", "Nenhum ajuste foi alterado pelo Fort Knox ainda.");
            return;
        }
        if (!FkDialog.Confirm(Owner, "Restaurar os ajustes originais?",
                              "Os ajustes do Windows voltam para como estavam antes do Fort Knox alterá-los.", "Restaurar"))
            return;
        bool restart = _tweaks.Any(t => t.NeedsRestart && _settings.TweakBackup.ContainsKey(t.Id));
        WindowsTweaks.RestoreAll(_tweaks, _settings);
        foreach (var t in _tweaks) { t.Refresh(); t.Status = "Restaurado."; }
        if (restart) FkDialog.Info(Owner, "Reinicie para concluir", "Alguns ajustes só voltam ao original depois de reiniciar o PC.");
    }
}
