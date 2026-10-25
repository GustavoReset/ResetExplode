using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ResetExplode;

public partial class MainWindow : Window
{
    private readonly SensorMonitor _sensors = new();
    private readonly StressTester _stress = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<InfoNode> _info = new();
    private DateTime _stressStart, _stressEnd;
    private double _maxTemp;
    private readonly List<DiskHealthCard> _extra = new();
    private List<AutostartEntry> _auto = new();
    private List<GameEntry> _games = new();
    private CancellationTokenSource? _netCts;

    public MainWindow()
    {
        InitializeComponent();
        SensorGrid.ItemsSource = _sensors.Rows;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        BtnRefresh.Click += async (_, _) => await LoadInfo();
        BtnReport.Click += (_, _) => SaveReport();
        BtnStress.Click += (_, _) => ToggleStress();
        BtnDisks.Click += async (_, _) => await LoadDisks();
        BtnNet.Click += async (_, _) => await RunNet();
        BtnLaunch.Click += (_, _) => { if ((GamesList.SelectedItem as ListBoxItem)?.Tag is GameEntry g) Tools.Launch(g); };
        BtnToggleAuto.Click += (_, _) => ToggleAutostart();
        Opened += async (_, _) => await LoadAll();
        Closing += (_, _) => _stress.Stop();
    }

    private async Task LoadAll()
    {
        await LoadInfo();
        PowerBox.Text = string.Concat(BatteryInfo.Read().Select(c => c.ToText()));
        _games = await Task.Run(Tools.SteamGames);
        GamesList.ItemsSource = _games.Select(g => new ListBoxItem { Content = $"{g.Name}  ({Sys.Bytes(g.SizeBytes)})", Tag = g }).ToList();
        RefreshAutostart();
        SerialBox.Text = string.Concat(await Task.Run(Tools.SerialPorts).ContinueWith(t => t.Result.Select(c => c.ToText())));
        TweakBox.Text = string.Concat(await Task.Run(Tools.PerformanceTweaks).ContinueWith(t => t.Result.Select(c => c.ToText())));
    }

    private async Task LoadInfo()
    {
        _info = await Task.Run(SystemInfoCollector.Collect);
        InfoTree.ItemsSource = _info.Select(ToItem).ToList();
    }

    private static TreeViewItem ToItem(InfoNode n)
        => new() { Header = n.Display, IsExpanded = n.Header is "Sistema operacional" or "Processador", ItemsSource = n.Children.Select(ToItem).ToList() };

    private async Task LoadDisks()
    {
        DisksBox.Text = "Lendo…";
        var cards = await Task.Run(DiskHealth.Read);
        _extra.Clear(); _extra.AddRange(cards);
        DisksBox.Text = string.Concat(cards.Select(c => c.ToText() + "\n"));
    }

    private async Task RunNet()
    {
        _netCts?.Cancel();
        _netCts = new CancellationTokenSource();
        BtnNet.IsEnabled = false;
        try
        {
            var cards = await NetworkTest.Run((s, p) => Dispatcher.UIThread.Post(() => { NetStatus.Text = s; NetProg.Value = p; }), _netCts.Token);
            NetBox.Text = string.Concat(cards.Select(c => c.ToText()));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { NetBox.Text = "Erro: " + ex.Message; }
        finally { BtnNet.IsEnabled = true; }
    }

    private void RefreshAutostart()
    {
        _auto = Tools.Autostart();
        AutoList.ItemsSource = _auto.Select(a => new ListBoxItem { Content = $"[{(a.Enabled ? "ativo" : "desativado")}] {a.Name}", Tag = a }).ToList();
    }

    private void ToggleAutostart()
    {
        if ((AutoList.SelectedItem as ListBoxItem)?.Tag is AutostartEntry a) { Tools.SetAutostartEnabled(a, !a.Enabled); RefreshAutostart(); }
    }

    private void SaveReport()
    {
        string path = Path.Combine(Sys.Home, $"resetexplode-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, Report.Build(_info, _sensors.Rows, _extra));
        TxtReport.Text = "Salvo em " + path;
    }

    private void ToggleStress()
    {
        if (_stress.Running) { StopStress("parado pelo usuário"); return; }
        if (ChkCpu.IsChecked != true && ChkRam.IsChecked != true) return;
        _maxTemp = 0;
        _stressStart = DateTime.Now;
        _stressEnd = _stressStart.AddMinutes((double)(NumMin.Value ?? 10));
        StressLog.Text = $"{DateTime.Now:HH:mm:ss} iniciado\n";
        _stress.Start(ChkCpu.IsChecked == true, ChkRam.IsChecked == true);
        BtnStress.Content = "Parar";
    }

    private void StopStress(string why)
    {
        _stress.Stop();
        BtnStress.Content = "Iniciar";
        StressLog.Text += $"{DateTime.Now:HH:mm:ss} {why}\n";
    }

    private void Tick()
    {
        _sensors.Update();
        if (!_stress.Running) return;
        double temp = _sensors.CpuTemp ?? 0;
        _maxTemp = Math.Max(_maxTemp, temp);
        StressLog.Text += $"{DateTime.Now:HH:mm:ss}  CPU {_sensors.CpuLoad:0}%  {temp:0} °C (máx {_maxTemp:0})  {_sensors.CpuPower:0.#} W" +
                          (ChkRam.IsChecked == true ? $"  RAM passes {_stress.MemoryPasses} erros {_stress.MemoryErrors}" : "") + "\n";
        StressLog.CaretIndex = StressLog.Text.Length;
        if (_stress.MemoryErrors > 0) StopStress("ERRO DE MEMÓRIA detectado");
        else if (temp >= (double)(NumTemp.Value ?? 95)) StopStress($"limite de {NumTemp.Value:0} °C atingido");
        else if (DateTime.Now >= _stressEnd) StopStress("tempo concluído");
    }
}
