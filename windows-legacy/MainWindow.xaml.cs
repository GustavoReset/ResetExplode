using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

public partial class MainWindow : Window
{
    private readonly SensorMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly ICollectionView _view;
    private string? _category;   // filtro por chip (null = todos)
    private string _search = ""; // filtro por texto

    // Placa de vídeo
    private List<GpuInfo> _gpus = new();
    private GpuInfo? _gpu;

    // Stress test
    private readonly StressTester _stress = new();
    private readonly List<double> _tempHistory = new();
    private const int HistoryLength = 300; // 5 min a 1 amostra/s
    private DateTime _stressStart;
    private TimeSpan _stressDuration;
    private double _tempLimit = 90;
    private float _stressMaxTemp;

    // Jogos
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HowManyFpsClient _fpsApi = new();
    private List<GameRow> _gameRows = new();
    private ListCollectionView? _gameView;
    private CancellationTokenSource? _gameFetch;
    private string _cpuName = "", _gpuName = "";
    private double _cpuIdx = 80, _gpuIdx = 50;

    // Gargalo + overlay
    public sealed record MeterItem(string Label, string Value, double Fraction);
    private readonly FrameMonitor _frames = new();
    private BottleneckSession? _bnSession;
    private double _balanceRatio = 1;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _overlayTimer;
    private bool _overlayReady;
    private const int HotkeyId = 0x5245; // "RE"

    // RESET BENCH
    private readonly BenchRenderer _bench = new();
    private System.Windows.Media.Imaging.WriteableBitmap? _benchBitmap;
    private CancellationTokenSource? _benchCts;
    private DispatcherTimer? _benchTimer;
    private DateTime _benchStart;
    private string _benchPhase = "";
    private DateTime _benchPhaseStart;

    // RESET OTIMIZA
    private List<BackgroundApp> _apps = new();
    private readonly List<Tweak> _tweaks = WindowsTweaks.All(); // só as definições; o estado é lido ao abrir a página
    private bool _optInit;

    // Stress de GPU (estilo FurMark)
    private readonly GpuBurn _burn = new();
    private bool _gpuBurnOn;
    private double _gpuTempLimit = 85;
    private float _gpuMaxTemp;
    private readonly List<double> _gpuTempHistory = new();

    private Brush Fg => (Brush)FindResource("Fg");
    private Brush Ink => (Brush)FindResource("Ink");
    private Brush Muted => (Brush)FindResource("Muted");
    private Brush Card => (Brush)FindResource("Card");

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Apply(_settings.LightTheme);
        UpdateThemeButton();
        ShowPage("PageDash");

