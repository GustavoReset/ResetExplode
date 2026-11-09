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
    private SensorPump? _pump; // ciclo de 1 s dos sensores (Modules/Shared/SensorPump), publicado no barramento

    // Placa de vídeo
    private List<GpuInfo> _gpus = new();

    // Jogos
    private readonly AppSettings _settings = AppSettings.Load();
    private string _cpuName = "", _gpuName = "";
    private double _cpuIdx = 80, _gpuIdx = 50;

    // Gargalo + overlay
    public sealed record MeterItem(string Label, string Value, double Fraction);
    private readonly FrameMonitor _frames = new();
    private LiveReadings _live = null!; // leituras de GPU/CPU compartilhadas com os módulos (Modules/Shared)
    private const int HotkeyId = 0x5245; // "RE"


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
        PageSensors.Attach(_monitor);
        PageSystem.Subtitle = "Lendo informações do sistema…";
        PageSensors.Status = "Detectando o hardware…";

        RegisterModules();
        SnapshotUsb();
        PageSystem.RefreshDrives();
        TitlePage.Text = "Painel";
        PageDash.Refresh();

        // A janela aparece já; as coletas pesadas (WMI, sensores, GPU) rodam em paralelo fora da thread da janela.
        Loaded += async (_, _) => await LoadHardwareAsync();

        // Ciclo de 1 s: relê os sensores e avisa pelo barramento (cada página assina em MainWindow.Shell.cs).
        // Durante a cena de GPU as leituras ficam pausadas: cada consulta trava o driver e aparecia como engasgo no 1% low.
        StartSensorPump();

        StateChanged += (_, _) =>
        {
            // Sem borda nativa, a janela maximizada vaza ~7 px para fora da tela.
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? new Thickness(7) : new Thickness(0);
            BtnMax.Content = max ? "" : "";
        };
        Closed += (_, _) =>
        {
            _pump?.Dispose();
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
                PageSystem.SetInfo(info);
            }
            catch (Exception ex) { PageSystem.Subtitle = $"Não foi possível ler as informações do sistema: {ex.Message}"; }
        }

        async Task LoadSensors()
        {
            try { await _monitor.OpenAsync(); }
            catch (Exception ex) { PageSensors.Status = $"Sensores indisponíveis: {ex.Message}"; return; }
            _monitor.Refresh();
            UpdateCpuSensorWarning();
            PageSensors.BuildChips(_monitor);
            _sentinela?.PrimeDiskHealth(); // Lastro: saúde dos discos sem precisar abrir a página
            PageDash.Refresh();
        }

        async Task LoadGpu()
        {
            try { _gpus = await Task.Run(GpuInfoCollector.Collect); }
            catch { _gpus = new(); }
            PageGpu.ShowGpus(_gpus);

            // Dependem do nome da GPU/CPU
            DetectCpuGpuAndInitArena();
            PageOc.Init();
            PageDash.Refresh();
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

    }

    private void ShowPage(string name)
    {
        foreach (var p in new FrameworkElement[] { PageDash, PageSystem, PageSensors, PageGpu, PageGames, PageBottleneck, PageOverlay, PageBench, PageOptimize, PageOc, PageInteg, PageLauncher, PageDriver, PageStartup, PageApps, PageMakerPorts, PageMakerDrv, PageDisks, PageEnergy, PageNet, PageStress, PageVault, PagePdf })
            p.Visibility = p.Name == name ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Unidades e dispositivos conectados ----------------

    private Dictionary<string, UsbDevice> _usbPresent = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastVolumeEvent = DateTime.MinValue;
    private DispatcherTimer? _usbDebounce;
    private ToastWindow? _toast;

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
        var before = PageSystem.Drives;
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
        await PageSystem.OnVolumesChangedAsync(now); // unidades e discos físicos da página Sistema
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
        PageGpu.Tick();
    }

    // ---------------- Identidade do hardware (ARENA, FORJA e PAINEL usam) ----------------

    /// <summary>
    /// Depois que a GPU é detectada: nome e índice de desempenho de CPU e GPU, e liga as páginas que dependem deles
    /// (ARENA · FPS estimado e Gargalo, VIGIA). O FrameMonitor (FPS por ETW) é compartilhado pelo Gargalo e pelo overlay.
    /// </summary>
    private void DetectCpuGpuAndInitArena()
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

        PageGames.Init(_settings, SaveSettings, _cpuName, _gpuName, _cpuIdx, _gpuIdx);
        _frames.Start();
        PageBottleneck.Init(_live, _frames, _cpuName, _gpuName, _cpuIdx, _gpuIdx);

        // VIGIA: o overlay fica pronto já agora (o atalho Ctrl+Shift+O funciona sem visitar a página)
        if (App.Core != null) VigiaModule.Prepare(PageOverlay, App.Core, _live, _frames, _settings, SaveSettings);
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
