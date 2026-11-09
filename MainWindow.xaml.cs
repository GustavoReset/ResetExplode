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

namespace FortKnox;

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
    private LiveReadings _live = null!; // leituras de GPU/CPU compartilhadas com os módulos (Modules/Shared)
    private const int HotkeyId = 0x5245; // "RE"


    // ARSENAL · OTIMIZAÇÃO
    private List<BackgroundApp> _apps = new();
    private readonly List<Tweak> _tweaks = WindowsTweaks.All(); // só as definições; o estado é lido ao abrir a página
    private bool _optInit;

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
        _live = new LiveReadings(_monitor, () => _gpus.FirstOrDefault());
        _view = CollectionViewSource.GetDefaultView(_monitor.Rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Hardware)));
        SensorGrid.ItemsSource = _view;
        SystemSub.Text = "Lendo informações do sistema…";
        StatusText.Text = "Detectando o hardware…";

        RegisterModules();
        SnapshotUsb();
        RefreshDrives();
        TitlePage.Text = "Painel";
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
            PageStress.Tick();
            UpdateSideSensors();
            UpdateGpuLive();
            UpdateCpuSensorWarning();
            CollectDashSample();
            UpdateBottleneck();
            PageBench.Tick();
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
            _timer.Stop();
            _toast?.Close();
            _frames.Dispose();
            UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, HotkeyId);
            ShutdownModules(); // COFRE, PRENSA e RADAR (Fase 3.2)
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
            PageOc.Init();
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
            MessageBox.Show(why, "Fort Knox", MessageBoxButton.OK, MessageBoxImage.Information);
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
                            "Fort Knox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var r = MessageBox.Show(why + "\n\nInstalar o PawnIO agora? Vai abrir uma janela com o instalador do Windows (winget). " +
                                "Quando terminar, feche o Fort Knox e abra de novo.",
                                "Fort Knox", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/k winget install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements" +
            " && echo. && echo Pronto! Feche esta janela e reabra o Fort Knox.")
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
        NavigateModules(page); // módulos do núcleo: o COFRE sai da memória, o RADAR para o ETW etc.

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
            case "PageOptimize": InitOptimize(); break;
        }
    }

    private void ShowPage(string name)
    {
        foreach (var p in new FrameworkElement[] { PageDash, PageSystem, PageSensors, PageGpu, PageGames, PageBottleneck, PageOverlay, PageBench, PageOptimize, PageOc, PageInteg, PageLauncher, PageDriver, PageStartup, PageApps, PageMakerPorts, PageMakerDrv, PageDisks, PageEnergy, PageNet, PageStress, PageVault, PagePdf })
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
        if (added.Count + removed.Count > 0) App.Core?.Bus.Publish(new UsbDevicesChanged(added.Count, removed.Count)); // OFICINA atualiza a página aberta

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

        // VIGIA: o overlay fica pronto já agora (o atalho Ctrl+Shift+O funciona sem visitar a página)
        if (App.Core != null)
            VigiaModule.Prepare(PageOverlay, App.Core, _live, _frames, _settings, () => { try { _settings.Save(); } catch (System.IO.IOException) { } });
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

    // ---------------- ARSENAL · OTIMIZAÇÃO ----------------

    /// <summary>Carrega a página ARSENAL · OTIMIZAÇÃO quando ela é aberta (os ajustes do Windows só na primeira vez).</summary>
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
        var r = MessageBox.Show("O ARSENAL vai:\n\n" +
                                "1. Limpar a memória em espera e enxugar a RAM dos programas.\n" +
                                $"2. Fechar os programas marcados:\n{list}\n\n" +
                                "Salve o que estiver aberto nesses programas antes. Continuar?",
                                "Arsenal · Otimização", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

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

    private bool RequireAdmin()
    {
        if (SensorMonitor.IsElevated) return true;
        MessageBox.Show("Esta ação precisa do Fort Knox aberto como administrador.", "Fort Knox",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void BtnRestoreTweaks_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.TweakBackup.Count == 0)
        {
            MessageBox.Show("Nenhum ajuste foi alterado pelo Fort Knox ainda.", "Arsenal · Otimização",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = MessageBox.Show("Voltar todos os ajustes do Windows para como estavam antes do Fort Knox alterá-los?",
                                "Arsenal · Otimização", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        bool restart = _tweaks.Any(t => t.NeedsRestart && _settings.TweakBackup.ContainsKey(t.Id));
        WindowsTweaks.RestoreAll(_tweaks, _settings);
        foreach (var t in _tweaks) { t.Refresh(); t.Status = "Restaurado."; }
        if (restart) MessageBox.Show("Alguns ajustes só voltam ao original depois de reiniciar o PC.", "Arsenal · Otimização");
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
            PageOverlay.IsOn = !PageOverlay.IsOn;
            handled = true;
        }
        else if (msg == 0x0219) // WM_DEVICECHANGE
            OnDeviceChange(wParam.ToInt32(), lParam);
        return IntPtr.Zero;
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

    // ---------------- Utilidades (o Stress test foi para Modules/Forja/StressView) ----------------

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    /// <summary>Temperatura do núcleo da GPU principal (dedicada).</summary>
    private float? MainGpuTemp() => _live.MainGpuTemp();

    /// <summary>Consumo da GPU principal: NVML quando disponível, senão LibreHardwareMonitor.</summary>
    private float? MainGpuPower() => _live.MainGpuPower();

    private string MainGpuPerfCap() => _live.MainGpuPerfCap();

    /// <summary>Rodapé do menu: temperatura e carga da CPU e estado do Stress test, a cada 1 s.</summary>
    private void UpdateSideSensors()
    {
        float? temp = _monitor.CpuTemperature, load = _monitor.CpuLoad;
        SideTemp.Text = temp == null ? "—" : $"{temp:0.#} °C";
        SideLoad.Text = load == null ? "—" : $"{load:0} %";
    }
}