        // Sensores: a lista começa vazia e é preenchida quando a detecção do hardware termina (LoadHardwareAsync).
        _monitor = new SensorMonitor();
        _view = CollectionViewSource.GetDefaultView(_monitor.Rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Hardware)));
        SensorGrid.ItemsSource = _view;
        SystemSub.Text = "Lendo informações do sistema…";
        StatusText.Text = "Detectando o hardware…";

        InitBench();
        SnapshotUsb();
        RefreshDrives();
        TitlePage.Text = "Visão geral";
        UpdateDashboard();

        // A janela aparece já; as coletas pesadas (WMI, sensores, GPU) rodam em paralelo fora da thread da janela.
        Loaded += async (_, _) => await LoadHardwareAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            // Durante a cena de GPU as leituras de sensores (NVML/driver) ficam pausadas: cada consulta trava o driver
            // por um instante e aparecia como engasgo no 1% low do benchmark.
            if (_gpuBenchActive) return;
            _monitor.Refresh();
            if (_monitor.IsOpen) StatusText.Text = $"{_monitor.Rows.Count} sensores · atualizado {DateTime.Now:HH:mm:ss}";
            UpdateStressPanel();
            UpdateGpuLive();
            UpdateCpuSensorWarning();
            CollectDashSample();
            UpdateBottleneck();
            if (PageBench.IsVisible) UpdateBenchLive();
            if (PageOptimize.IsVisible) UpdateRamStatus();
        };
        _timer.Start();

        StateChanged += (_, _) =>
        {
            // Sem borda nativa, a janela maximizada vaza ~7 px para fora da tela.
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? new Thickness(7) : new Thickness(0);
            BtnMax.Content = max ? "" : "";
        };
        Closed += (_, _) =>
        {
            _timer.Stop(); _overlayTimer?.Stop();
            _stress.Stop(); _burn.Stop(); _benchCts?.Cancel();
            _overlay?.Close();
            _toast?.Close();
            _frames.Dispose();
            UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, HotkeyId);
            ShutdownInteg();
            _launchCts?.Cancel();
            _monitor.Dispose();
        };
    }

    /// <summary>Coletas lentas da abertura, em paralelo e fora da thread da janela; cada parte preenche a tela quando fica pronta.</summary>
    private async Task LoadHardwareAsync()
    {
        async Task LoadInfo()
        {
            try
            {
                var info = await Task.Run(SystemInfoCollector.Collect);
                InfoCategories.ItemsSource = info;
                InfoCategories.SelectedIndex = 0;
                SystemSub.Text = $"{Environment.MachineName} · {info.Count} categorias";
            }
            catch (Exception ex) { SystemSub.Text = $"Não foi possível ler as informações do sistema: {ex.Message}"; }
        }

        async Task LoadSensors()
        {
            try { await _monitor.OpenAsync(); }
            catch (Exception ex) { StatusText.Text = $"Sensores indisponíveis: {ex.Message}"; return; }
            _monitor.Refresh();
            UpdateCpuSensorWarning();
            AddChip("Todos", null, isChecked: true);
            foreach (var c in _monitor.Rows.Select(r => r.Category).Distinct().OrderBy(x => x))
                AddChip(c, c);
            UpdateDashboard();
        }

        async Task LoadGpu()
        {
            try { _gpus = await Task.Run(GpuInfoCollector.Collect); }
            catch { _gpus = new(); }
            for (int i = 0; i < _gpus.Count && _gpus.Count > 1; i++)
            {
                var g = _gpus[i];
                var chip = new RadioButton { Content = g.Name, GroupName = "Gpu", Style = (Style)FindResource("Chip"), IsChecked = i == 0 };
                chip.Checked += (_, _) => ShowGpu(g);
                GpuChips.Children.Add(chip);
            }
            if (_gpus.Count > 0) ShowGpu(_gpus[0]);

            // Dependem do nome da GPU/CPU
            InitGames();
            InitBottleneckAndOverlay();
            InitOc();
            UpdateDashboard();
        }

        await Task.WhenAll(LoadInfo(), LoadSensors(), LoadGpu());
    }

    // ---------------- Sensores da CPU (PawnIO) ----------------

    /// <summary>Mostra o aviso no menu quando temperatura/consumo da CPU não respondem, com o motivo provável.</summary>
    private void UpdateCpuSensorWarning()
    {
        if (!_monitor.IsOpen) return; // ainda detectando: não acusar falta de sensor antes da hora
        var p = _monitor.DiagnoseCpuSensors();
        CpuSensorWarn.Visibility = p == SensorMonitor.CpuSensorProblem.None ? Visibility.Collapsed : Visibility.Visible;
        CpuSensorWarnText.Text = p switch
        {
            SensorMonitor.CpuSensorProblem.NotElevated => "Sem permissão de administrador.",
            SensorMonitor.CpuSensorProblem.PawnIOMissing => "Falta o driver PawnIO.",
            SensorMonitor.CpuSensorProblem.ReadFailed => "O driver não respondeu.",
            _ => ""
        };
    }

    private void BtnFixCpuSensors_Click(object sender, RoutedEventArgs e)
    {
        var p = _monitor.DiagnoseCpuSensors();
        string why = SensorMonitor.Explain(p);
        if (p != SensorMonitor.CpuSensorProblem.PawnIOMissing)
        {
            MessageBox.Show(why, "RESET EXPLODE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // winget vem com o Windows 11 e com o Windows 10 atualizado (App Installer).
        string winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                               @"Microsoft\WindowsApps\winget.exe");
        if (!System.IO.File.Exists(winget))
        {
            MessageBox.Show(why + "\n\nO instalador de pacotes do Windows (winget) não foi encontrado neste PC. " +
                            "Atualize o \"Instalador de Aplicativo\" pela Microsoft Store e use o botão de novo, " +
                            "ou instale o PawnIO (autor: namazso) manualmente.",
                            "RESET EXPLODE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var r = MessageBox.Show(why + "\n\nInstalar o PawnIO agora? Vai abrir uma janela com o instalador do Windows (winget). " +
                                "Quando terminar, feche o RESET EXPLODE e abra de novo.",
                                "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/k winget install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements" +
            " && echo. && echo Pronto! Feche esta janela e reabra o RESET EXPLODE.")
        { UseShellExecute = true });
    }

    // ---------------- Janela / navegação ----------------

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageSystem == null) return; // disparado durante InitializeComponent
        var item = (FrameworkElement)sender;
        string page = (string)item.Tag;
        ShowPage(page);

        // Abre a categoria da página (ex.: quando o painel pula direto para o Benchmark)
        // e mostra "CATEGORIA · página" na barra de título.
        string name = item is ContentControl { Content: Panel panel }
            ? panel.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? "" : "";
        if (item.Parent is FrameworkElement { Name: var items } && items.EndsWith("Items")
            && FindName(items[..^5]) is System.Windows.Controls.Primitives.ToggleButton group)
        {
            group.IsChecked = true;
            name = $"{group.Content} · {name}";
        }
        TitlePage.Text = name;

        // Algumas páginas carregam dados só quando abertas.
        switch (page)
        {
            case "PageDash": UpdateDashboard(); break;
            case "PageDisks": RefreshDiskHealth(); break;
            case "PageEnergy": RefreshEnergy(); break;
            case "PageOc": UpdateOcGpuState(); UpdateRecentErrors(); break;
            case "PageInteg": InitInteg(); break;
            case "PageLauncher": InitLauncher(); break;
            case "PageDriver": InitDriver(); break;
            case "PageStartup": ScanStartup(); break;
            case "PageApps": InitApps(); break;
            case "PageMakerPorts": ScanUsb(); break;
            case "PageMakerDrv": InitMakerDrivers(); break;
            case "PageOptimize": InitOptimize(); break;
        }
    }

    private void ShowPage(string name)
    {
        foreach (var p in new FrameworkElement[] { PageDash, PageSystem, PageSensors, PageGpu, PageGames, PageBottleneck, PageOverlay, PageBench, PageOptimize, PageOc, PageInteg, PageLauncher, PageDriver, PageStartup, PageApps, PageMakerPorts, PageMakerDrv, PageDisks, PageEnergy, PageNet, PageStress })
            p.Visibility = p.Name == name ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Sistema ----------------

    private void InfoCategories_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InfoCategories.SelectedItem is not InfoNode node) return;
        InfoTitle.Text = node.Header;
        InfoDetails.ItemsSource = node.Children;
        bool storage = node.Header == "Armazenamento";
        DrivesPanel.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
        if (storage) RefreshDrives();
    }

    // ---------------- Unidades e dispositivos conectados ----------------

    private List<DriveCard> _drives = new();
    private Dictionary<string, UsbDevice> _usbPresent = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastVolumeEvent = DateTime.MinValue;
    private DispatcherTimer? _usbDebounce;
    private ToastWindow? _toast;

    private async void RefreshDrives()
    {
        _drives = await Task.Run(DriveMap.Read);
        DriveCards.ItemsSource = _drives;
        long total = _drives.Sum(d => d.Total), free = _drives.Sum(d => d.Free);
        DrivesSummary.Text = $"{_drives.Count} unidades · {DriveCard.Fmt(free)} livres de {DriveCard.Fmt(total)}";
    }

    private void Toast(string title, string text, string icon)
    {
        AddEvent(icon, title, text.Split('\n')[0], "");
        _toast ??= new ToastWindow();
        _toast.Notify(title, text, icon);
    }

    private async void SnapshotUsb()
    {
        try
        {
            var list = await Task.Run(UsbCleaner.List);
            _usbPresent = list.Where(d => d.Present).GroupBy(d => d.InstanceId).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    /// <summary>WM_DEVICECHANGE: unidades (pendrive, HD externo) e qualquer dispositivo USB.</summary>
    private void OnDeviceChange(int ev, IntPtr lParam)
    {
        const int DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
        if ((ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero &&
            Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            int mask = Marshal.ReadInt32(lParam, 12); // DEV_BROADCAST_VOLUME.dbcv_unitmask: bit 0 = A:
            var letters = Enumerable.Range(0, 26).Where(i => (mask & (1 << i)) != 0).Select(i => $"{(char)('A' + i)}:").ToList();
            _lastVolumeEvent = DateTime.Now;
            OnVolumes(letters, ev == DBT_DEVICEARRIVAL);
        }
        else if (ev == DBT_DEVNODES_CHANGED)
        {
            // Chega várias vezes seguidas por dispositivo: espera assentar e compara com a lista anterior.
            _usbDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Normal, (_, _) =>
            {
                _usbDebounce!.Stop();
                DiffUsb();
            }, Dispatcher);
            _usbDebounce.Stop();
            _usbDebounce.Start();
        }
    }

    private async void OnVolumes(List<string> letters, bool arrived)
    {
        if (arrived) await Task.Delay(800); // o sistema de arquivos leva um instante para montar
        var before = _drives;
        var now = await Task.Run(DriveMap.Read);
        foreach (var l in letters)
        {
            if (arrived && now.FirstOrDefault(d => d.Letter.Equals(l, StringComparison.OrdinalIgnoreCase)) is { } d)
                Toast(d.Removable ? "Pendrive conectado" : "Unidade conectada",
                      $"{d.Title} · {DriveCard.Fmt(d.Free)} livres de {DriveCard.Fmt(d.Total)}" + (d.Disk.Length > 0 ? $"\n{d.Disk}" : ""), "");
            else if (!arrived)
            {
                var old = before.FirstOrDefault(d => d.Letter.Equals(l, StringComparison.OrdinalIgnoreCase));
                Toast("Unidade removida", old != null ? $"{old.Title} pode ser desconectada." : $"{l} removida.", "");
            }
        }
        _drives = now;

        // Discos físicos (lista abaixo dos cartões) também mudam com pendrive/HD externo.
        if (InfoCategories.ItemsSource is IEnumerable<InfoNode> cats && cats.FirstOrDefault(c => c.Header == "Armazenamento") is { } storage)
        {
            await Task.Run(() => SystemInfoCollector.RefreshDisks(storage));
            if (InfoCategories.SelectedItem == storage) { InfoDetails.ItemsSource = null; InfoDetails.ItemsSource = storage.Children; }
        }
        if (DrivesPanel.IsVisible) RefreshDrives();
    }

    private async void DiffUsb()
    {
        List<UsbDevice> list;
        try { list = await Task.Run(UsbCleaner.List); } catch { return; }
        var present = list.Where(d => d.Present).GroupBy(d => d.InstanceId)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = present.Values.Where(d => !_usbPresent.ContainsKey(d.InstanceId)).ToList();
        var removed = _usbPresent.Values.Where(d => !present.ContainsKey(d.InstanceId)).ToList();
        _usbPresent = present;
        if (PageMakerPorts.IsVisible) ScanUsb();
        if (PageMakerDrv.IsVisible) RefreshMakerDevices(); // placa plugada/removida: atualiza o diagnóstico

        // Pendrives já geram aviso de unidade: não repete com o nome genérico "armazenamento em massa".
        bool volumeJustNow = (DateTime.Now - _lastVolumeEvent).TotalSeconds < 4;
        static bool Storage(UsbDevice d) => d.Name.Contains("Mass Storage", StringComparison.OrdinalIgnoreCase) ||
                                            d.Name.Contains("Armazenamento", StringComparison.OrdinalIgnoreCase);
        // Um dispositivo composto (ex.: Arduino Leonardo) gera vários registros: anuncia o mais descritivo.
        static UsbDevice? Best(List<UsbDevice> l) =>
            l.FirstOrDefault(d => d.Port != null) ?? l.FirstOrDefault(d => d.Maker != null) ??
            l.FirstOrDefault(d => !d.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase) &&
                                  !d.Name.Contains("Hub", StringComparison.OrdinalIgnoreCase)) ?? l.FirstOrDefault();

        added = added.Where(d => !Storage(d)).ToList();
        removed = removed.Where(d => !Storage(d)).ToList();
        if (!volumeJustNow && Best(added) is { } a)
            Toast("Dispositivo USB conectado", a.Name + (a.Maker != null && !a.Name.Contains(a.Maker) ? $" · {a.Maker}" : ""), "");
        else if (!volumeJustNow && Best(removed) is { } r)
            Toast("Dispositivo USB desconectado", r.Name, "");
    }

    // ---------------- Placa de vídeo ----------------

    private void ShowGpu(GpuInfo g)
    {
        _gpu = g;
        GpuName.Text = g.Name;
        GpuSubtitle.Text = g.Subtitle;
        GpuVendor.Text = g.Vendor;
        GpuSections.ItemsSource = g.Sections;

        // Sensores do LibreHardwareMonitor deste hardware, em ordem fixa (clock, temperatura, carga...).
        var live = new ListCollectionView(_monitor.Rows)
        {
            Filter = o => ((SensorRow)o).Hardware == g.Name
        };
        live.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Order), ListSortDirection.Ascending));
        live.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Name), ListSortDirection.Ascending));
        GpuLive.ItemsSource = live;
        GpuExtras.ItemsSource = g.LiveExtras();
    }

    private void UpdateGpuLive()
    {
        // Barra lateral: GPU principal (dedicada = primeira da lista).
        if (_gpus.Count > 0)
        {
            var load = _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == SensorType.Load
                                                         && r.Name == "GPU Core");
            SideGpuTemp.Text = MainGpuTemp() is float t ? $"{t:0.#} °C" : "—";
            SideGpuLoad.Text = load?.RawValue is float l ? $"{l:0} %" : "—";
        }
        if (_gpu != null && PageGpu.IsVisible) GpuExtras.ItemsSource = _gpu.LiveExtras();
    }

    // ---------------- Jogos ----------------

    private void InitGames()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (System.Management.ManagementObject mo in s.Get()) { _cpuName = mo["Name"]?.ToString()?.Trim() ?? ""; break; }
        }
        catch { }
        _gpuName = _gpus.Count > 0 ? _gpus[0].Name : "";
        _gpuIdx = GameCatalog.GpuPerf(_gpuName) ?? 50;
        _cpuIdx = GameCatalog.CpuPerf(_cpuName) ?? 80;
        GamesSub.Text = $"FPS esperado no seu PC · {GameCatalog.ShortGpuName(_gpuName)} · {GameCatalog.ShortCpuName(_cpuName)}";

        _fpsApi.ApiKey = _settings.HowManyFpsKey;
        if (_fpsApi.Configured) ApiKeyBox.Password = _settings.HowManyFpsKey;

        _gameRows = GameCatalog.Games.Select(g => new GameRow(g)).ToList();
        _gameView = new ListCollectionView(_gameRows);
        GameList.ItemsSource = _gameView;
        RefreshGames();
    }

    private void GameOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_gameView != null) RefreshGames(); // também dispara durante InitializeComponent
    }

    private void GameSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_gameView == null) return;
        string q = GameSearch.Text.Trim();
        _gameView.Filter = q.Length == 0 ? null : o => ((GameRow)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApiKeyBox_Changed(object sender, RoutedEventArgs e)
        => ApiKeyHint.Visibility = ApiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void BtnApiKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.HowManyFpsKey = ApiKeyBox.Password;
        _settings.Save();
        _fpsApi.ApiKey = _settings.HowManyFpsKey;
        RefreshGames();
    }

    /// <summary>Recalcula a estimativa local de todos os jogos e, com chave configurada, busca os dados reais na API.</summary>
    private void RefreshGames()
    {
        string res = CheckedTag(GameResPanel) ?? "1920x1080";
        string preset = CheckedTag(GamePresetPanel) ?? "Alto";
        string up = CheckedTag(GameUpscalePanel) ?? "Native";

        foreach (var row in _gameRows)
        {
            var est = GameCatalog.Estimate(row.Game, _gpuIdx, _cpuIdx, res, preset, up);
            string limiter = est.Limiter switch
            {
                Bottleneck.Cpu => "gargalo: CPU",
                Bottleneck.Gpu => "gargalo: GPU",
                Bottleneck.Engine => $"trava do jogo em {row.Game.EngineCap:0} FPS",
                _ => ""
            };
            row.Set(est.Avg, 0, "ESTIMATIVA", limiter);
        }

        _gameFetch?.Cancel();
        if (_fpsApi.Configured)
        {
            GameSourceTitle.Text = "Fonte: HowManyFPS (dados reais)";
            GameSourceText.Text = "FPS médio e 1% low medidos em sessões reais de jogo. Respostas ficam em cache por 7 dias. " +
                                  "A API usa as configurações gráficas dela; \"Gráficos\" vale só para a estimativa.";
            BtnApiKey.Content = "ATUALIZAR CHAVE";
            _gameFetch = new CancellationTokenSource();
            _ = FetchGamesAsync(res, up, _gameFetch.Token);
        }
        else
        {
            GameSourceTitle.Text = "Fonte: estimativa local (aproximada)";
            GameSourceText.Text = "Calculada a partir do desempenho relativo da sua GPU e CPU. Para FPS medido em jogo, " +
                                  "peça uma chave da API em info@howmanyfps.com e cole ao lado.";
            BtnApiKey.Content = "SALVAR CHAVE";
        }
    }

    private async Task FetchGamesAsync(string res, string up, CancellationToken ct)
    {
        string cpu = GameCatalog.ShortCpuName(_cpuName), gpu = GameCatalog.ShortGpuName(_gpuName);
        int ok = 0;
        foreach (var row in _gameRows)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var r = await _fpsApi.GetAsync(cpu, gpu, row.Name, res, up, ct);
                row.Set(r.AvgFps, r.Min1Fps, "HOWMANYFPS", "");
                ok++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Set(row.Fps, 0, "ESTIMATIVA", $"API: {ex.Message}");
                if (ex.Message.Contains("chave") || ex.Message.Contains("cota"))
                {
                    GameSourceText.Text = $"A API recusou a consulta ({ex.Message}). Mostrando a estimativa local.";
                    return; // não adianta insistir nos outros jogos
                }
            }
        }
        if (!ct.IsCancellationRequested)
            GameSourceText.Text = $"{ok} de {_gameRows.Count} jogos com dados da HowManyFPS; os demais usam a estimativa local.";
    }

    // ---------------- Gargalo ----------------

    private void InitBottleneckAndOverlay()
    {
        _frames.Start();

        var (ratio, text) = BottleneckAnalyzer.Balance(_gpuIdx, _cpuIdx);
        _balanceRatio = ratio;
        BnCpuName.Text = $"CPU · {GameCatalog.ShortCpuName(_cpuName)}";
        BnGpuName.Text = $"GPU · {GameCatalog.ShortGpuName(_gpuName)}";
        BnBalanceText.Text = text;

        // Preferências do overlay
        foreach (var rb in OvCornerPanel.Children.OfType<RadioButton>())
            rb.IsChecked = (string)rb.Tag == _settings.OverlayCorner;
        foreach (var rb in OvScalePanel.Children.OfType<RadioButton>())
            rb.IsChecked = Math.Abs(double.Parse((string)rb.Tag, CultureInfo.InvariantCulture) - _settings.OverlayScale) < 0.01;
        BuildOverlayPicker();
        _overlayReady = true;
        UpdateOverlayStatus(null);
    }

    /// <summary>Posição do marcador: escala logarítmica da razão GPU/CPU (0,4× … 2,5×), centro = equilibrado.</summary>
    private void BnBalanceCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = BnBalanceCanvas.ActualWidth;
        double t = Math.Clamp((Math.Log(_balanceRatio) - Math.Log(0.4)) / (Math.Log(2.5) - Math.Log(0.4)), 0, 1);
        Canvas.SetLeft(BnBalanceDot, t * (w - BnBalanceDot.Width));
    }

    private static MeterItem Meter(string label, double? pct, string? suffix = null)
        => new(label, pct == null ? "—" : $"{pct:0} %{suffix}", Math.Clamp((pct ?? 0) / 100, 0, 1));

    private void UpdateBottleneck()
    {
        var frames = _frames.GetForegroundStats();
        var s = BottleneckAnalyzer.Sample(_monitor, _gpus.FirstOrDefault(), frames);
        var lim = s.Classify();

        if (PageBottleneck.IsVisible || _bnSession != null)
        {
            BnLiveTitle.Text = BottleneckAnalyzer.Title(lim);
            BnLiveAdvice.Text = BottleneckAnalyzer.Advice(lim);
            BnLiveGame.Text = frames != null
                ? $"{frames.ProcessName} · {frames.Fps:0} FPS · 1% low {frames.Low1Fps:0}"
                : "Nenhum jogo em primeiro plano (o FPS aparece quando o jogo estiver na tela)";
            bool alert = lim is Limiter.Cpu or Limiter.Vram or Limiter.Ram or Limiter.Thermal;
            BnLiveCard.Background = alert ? Fg : Card;
            BnLiveTitle.Foreground = alert ? Ink : Fg;
            BnLiveLabel.Foreground = BnLiveGame.Foreground = BnLiveAdvice.Foreground = alert ? Ink : Muted;

            BnMeters.ItemsSource = new[]
            {
                Meter("Carga da GPU", s.GpuLoad),
                Meter("Carga total da CPU", s.CpuTotal),
                Meter(s.BusiestCore != null ? $"Núcleo mais ocupado ({s.BusiestCore})" : "Núcleo mais ocupado", s.CpuMaxCore),
                Meter("VRAM em uso", s.VramPct),
                Meter("RAM em uso", s.RamPct),
                new MeterItem("Temperaturas (GPU / CPU)",
                              $"{(s.GpuTemp is float g ? $"{g:0} °C" : "—")} / {(s.CpuTemp is float c ? $"{c:0} °C" : "—")}",
                              Math.Clamp(Math.Max(s.GpuTemp ?? 0, s.CpuTemp ?? 0) / 100.0, 0, 1)),
            };
        }

        if (_bnSession != null)
        {
            _bnSession.Add(s);
            var el = DateTime.Now - _bnSession.Start;
            BnSessionStatus.Text = $"Coletando… {el:m\\:ss} de {_bnSession.Duration:m\\:ss} · {_bnSession.Count} amostras · " +
                                   (frames != null ? $"jogo detectado: {frames.ProcessName}" : "abra o jogo agora");
            if (_bnSession.Done) FinishBottleneckSession();
        }
    }

    private void BtnBnSession_Click(object sender, RoutedEventArgs e)
    {
        if (_bnSession != null)
        {
            _bnSession = null;
            BtnBnSession.Content = "INICIAR ANÁLISE";
            BtnBnSession.Style = (Style)FindResource("PrimaryButton");
            BnSessionStatus.Text = "Análise cancelada.";
            return;
        }
        int minutes = int.Parse(CheckedTag(BnDurationPanel) ?? "1");
        _bnSession = new BottleneckSession { Duration = TimeSpan.FromMinutes(minutes) };
        BnResult.Visibility = Visibility.Collapsed;
        BtnBnSession.Content = "CANCELAR";
        BtnBnSession.Style = (Style)FindResource("OutlineButton");
        BnSessionStatus.Text = "Coletando… abra o jogo agora.";
    }

    private void FinishBottleneckSession()
    {
        var sess = _bnSession!;
        _bnSession = null;
        BtnBnSession.Content = "INICIAR ANÁLISE";
        BtnBnSession.Style = (Style)FindResource("PrimaryButton");
        BnSessionStatus.Text = $"Análise concluída às {DateTime.Now:HH:mm} ({sess.Count} amostras).";

        var main = sess.Main ?? Limiter.Capped;
        BnResultTitle.Text = $"Resultado: {BottleneckAnalyzer.Title(main)}";
        var parts = new List<string>();
        if (sess.AvgFps is double fps) parts.Add($"FPS médio {fps:0}");
        if (sess.Avg(x => x.GpuLoad) is double g) parts.Add($"GPU média {g:0} %");
        if (sess.Avg(x => x.CpuTotal) is double c) parts.Add($"CPU média {c:0} %");
        if (sess.Avg(x => x.CpuMaxCore) is double k) parts.Add($"núcleo mais ocupado {k:0} %");
        if (sess.Max(x => x.VramPct) is double v) parts.Add($"VRAM máx. {v:0} %");
        if (sess.Max(x => x.GpuTemp) is double gt) parts.Add($"GPU máx. {gt:0} °C");
        BnResultSummary.Text = string.Join(" · ", parts);
        BnDistribution.ItemsSource = sess.Distribution()
            .Select(d => new MeterItem($"{BottleneckAnalyzer.Title(d.Limiter)} — % do tempo", $"{d.Pct:0} %", d.Pct / 100)).ToList();
        BnResultAdvice.Text = BottleneckAnalyzer.Advice(main);
        BnResult.Visibility = Visibility.Visible;
    }

    // ---------------- RESET BENCH ----------------

    private void InitBench()
    {
        _benchBitmap = new System.Windows.Media.Imaging.WriteableBitmap(BenchRenderer.Width, BenchRenderer.Height, 96, 96,
                                                                       PixelFormats.Bgra32, null);
        BenchImage.Source = _benchBitmap;
        UpdateBenchHistory();
        UpdateBenchLive();
    }

    private bool BenchRunning => _benchCts != null;

    private async void BtnBench_Click(object sender, RoutedEventArgs e)
    {
        if (BenchRunning) { _benchCts!.Cancel(); return; }
        if (TestRunning || _gameBenchRunning) { BenchHint.Text = "Pare o outro teste antes de rodar o benchmark."; return; }

        string mode = CheckedTag(BenchModePanel) ?? "both";
        bool sustained = CheckedTag(BenchDurationPanel) == "10";
        var minDuration = sustained ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;
        int threads = Environment.ProcessorCount;

        _benchCts = new CancellationTokenSource();
        var ct = _benchCts.Token;
        BtnBench.Content = "PARAR";
        BtnBench.Style = (Style)FindResource("OutlineButton");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = false;
        BenchHint.Text = "";
        BenchIdle.Visibility = Visibility.Collapsed;
        BenchMt.Text = BenchSt.Text = "—";
        BenchRatio.Text = "";
        _benchStart = DateTime.Now;
        _benchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => UpdateBenchView(), Dispatcher);
        _benchTimer.Start();

        double? mt = null, st = null;
        try
        {
            if (mode is "mt" or "both")
            {
                _benchPhase = $"Multi-core · {threads} threads";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(threads, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { mt = r.Score; BenchMt.Text = $"{r.Score:N0}"; }
            }
            if (!ct.IsCancellationRequested && mode is "st" or "both")
            {
                _benchPhase = "Single-core · 1 thread";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(1, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { st = r.Score; BenchSt.Text = $"{r.Score:N0}"; }
            }
        }
        catch (OperationCanceledException) { }

        _benchTimer.Stop();
        UpdateBenchView();
        bool cancelled = ct.IsCancellationRequested;
        _benchCts = null;
        BtnBench.Content = "INICIAR";
        BtnBench.Style = (Style)FindResource("PrimaryButton");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = true;
        BenchBuckets.Children.Clear();

        var total = DateTime.Now - _benchStart;
        if (cancelled)
        {
            BenchStatus.Text = "Cancelado";
            return;
        }
        BenchStatus.Text = $"Concluído em {total:m\\:ss}";
        if (mt is double m && st is double s)
            BenchRatio.Text = $"Multi ÷ single: {m / s:0.0}x com {threads} threads " +
                              $"({Environment.ProcessorCount / 2} núcleos com SMT costumam render 8–12x).";

        _settings.BenchRuns.Insert(0, new BenchRun(DateTime.Now, mt, st, sustained, GameCatalog.ShortCpuName(_cpuName)));
        AddEvent("", "RESET BENCH", "Render de CPU", mt is double mte ? $"{mte:N0} pts" : st is double ste ? $"{ste:N0} pts" : "");
        if (_settings.BenchRuns.Count > 10) _settings.BenchRuns.RemoveRange(10, _settings.BenchRuns.Count - 10);
        _settings.Save();
        UpdateBenchHistory();
    }

    /// <summary>Copia a imagem parcial e desenha o contorno dos blocos que cada thread está renderizando.</summary>
    private void UpdateBenchView()
    {
        _benchBitmap!.WritePixels(new Int32Rect(0, 0, BenchRenderer.Width, BenchRenderer.Height),
                                  _bench.Pixels, BenchRenderer.Width * 4, 0);

        BenchBuckets.Children.Clear();
        var active = _bench.Active;
        foreach (int b in active)
        {
            if (b < 0) continue;
            var (x, y, w, h) = _bench.Buckets[b];
            var rect = new Rectangle { Width = w, Height = h, Stroke = Brushes.White, StrokeThickness = 1.5 };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            BenchBuckets.Children.Add(rect);
        }

        if (BenchRunning)
        {
            double frac = _bench.BucketsTotal > 0 ? (double)_bench.BucketsDone / _bench.BucketsTotal : 0;
            BenchProgress.ScaleX = frac;
            // Tempo restante da passada atual, pela velocidade até agora (só depois de 3% para não oscilar).
            var phaseElapsed = DateTime.Now - _benchPhaseStart;
            string eta = frac >= 0.03 ? $" · faltam ~{TimeSpan.FromSeconds(phaseElapsed.TotalSeconds * (1 - frac) / frac):m\\:ss}" : "";
            BenchStatus.Text = $"{_benchPhase} · {frac:P0}{eta}";
            BenchElapsed.Text = (DateTime.Now - _benchStart).ToString(@"mm\:ss");
        }
        else BenchProgress.ScaleX = 0;
    }

    /// <summary>Temperatura, clock médio dos núcleos e consumo da CPU (atualizado junto com os sensores).</summary>
    private void UpdateBenchLive()
    {
        var clocks = _monitor.Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Clock &&
                                              r.Name.StartsWith("Core #") && r.RawValue > 0).Select(r => r.RawValue!.Value).ToList();
        BenchLive.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("CPU", GameCatalog.ShortCpuName(_cpuName)),
            new KeyValuePair<string, string>("Threads", $"{Environment.ProcessorCount}"),
            new KeyValuePair<string, string>("Temperatura", _monitor.CpuTemperature is float t ? $"{t:0.#} °C" : "—"),
            new KeyValuePair<string, string>("Clock médio", clocks.Count > 0 ? $"{clocks.Average():0} MHz" : "—"),
            new KeyValuePair<string, string>("Consumo", _monitor.CpuPower is float p ? $"{p:0.#} W" : "—"),
            new KeyValuePair<string, string>("Carga", _monitor.CpuLoad is float l ? $"{l:0} %" : "—"),
        };
    }

    private void UpdateBenchHistory()
    {
        var runs = _settings.BenchRuns;
        BenchHistoryEmpty.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        double bestMt = runs.Max(r => r.Multi) ?? 1, bestSt = runs.Max(r => r.Single) ?? 1;
        BenchHistory.ItemsSource = runs.Select(r =>
        {
            string label = $"{r.Date:dd/MM HH:mm}{(r.Sustained ? " · 10 min" : "")}";
            string value = $"{(r.Multi is double m ? $"{m:N0}" : "—")} / {(r.Single is double s ? $"{s:N0}" : "—")}";
            double frac = r.Multi is double mm ? mm / bestMt : r.Single is double ss ? ss / bestSt : 0;
            return new MeterItem(label, value, frac);
        }).ToList();
    }

    // ---------------- RESET OTIMIZA ----------------

    /// <summary>Carrega a página RESET OTIMIZA quando ela é aberta (os ajustes do Windows só na primeira vez).</summary>
    private void InitOptimize()
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
                                         : string.Join("\n", toClose.Select(a => $"  • {a.Title}"));
        var r = MessageBox.Show("O RESET OTIMIZA vai:\n\n" +
                                "1. Limpar a memória em espera e enxugar a RAM dos programas.\n" +
                                $"2. Fechar os programas marcados:\n{list}\n\n" +
                                "Salve o que estiver aberto nesses programas antes. Continuar?",
                                "RESET OTIMIZA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        BtnOptimize.IsEnabled = false;
        BtnOptimize.Content = "OTIMIZANDO…";
        OptResult.Text = "";
        var closeResult = await Task.Run(() => Optimizer.CloseApps(toClose));
        var mem = await Task.Run(Optimizer.CleanMemory); // depois de fechar, para contar a memória liberada por eles também
        BtnOptimize.IsEnabled = true;
        BtnOptimize.Content = "RESET OTIMIZA";

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
            _settings.Save();
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

    // ----- USB e portas COM -----

    private List<UsbDevice> _usb = new();

    private void ScanUsb()
    {
        try
        {
            _usb = UsbCleaner.List();
        }
        catch (Exception ex) { UsbResult.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; return; }

        static bool Relevant(UsbDevice d) => d.Port != null || d.Maker != null;
        var connected = _usb.Where(d => d.Present && Relevant(d)).ToList();
        UsbConnected.Text = connected.Count == 0 ? "Nenhuma placa ou porta serial conectada."
                          : string.Join("\n", connected.Select(d => $"{d.Port ?? "—"} · {d.Name}"));

        var shown = _usb.Where(d => !d.Present && (UsbShowAll.IsChecked == true || Relevant(d))).ToList();
        UsbList.ItemsSource = shown;
        int ghosts = _usb.Count(d => !d.Present);
        UsbSummary.Text = shown.Count == 0
            ? "Nenhum registro antigo encontrado."
            : $"{shown.Count} registros antigos na lista ({ghosts} dispositivos ausentes no total).";

        try
        {
            var (reserved, inUse) = UsbCleaner.ComStatus();
            string R(IEnumerable<int> l) => l.Any() ? string.Join(", ", l.Select(n => $"COM{n}")) : "nenhum";
            UsbComDb.Text = $"{reserved.Count} reservados: {R(reserved)}\nEm uso agora: {R(inUse)}";
        }
        catch (Exception ex) { UsbComDb.Text = ex.Message; }
        BtnComDbRestore.IsEnabled = _settings.ComDbBackup != null;
    }

    private void BtnUsbScan_Click(object sender, RoutedEventArgs e) => ScanUsb();
    private void UsbShowAll_Changed(object sender, RoutedEventArgs e) { if (UsbList != null) ScanUsb(); }

    private bool RequireAdmin()
    {
        if (SensorMonitor.IsElevated) return true;
        MessageBox.Show("Esta ação precisa do RESET EXPLODE aberto como administrador.", "RESET EXPLODE",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async void BtnUsbRemove_Click(object sender, RoutedEventArgs e)
    {
        var sel = _usb.Where(d => d.Selected && !d.Present).ToList();
        if (sel.Count == 0) { UsbResult.Text = "Nenhum registro selecionado."; return; }
        if (!RequireAdmin()) return;
        string names = string.Join("\n", sel.Take(12).Select(d => $"  • {d.Name}{(d.Port != null ? "" : $" ({d.Class})")}"))
                       + (sel.Count > 12 ? $"\n  … e mais {sel.Count - 12}" : "");
        var r = MessageBox.Show($"Remover {sel.Count} registros de dispositivos que não estão conectados?\n\n{names}\n\n" +
                                "Se plugar algum deles de novo, o Windows reinstala automaticamente.",
                                "RESET MAKER", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        BtnUsbRemove.IsEnabled = false;
        UsbResult.Text = "Removendo…";
        var (removed, failed) = await Task.Run(() => UsbCleaner.Remove(sel.Select(d => d.InstanceId)));
        BtnUsbRemove.IsEnabled = true;
        UsbResult.Text = $"{removed} registros removidos." +
                         (failed.Count > 0 ? $" Não removidos: {string.Join("; ", failed.Take(5))}" : "") +
                         " Para liberar os números COM que eles prendiam, use \"Liberar números COM presos\".";
        ScanUsb();
    }

    private void BtnComDb_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var (reserved, inUse) = UsbCleaner.ComStatus();
        var freed = reserved.Except(inUse).ToList();
        if (freed.Count == 0) { UsbResult.Text = "Nenhum número COM preso: todos os reservados estão em uso."; return; }
        var r = MessageBox.Show($"Liberar {freed.Count} números COM presos por dispositivos que não estão conectados?\n\n" +
                                $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}\n" +
                                $"Mantidos (em uso): {(inUse.Count > 0 ? string.Join(", ", inUse.Select(n => $"COM{n}")) : "nenhum")}\n\n" +
                                "A próxima placa plugada vai receber um número baixo (COM3, COM4…). " +
                                "O valor atual fica guardado para \"Desfazer liberação\".",
                                "RESET MAKER", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            var old = UsbCleaner.RebuildComDb();
            _settings.ComDbBackup ??= old; // guarda o original da primeira vez
            _settings.Save();
            UsbResult.Text = $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}. Plugue a placa de novo para ela pegar um número baixo.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        ScanUsb();
    }

    private void BtnComDbRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ComDbBackup == null || !RequireAdmin()) return;
        try
        {
            UsbCleaner.RestoreComDb(_settings.ComDbBackup);
            _settings.ComDbBackup = null;
            _settings.Save();
            UsbResult.Text = "Reservas de números COM restauradas para como estavam.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        ScanUsb();
    }

    private void BtnRestoreTweaks_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.TweakBackup.Count == 0)
        {
            MessageBox.Show("Nenhum ajuste foi alterado pelo RESET EXPLODE ainda.", "RESET OTIMIZA",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = MessageBox.Show("Voltar todos os ajustes do Windows para como estavam antes do RESET EXPLODE alterá-los?",
                                "RESET OTIMIZA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        bool restart = _tweaks.Any(t => t.NeedsRestart && _settings.TweakBackup.ContainsKey(t.Id));
        WindowsTweaks.RestoreAll(_tweaks, _settings);
        foreach (var t in _tweaks) { t.Refresh(); t.Status = "Restaurado."; }
        if (restart) MessageBox.Show("Alguns ajustes só voltam ao original depois de reiniciar o PC.", "RESET OTIMIZA");
    }

    // ---------------- RESET BENCH: benchmarks de jogo ----------------

    public sealed record MemRow(string Label, string Value, string Hint);
    private bool _gameBenchRunning;
    private GpuGameBench? _gpuBench;
    private bool _gpuBenchActive;
    private CancellationTokenSource? _gameBenchCts;
    private System.Windows.Media.Imaging.WriteableBitmap? _simBitmap;
    private int[] _simPixels = Array.Empty<int>();
    private const int SimView = 440;

    private bool AnyBenchRunning => BenchRunning || _gameBenchRunning;

    private void BenchTest_Checked(object sender, RoutedEventArgs e)
    {
        if (BenchPanelRender == null) return; // durante InitializeComponent
        string test = (string)((FrameworkElement)sender).Tag;
        BenchPanelRender.Visibility = test == "render" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelGpu.Visibility = test == "gpu" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelRt.Visibility = test == "rt" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelSim.Visibility = test == "sim" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelMem.Visibility = test == "mem" ? Visibility.Visible : Visibility.Collapsed;
        BenchPanelDisk.Visibility = test == "disk" ? Visibility.Visible : Visibility.Collapsed;
        if (test == "disk") InitDiskBench();
        BenchSub.Text = test switch
        {
            "gpu" => "Cena de jogo em Direct3D 11 para medir a placa de vídeo, no estilo 3DMark / Unigine",
            "rt" => "Ray tracing por hardware (DXR 1.1, Direct3D 12): usa os núcleos de RT da placa, com 3 passadas e nota pela mediana",
            "sim" => "Quanto FPS a CPU aguenta numa simulação de jogo com milhares de unidades",
            "mem" => "Latência e banda da RAM e velocidade de carregamento do disco",
            "disk" => "Velocidade de leitura e escrita do disco: sequencial e acessos aleatórios de 4 KB, no estilo CrystalDiskMark",
            _ => "Benchmark de CPU por renderização 3D (path tracing), no mesmo princípio do Cinebench",
        };
        UpdateGameBenchHistory();
    }

    private void SaveBenchRecord(string test, double score, string summary)
    {
        AddEvent("", "Benchmark concluído", summary, "");
        _settings.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, test, score, summary));
        // Mantém os 10 mais recentes de cada teste.
        _settings.BenchRecords = _settings.BenchRecords.GroupBy(r => r.Test).SelectMany(g => g.Take(10))
                                          .OrderByDescending(r => r.Date).ToList();
        _settings.Save();
        UpdateGameBenchHistory();
    }

    private void UpdateGameBenchHistory()
    {
        List<MeterItem> For(string test)
        {
            var list = _settings.BenchRecords.Where(r => r.Test == test).ToList();
            double best = list.Count > 0 ? list.Max(r => r.Score) : 1;
            return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best > 0 ? r.Score / best : 0)).ToList();
        }
        GbHistory.ItemsSource = For("gpu");
        RtHistory.ItemsSource = RtHistoryItems();
        SimHistory.ItemsSource = For("sim");
        MemHistory.ItemsSource = For("mem");
        DiskHistory.ItemsSource = For("disk");
    }

    private void SetGameBenchRunning(Button btn, bool running)
    {
        _gameBenchRunning = running;
        btn.Content = running ? "CANCELAR" : "INICIAR";
        btn.Style = (Style)FindResource(running ? "OutlineButton" : "PrimaryButton");
        BenchTestPanel.IsEnabled = !running;
    }

    // ----- Cena de jogo (GPU) -----

    private async void BtnGpuBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gpuBench?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { GbStatus.Text = "Termine o outro teste antes."; return; }

        var size = (CheckedTag(GbResPanel) ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int secs = int.Parse(CheckedTag(GbDurPanel) ?? "60");
        _gpuBench = new GpuGameBench
        {
            Width = size[0], Height = size[1], Duration = TimeSpan.FromSeconds(secs),
            AdapterName = _gpus.Count > 0 ? _gpus[0].Name : ""
        };
        SetGameBenchRunning(BtnGpuBench, true);
        GbResult.ItemsSource = null;
        var bench = _gpuBench;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            GbFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            GbProgress.ScaleX = bench.Progress;
            GbStatus.Text = $"Rodando · {size[0]}x{size[1]} · {bench.Progress:P0}";
        }, Dispatcher);
        timer.Start();
        _gpuBenchActive = true;
        GbGpuNow.Text = "sensores pausados";
        bool overlayWasOn = ChkOverlay.IsChecked == true;
        if (overlayWasOn) ChkOverlay.IsChecked = false; // o overlay também consulta a GPU

        // Thread dedicada: a cena roda num loop próprio com prioridade alta.
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        if (overlayWasOn) ChkOverlay.IsChecked = true;
        GbGpuNow.Text = "—";
        SetGameBenchRunning(BtnGpuBench, false);
        GbProgress.ScaleX = 0;
        GbFpsNow.Text = "—";

        if (r == null)
        {
            GbStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        GbStatus.Text = $"Concluído · {r.Adapter}";
        GbResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Nota", $"{r.Score:N0} pts"),
            new KeyValuePair<string, string>("FPS médio", $"{r.AvgFps:0.0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0.0}"),
            new KeyValuePair<string, string>("Resolução", $"{r.Width}x{r.Height}"),
            new KeyValuePair<string, string>("Frames", $"{r.Frames:N0}"),
        };
        SaveBenchRecord("gpu", r.Score, $"{r.Score:N0} pts · {r.AvgFps:0} FPS · {r.Height}p");
    }

    // ----- Simulação de jogo (CPU) -----

    private async void BtnSimBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { SimStatus.Text = "Termine o outro teste antes."; return; }

        int secs = int.Parse(CheckedTag(SimDurPanel) ?? "20");
        _simBitmap ??= new System.Windows.Media.Imaging.WriteableBitmap(SimView, SimView, 96, 96, PixelFormats.Bgra32, null);
        if (_simPixels.Length == 0) _simPixels = new int[SimView * SimView];
        SimImage.Source = _simBitmap;
        SimIdle.Visibility = Visibility.Collapsed;

        var sim = new GameSimBench();
        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnSimBench, true);
        SimResult.ItemsSource = null;
        SimStatus.Text = "Aquecendo (2 s)…";
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) =>
        {
            DrawSim(sim);
            SimProgress.ScaleX = sim.Progress;
            if (sim.FramesDone > 0) SimStatus.Text = $"Simulando · {sim.Progress:P0}";
            SimFpsNow.Text = sim.CurrentFps > 0 ? $"{sim.CurrentFps:0} FPS" : "";
        }, Dispatcher);
        timer.Start();

        var r = await Task.Run(() => sim.Run(TimeSpan.FromSeconds(secs), ct));
        timer.Stop();
        bool cancelled = ct.IsCancellationRequested;
        SetGameBenchRunning(BtnSimBench, false);
        SimProgress.ScaleX = 0;
        SimFpsNow.Text = "";
        if (cancelled) { SimStatus.Text = "Cancelado"; return; }

        SimStatus.Text = $"Concluído · {r.Frames:N0} frames em {r.Seconds:0} s";
        SimResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("FPS médio (nota)", $"{r.AvgFps:0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0}"),
            new KeyValuePair<string, string>("Thread principal", $"{r.MainThreadMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Física + IA (paralelo)", $"{r.ParallelMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Unidades", $"{GameSimBench.Entities:N0}"),
        };
        SaveBenchRecord("sim", r.Score, $"{r.AvgFps:0} FPS · 1% low {r.Low1Fps:0}");
    }

    /// <summary>Vista de cima da batalha: time A em branco, time B em cinza.</summary>
    private void DrawSim(GameSimBench sim)
    {
        Array.Fill(_simPixels, unchecked((int)0xFF050505));
        float[] xs = sim.ViewX, ys = sim.ViewY; byte[] team = sim.ViewTeam;
        float k = SimView / GameSimBench.World;
        for (int i = 0; i < GameSimBench.Entities; i++)
        {
            int px = (int)(xs[i] * k), py = (int)(ys[i] * k);
            if ((uint)px < SimView && (uint)py < SimView)
                _simPixels[py * SimView + px] = team[i] == 0 ? unchecked((int)0xFFF5F5F5) : unchecked((int)0xFF5A5A5A);
        }
        _simBitmap!.WritePixels(new Int32Rect(0, 0, SimView, SimView), _simPixels, SimView * 4, 0);
    }

    // ----- Memória e carregamento -----

    private async void BtnMemBench_Click(object sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { MemStatus.Text = "Termine o outro teste antes."; return; }

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnMemBench, true);
        var rows = new List<MemRow>();
        void Show() => MemResult.ItemsSource = rows.ToList();
        (string Phase, double Frac) progress = ("", 0);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) =>
        {
            MemStatus.Text = progress.Phase;
            MemProgress.ScaleX = progress.Frac;
        }, Dispatcher);
        timer.Start();

        try
        {
            progress = ("Medindo latência da RAM…", 0.05);
            double lat = await Task.Run(() => MemoryBench.MeasureLatencyNs(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Latência da RAM", $"{lat:0.0} ns", "Acesso aleatório. Menor é melhor; pesa muito em jogos competitivos."));
            Show();

            progress = ("Medindo banda da RAM…", 0.2);
            var bw = await Task.Run(() => MemoryBench.MeasureBandwidth(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Banda de leitura da RAM", $"{bw.Read:0.0} GB/s", "Todas as threads lendo em sequência."));
            rows.Add(new MemRow("Banda de cópia da RAM", $"{bw.Copy:0.0} GB/s", "Leitura + escrita."));
            Show();

            var load = await Task.Run(() => MemoryBench.MeasureLoading((phase, f) => progress = (phase, 0.35 + f * 0.6), ct));
            rows.Add(new MemRow($"SSD {load.Drive} · leitura sequencial", $"{load.SeqReadMBs:N0} MB/s", "Pacote de 512 MB, sem cache do Windows."));
            rows.Add(new MemRow("Assets pequenos", $"{load.FilesPerSec:N0} arq/s",
                                $"Mil arquivos de 64 KB com 8 threads ({load.SmallFilesMBs:N0} MB/s), como engines modernas."));
            rows.Add(new MemRow("Carregamento do mapa", $"{load.MapLoadSeconds:0.00} s", "Pacote + assets: quanto menor, mais rápido o jogo carrega."));
            Show();

            MemStatus.Text = "Concluído · arquivos de teste apagados";
            // Nota para a barra do histórico: mais rápido = maior (inverso da latência × velocidade de carga).
            SaveBenchRecord("mem", 1000 / lat + 10 / load.MapLoadSeconds,
                            $"{lat:0} ns · {bw.Read:0} GB/s · mapa {load.MapLoadSeconds:0.00} s");
        }
        catch (OperationCanceledException) { MemStatus.Text = "Cancelado (arquivos de teste apagados)"; }
        catch (Exception ex) { MemStatus.Text = $"Falhou: {ex.Message}"; }
        finally
        {
            timer.Stop();
            MemProgress.ScaleX = 0;
            SetGameBenchRunning(BtnMemBench, false);
        }
    }

    // ---------------- Overlay ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyHook);
        // Ctrl + Shift + O, sem repetição ao segurar
        RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0004 | 0x4000, 0x4F);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) // WM_HOTKEY
        {
            ChkOverlay.IsChecked = ChkOverlay.IsChecked != true;
            handled = true;
        }
        else if (msg == 0x0219) // WM_DEVICECHANGE
            OnDeviceChange(wParam.ToInt32(), lParam);
        return IntPtr.Zero;
    }

    private void ChkOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkOverlay.IsChecked == true)
        {
            _overlay ??= new OverlayWindow();
            ApplyOverlayOptions();
            _overlay.Show();
            _overlayTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal,
                                                  (_, _) => UpdateOverlay(), Dispatcher);
            _overlayTimer.Start();
            UpdateOverlay();
        }
        else
        {
            _overlayTimer?.Stop();
            _overlay?.Hide();
        }
    }

    private void OvOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_overlayReady) return;
        _settings.OverlayCorner = CheckedTag(OvCornerPanel) ?? "TL";
        _settings.OverlayScale = double.Parse(CheckedTag(OvScalePanel) ?? "1", CultureInfo.InvariantCulture);
        _settings.Save();
        ApplyOverlayOptions();
    }

    private void ApplyOverlayOptions()
    {
        if (_overlay == null) return;
        _overlay.Corner = _settings.OverlayCorner;
        _overlay.SetScale(_settings.OverlayScale);
        _overlay.Reposition();
    }

    private void UpdateOverlay()
    {
        var f = _frames.GetForegroundStats();
        if (_overlay != null && _overlay.IsVisible)
        {
            _overlay.Update(f, BuildOverlayLines(f), OvShow, OvShow.Contains("clock") ? DateTime.Now.ToString("HH:mm") : null);
            _overlay.KeepOnTop(); // o jogo pode ter passado na frente (janela sem borda "sempre no topo")
        }
        UpdateOverlayStatus(f);
    }


    private void UpdateOverlayStatus(FrameStats? f)
    {
        if (_frames.Error != null) OvStatus.Text = $"Detecção de FPS indisponível: {_frames.Error}";
        else if (f != null) OvStatus.Text = $"Ativa · {f.ProcessName}: {f.Fps:0} FPS ({f.FrameTimeMs:0.0} ms) · 1% low {f.Low1Fps:0}";
        else OvStatus.Text = "Ativa · aguardando um jogo em primeiro plano";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------------- Sensores ----------------

    private void AddChip(string label, string? category, bool isChecked = false)
    {
        var chip = new RadioButton
        {
            Content = label,
            GroupName = "Category",
            Style = (Style)FindResource("Chip"),
            IsChecked = isChecked
        };
        chip.Checked += (_, _) => { _category = category; ApplySensorFilter(); };
        CategoryChips.Children.Add(chip);
    }

    private void SensorSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _search = SensorSearch.Text.Trim();
        ApplySensorFilter();
    }

    private void ApplySensorFilter()
    {
        if (_view == null) return;
        if (_category == null && _search.Length == 0) { _view.Filter = null; return; }
        _view.Filter = o =>
        {
            var r = (SensorRow)o;
            return (_category == null || r.Category == _category)
                && (_search.Length == 0
                    || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                    || r.Hardware.Contains(_search, StringComparison.OrdinalIgnoreCase));
        };
    }

    // ---------------- Stress test ----------------

    private bool TestRunning => _stress.Running || _gpuBurnOn;

    private void BtnStress_Click(object sender, RoutedEventArgs e)
    {
        if (TestRunning) StopStress("Parado pelo usuário");
        else StartStress();
    }

    private void ChkRam_Click(object sender, RoutedEventArgs e)
    {
        if (RamFractionPanel != null) RamFractionPanel.IsEnabled = ChkRam.IsChecked == true;
    }

    private void ChkGpu_Click(object sender, RoutedEventArgs e)
    {
        if (GpuOptions != null) GpuOptions.IsEnabled = GpuTempLimit.IsEnabled = ChkGpu.IsChecked == true;
        if (PageStress != null) DrawChart(); // mostra/oculta a linha de limite da GPU
    }

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void StartStress()
    {
        StressHint.Text = "";
        bool cpu = ChkCpu.IsChecked == true, ram = ChkRam.IsChecked == true, gpu = ChkGpu.IsChecked == true;
        if (!cpu && !ram && !gpu) { StressHint.Text = "Ative CPU, RAM ou GPU."; return; }

        if (!double.TryParse(TempLimit.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _tempLimit)
            || _tempLimit < 40 || _tempLimit > 110)
        {
            StressHint.Text = "Limite da CPU deve estar entre 40 e 110 °C.";
            return;
        }
        if (gpu && (!double.TryParse(GpuTempLimit.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _gpuTempLimit)
                    || _gpuTempLimit < 40 || _gpuTempLimit > 100))
        {
            StressHint.Text = "Limite da GPU deve estar entre 40 e 100 °C.";
            return;
        }
        if (!double.TryParse(TxtDuration.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double minutes) || minutes < 0)
        {
            StressHint.Text = "Duração inválida.";
            return;
        }
        if ((cpu || ram) && _monitor.CpuTemperature == null)
        {
            string why = SensorMonitor.IsPawnIOInstalled
                ? ""
                : "\n\nCausa provável: o driver PawnIO não está instalado. Instale com:\n" +
                  "winget install namazso.PawnIO\ne reabra o RESET EXPLODE.";
            var r = MessageBox.Show("Nenhum sensor de temperatura da CPU foi encontrado, então o corte automático " +
                                    "por temperatura da CPU NÃO funcionará. Continuar mesmo assim?" + why,
                                    "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        if (gpu && MainGpuTemp() == null)
        {
            var r = MessageBox.Show("Nenhum sensor de temperatura da GPU foi encontrado, então o corte automático " +
                                    "por temperatura da GPU NÃO funcionará. Continuar mesmo assim?",
                                    "RESET EXPLODE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        double fraction = double.Parse(CheckedTag(RamFractionPanel) ?? "0.6", CultureInfo.InvariantCulture);

        _stressDuration = TimeSpan.FromMinutes(minutes);
        _stressStart = DateTime.Now;
        _stressMaxTemp = 0;
        _gpuMaxTemp = 0;
        _tempHistory.Clear();
        _gpuTempHistory.Clear();

        if (cpu || ram) _stress.Start(cpu, ram, fraction);

        string gpuDesc = "não";
        if (gpu)
        {
            string res = CheckedTag(GpuResPanel) ?? "1920x1080";
            int msaa = int.Parse(CheckedTag(GpuMsaaPanel) ?? "4");
            var intensity = (CheckedTag(GpuIntensityPanel) ?? "128,48").Split(',').Select(int.Parse).ToArray();
            bool full = res == "full";
            var size = full ? new[] { 0, 0 } : res.Split('x').Select(int.Parse).ToArray();
            _burn.Start(new GpuBurnSettings
            {
                Fullscreen = full, Width = size[0], Height = size[1], Msaa = msaa,
                Shells = intensity[0], Iterations = intensity[1],
                AdapterName = _gpus.Count > 0 ? _gpus[0].Name : ""
            });
            _gpuBurnOn = true;
            gpuDesc = $"sim ({(full ? "tela cheia" : res)}, {(msaa > 1 ? $"MSAA {msaa}x" : "sem MSAA")}, " +
                      $"{GpuIntensityPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Content}, limite {_gpuTempLimit:0} °C)";
        }

        SetStressControls(running: true);
        SetStatus("Rodando", alert: false);
        Log($"Iniciado · CPU {(cpu ? $"sim (limite {_tempLimit:0} °C)" : "não")} · RAM {(ram ? $"sim ({fraction:P0} da livre)" : "não")} · " +
            $"GPU {gpuDesc} · {(minutes > 0 ? $"{minutes:0.##} min" : "sem limite de tempo")}");
    }

    private void StopStress(string reason)
    {
        if (TestRunning) AddEvent("", "Stress test", reason, reason.StartsWith("ABORTADO") ? "abortado" : "ok");
        if (!TestRunning) return;
        var errors = _stress.MemoryErrors;
        var passes = _stress.MemoryPasses;
        bool hadRam = _stress.MemoryAllocatedBytes > 0;
        bool hadCpu = _stress.Running;
        _stress.Stop();

        string gpuSummary = "";
        if (_gpuBurnOn)
        {
            _burn.Stop();
            _gpuBurnOn = false;
            // "Pontuação" no estilo FurMark: frames renderizados e FPS médio.
            gpuSummary = $" · GPU: {_burn.Frames:N0} frames, média {_burn.AverageFps:0} FPS, temp. máx. {_gpuMaxTemp:0.#} °C";
        }
        SetStressControls(running: false);

        bool aborted = reason.StartsWith("ABORTADO");
        SetStatus(aborted ? "Abortado" : "Parado", alert: aborted);
        Log($"{reason} · duração {DateTime.Now - _stressStart:hh\\:mm\\:ss}" +
            (hadCpu ? $" · CPU temp. máx. {_stressMaxTemp:0.#} °C" : "") +
            (hadRam ? $" · RAM {passes} passes, {errors} erros" : "") + gpuSummary);
    }

    private void SetStressControls(bool running)
    {
        BtnStress.Content = running ? "PARAR" : "INICIAR";
        BtnStress.Style = (Style)FindResource(running ? "OutlineButton" : "PrimaryButton");
        ChkCpu.IsEnabled = ChkRam.IsEnabled = ChkGpu.IsEnabled = !running;
        RamFractionPanel.IsEnabled = !running && ChkRam.IsChecked == true;
        GpuOptions.IsEnabled = GpuTempLimit.IsEnabled = !running && ChkGpu.IsChecked == true;
        TempLimit.IsEnabled = TxtDuration.IsEnabled = !running;
        SideStress.Text = running ? "rodando" : "parado";
    }

    /// <summary>Alerta = cartão invertido (branco com texto preto).</summary>
    private void SetStatus(string text, bool alert)
    {
        StStatus.Text = text;
        StatusCard.Background = alert ? Fg : Card;
        StStatus.Foreground = alert ? Ink : Fg;
        StStatusLabel.Foreground = alert ? Ink : Muted;
    }

    /// <summary>Temperatura do núcleo da GPU principal (dedicada).</summary>
    private float? MainGpuTemp()
    {
        if (_gpus.Count == 0) return null;
        var temps = _monitor.Rows.Where(r => r.Hardware == _gpus[0].Name && r.Type == SensorType.Temperature).ToList();
        return (temps.FirstOrDefault(r => r.Name == "GPU Core") ?? temps.FirstOrDefault())?.RawValue;
    }

    /// <summary>Consumo da GPU principal: NVML quando disponível, senão LibreHardwareMonitor.</summary>
    private float? MainGpuPower()
    {
        if (_gpus.Count == 0) return null;
        var g = _gpus[0];
        if (g.NvmlHandle != IntPtr.Zero && Nvml.PowerUsageMw(g.NvmlHandle) is uint mw) return mw / 1000f;
        return _monitor.Rows.FirstOrDefault(r => r.Hardware == g.Name && r.Type == SensorType.Power)?.RawValue;
    }

    private string MainGpuPerfCap()
    {
        if (_gpus.Count == 0) return "";
        return _gpus[0].LiveExtras().FirstOrDefault(kv => kv.Key.StartsWith("PerfCap")).Value ?? "";
    }

    private void UpdateStressPanel()
    {
        float? temp = _monitor.CpuTemperature;
        float? load = _monitor.CpuLoad;
        float? power = _monitor.CpuPower;
        float? gTemp = MainGpuTemp();
        float? gPower = MainGpuPower();

        if (temp is float t)
        {
            _tempHistory.Add(t);
            if (_tempHistory.Count > HistoryLength) _tempHistory.RemoveAt(0);
            if (_stress.Running && t > _stressMaxTemp) _stressMaxTemp = t;
        }
        if (gTemp is float gt)
        {
            _gpuTempHistory.Add(gt);
            if (_gpuTempHistory.Count > HistoryLength) _gpuTempHistory.RemoveAt(0);
            if (_gpuBurnOn && gt > _gpuMaxTemp) _gpuMaxTemp = gt;
        }

        StTemp.Text = SideTemp.Text = temp == null ? "—" : $"{temp:0.#} °C";
        StTempMax.Text = _stressMaxTemp > 0 ? $"máx. {_stressMaxTemp:0.#} °C" : "máx. —";
        SideLoad.Text = load == null ? "—" : $"{load:0} %";
        StLoad.Text = load == null ? "carga —" : $"carga {load:0} %";
        StPower.Text = power == null ? "—" : $"{power:0.#} W";

        StGpuTemp.Text = gTemp == null ? "—" : $"{gTemp:0.#} °C";
        StGpuTempMax.Text = _gpuMaxTemp > 0 ? $"máx. {_gpuMaxTemp:0.#} °C" : "máx. —";
        StGpuPower.Text = gPower == null ? "—" : $"{gPower:0.#} W";
        string cap = MainGpuPerfCap();
        StGpuCap.Text = cap.Length > 0 ? $"limitador: {cap}" : "—";
        StFps.Text = _gpuBurnOn && _burn.Fps > 0 ? $"{_burn.Fps:0}" : "—";
        StFpsAvg.Text = _gpuBurnOn && _burn.AverageFps > 0 ? $"média {_burn.AverageFps:0}" : "média —";

        if (TestRunning)
        {
            var elapsed = DateTime.Now - _stressStart;
            StElapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
            StRam.Text = _stress.MemoryAllocatedBytes > 0
                ? $"{_stress.MemoryPasses} / {_stress.MemoryErrors}"
                : "—";

            if (_stress.Running && temp >= _tempLimit)
                StopStress($"ABORTADO: CPU a {temp:0.#} °C atingiu o limite de {_tempLimit:0} °C");
            else if (_gpuBurnOn && gTemp >= _gpuTempLimit)
                StopStress($"ABORTADO: GPU a {gTemp:0.#} °C atingiu o limite de {_gpuTempLimit:0} °C");
            else if (_stress.MemoryErrors > 0)
                StopStress($"ABORTADO: {_stress.MemoryErrors} erros de memória detectados");
            else if (_gpuBurnOn && !_burn.Running)
                StopStress(_burn.Error != null ? $"ABORTADO: falha na GPU ({_burn.Error})"
                                               : "Janela da GPU fechada pelo usuário");
            else if (_stressDuration > TimeSpan.Zero && elapsed >= _stressDuration)
                StopStress("Concluído (tempo esgotado)");
        }

        if (PageStress.IsVisible) DrawChart();
    }

    private void Log(string msg)
    {
        StressLog.Items.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
        StressLog.ScrollIntoView(StressLog.Items[^1]);
    }

    private void TempChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>Desenha o histórico de temperatura da CPU (branco) e da GPU (cinza) com as linhas de limite.</summary>
    private void DrawChart()
    {
        TempChart.Children.Clear();
        double w = TempChart.ActualWidth, h = TempChart.ActualHeight;
        if (w < 50 || h < 40) return;

        const double left = 36, top = 6, bottom = 6;
        double plotW = w - left, plotH = h - top - bottom;
        double yMin = 20, yMax = Math.Max(110, Math.Max(_tempLimit, _gpuTempLimit) + 10);
        double Y(double v) => top + plotH * (1 - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));

        var gridBrush = (Brush)FindResource("Line");
        for (double v = yMin; v <= yMax; v += 20)
        {
            TempChart.Children.Add(new Line { X1 = left, X2 = w, Y1 = Y(v), Y2 = Y(v), Stroke = gridBrush });
            var lbl = new TextBlock { Text = $"{v:0}°", FontSize = 10, Foreground = (Brush)FindResource("Faint"),
                                      FontFamily = (FontFamily)FindResource("Mono") };
            Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, Y(v) - 7);
            TempChart.Children.Add(lbl);
        }

        void LimitLine(double v, Brush b) => TempChart.Children.Add(new Line
        {
            X1 = left, X2 = w, Y1 = Y(v), Y2 = Y(v),
            Stroke = b, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
        });
        LimitLine(_tempLimit, Muted);
        if (ChkGpu.IsChecked == true) LimitLine(_gpuTempLimit, (Brush)FindResource("Faint"));

        double step = plotW / (HistoryLength - 1);
        void Series(List<double> data, Brush stroke, bool fill)
        {
            if (data.Count < 2) return;
            int offset = HistoryLength - data.Count; // alinha à direita
            var line = new Polyline { Stroke = stroke, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < data.Count; i++)
                line.Points.Add(new Point(left + (offset + i) * step, Y(data[i])));
            if (fill)
            {
                var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x16, ((SolidColorBrush)FindResource("Fg")).Color.R, ((SolidColorBrush)FindResource("Fg")).Color.G, ((SolidColorBrush)FindResource("Fg")).Color.B)) };
                foreach (var p in line.Points) area.Points.Add(p);
                area.Points.Add(new Point(line.Points[^1].X, top + plotH));
                area.Points.Add(new Point(line.Points[0].X, top + plotH));
                TempChart.Children.Add(area);
            }
            TempChart.Children.Add(line);
        }
        Series(_gpuTempHistory, Muted, fill: false);
        Series(_tempHistory, Fg, fill: true);
    }
}
