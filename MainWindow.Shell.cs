using FortKnox.Core;
using FortKnox.Core.Events;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FortKnox;

/// <summary>
/// Shell: liga o menu (páginas Page* do MainWindow.xaml, cada uma uma View de módulo) ao registro de módulos do núcleo,
/// monta as dependências de cada View e o ciclo de 1 s dos sensores. Desde a Fase 3.3n todas as páginas são módulos.
/// </summary>
public partial class MainWindow
{
    private static readonly Dictionary<string, string> ModuleRoutes = new()
    {
        ["PageHome"] = HomeModule.RouteHome,
        ["PageDash"] = PainelModule.RouteVisao,
        ["PageVault"] = CofreModule.RouteSenhas,
        ["PagePdf"] = PrensaModule.RoutePdf,
        ["PageShield"] = MuralhaModule.RouteScan,
        ["PageNet"] = RadarModule.RouteConexao,
        ["PageOptimize"] = ArsenalModule.RouteOptimize,
        ["PageCleanup"] = ArsenalModule.RouteCleanup,
        ["PageUninstall"] = ArsenalModule.RouteUninstall,
        ["PageUpdates"] = ArsenalModule.RouteUpdates,
        ["PageStartup"] = ArsenalModule.RouteStartup,
        ["PageApps"] = ArsenalModule.RouteApps,
        ["PageMakerPorts"] = OficinaModule.RoutePorts,
        ["PageMakerDrv"] = OficinaModule.RouteDrivers,
        ["PageDriver"] = AlicerceModule.RouteDrivers,
        ["PageInteg"] = PrismaModule.RouteLuzes,
        ["PageLauncher"] = ArenaModule.RouteBiblioteca,
        ["PageGames"] = ArenaModule.RouteFps,
        ["PageBottleneck"] = ArenaModule.RouteGargalo,
        ["PageOverlay"] = VigiaModule.RouteOverlay,
        ["PageStress"] = ForjaModule.RouteStress,
        ["PageOc"] = ForjaModule.RouteOc,
        ["PageBench"] = ForjaModule.RouteBench,
        ["PageSensors"] = SentinelaModule.RouteSensores,
        ["PageGpu"] = SentinelaModule.RouteGpu,
        ["PageDisks"] = SentinelaModule.RouteDiscos,
        ["PageEnergy"] = SentinelaModule.RouteEnergia,
        ["PageSystem"] = SentinelaModule.RouteSistema,
    };

    private FortKnoxHost? Core => App.Core;

    /// <summary>Cena de GPU, ray tracing ou disco rodando: o ciclo de 1 s não consulta os sensores (evita engasgos na medição).</summary>
    private bool _gpuBenchActive;

    /// <summary>Algum benchmark da FORJA rodando (os outros testes esperam).</summary>
    private bool AnyBenchRunning => PageBench.IsRunning;

    private void SaveSettings() { try { _settings.Save(); } catch (System.IO.IOException) { } }

    /// <summary>Guardado para o Lastro pedir a primeira leitura dos discos assim que os sensores abrem.</summary>
    private SentinelaModule? _sentinela;

    /// <summary>"Eventos recentes" do PAINEL (benchmarks, testes, dispositivos conectados).</summary>
    private void AddEvent(string icon, string title, string detail, string value) => PageDash.AddEvent(icon, title, detail, value);

    /// <summary>
    /// Modo de revisão visual (--capturas): abre cada página do menu, espera carregar e grava um PNG da janela.
    /// Inclui a mandala do PAINEL aberta e com um módulo escolhido. Não altera nada no sistema além de ler sensores.
    /// </summary>
    public async Task CaptureTourAsync(string dir, bool light)
    {
        System.IO.Directory.CreateDirectory(dir);
        if (light != ThemeManager.IsLight) ThemeManager.Apply(light);
        WindowState = System.Windows.WindowState.Normal; Width = 1440; Height = 900; Left = 0; Top = 0; Topmost = true;
        await Task.Delay(2500);
        async Task Shot(string name)
        {
            await Task.Delay(1600);
            UpdateLayout();
            // Copia os pixels da tela (o que a pessoa vê). RenderTargetBitmap desenha a Michroma grande quase apagada.
            var src = ScreenCopy.Of(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            await using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        NavigateTo("PageHome");
        PageHome.Open();
        await Shot("00-inicio");
        PageHome.SelectCategoryForCapture(1);
        await Shot("01-inicio-software");
        NavigateTo("PageDash");
        await Shot("02-dashboard");
        SetSidebar(true);
        await Shot("03-menu");
        if (_categoryGroups.TryGetValue("Software", out var softwareGroup)) softwareGroup.IsChecked = true;
        await Shot("04-menu-software");
        SetSidebar(false);
        foreach (var page in ModuleRoutes.Keys.Where(p => p is not ("PageHome" or "PageDash" or "PageVault")))
        {
            NavigateTo(page);
            await Shot(page);
        }
    }

    /// <summary>Abre uma página do menu lateral pelo Tag (ex.: "PageBench"), como se a pessoa tivesse clicado.</summary>
    private void NavigateTo(string page)
    {
        if (_navByPage.TryGetValue(page, out var nav)) { nav.IsChecked = true; return; }
        static IEnumerable<RadioButton> Items(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is RadioButton rb) yield return rb;
                foreach (var inner in Items(child)) yield return inner;
            }
        }
        if (Items(NavRoot).FirstOrDefault(r => r.Tag as string == page) is { } fallback) fallback.IsChecked = true;
    }

    // ---------------- Menu lateral gerado do catálogo ----------------

    private readonly Dictionary<string, RadioButton> _navByPage = new();
    private readonly Dictionary<string, ToggleButton> _categoryGroups = new();
    private bool _navReady;

    /// <summary>
    /// Monta o menu: Início, Dashboard e as quatro categorias (Office, Software, Redes, Defesa).
    /// Cada categoria expande as ferramentas (nível 2). A mesma estrutura alimenta a mandala (HomeView).
    /// </summary>
    private void BuildNavigation()
    {
        NavRoot.Children.Clear();
        _navByPage.Clear();
        _categoryGroups.Clear();

        NavRoot.Children.Add(NavItem("PageHome", "\uE80F", "Início", true));
        NavRoot.Children.Add(NavItem("PageDash", "\uE7C4", "Dashboard", false));
        NavRoot.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(22, 10, 22, 6) });

        foreach (var category in AppCatalog.Categories)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = category.Glyph,
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 14,
                Width = 26,
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock { Text = category.Name, VerticalAlignment = VerticalAlignment.Center });

            var group = new ToggleButton { Style = (Style)FindResource("NavGroup"), Content = content };
            var items = new StackPanel { Margin = new Thickness(10, 0, 0, 4), Visibility = Visibility.Collapsed };
            group.Checked += (_, _) => items.Visibility = Visibility.Visible;
            group.Unchecked += (_, _) => items.Visibility = Visibility.Collapsed;

            foreach (var tool in category.Tools) items.Children.Add(NavItem(tool.Page, tool.Glyph, tool.Name));
            _categoryGroups[category.Name] = group;
            NavRoot.Children.Add(group);
            NavRoot.Children.Add(items);
        }
        _navReady = true;
    }

    private RadioButton NavItem(string page, string glyph, string label, bool @checked = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 14,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var item = new RadioButton { Style = (Style)FindResource("NavItem"), Content = content, Tag = page };
        item.Checked += Nav_Checked;
        _navByPage[page] = item;
        if (@checked) item.IsChecked = true;
        return item;
    }

    /// <summary>Fecha o menu lateral (Esc) quando ele está aberto.</summary>
    private void CloseSidebarOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _sidebarOpen) { SetSidebar(false); e.Handled = true; }
    }

    /// <summary>
    /// Liga o ciclo de 1 s ao barramento: cada página assina <see cref="SensorsRefreshed"/> e se atualiza sozinha.
    /// Sem núcleo (não acontece fora do modo --catalogo), nada roda.
    /// </summary>
    private void StartSensorPump()
    {
        if (Core == null) return;
        var bus = Core.Bus;
        void On(Action tick) => bus.Subscribe<SensorsRefreshed>(_ => tick(), Delivery.Synchronous);
        On(() => { if (_monitor.IsOpen) PageSensors.Status = $"{_monitor.Rows.Count} sensores · atualizado {DateTime.Now:HH:mm:ss}"; });
        On(PageStress.Tick);
        On(UpdateSideSensors);
        On(UpdateGpuLive);
        On(UpdateCpuSensorWarning);
        On(PageDash.Collect);
        On(PageBottleneck.Tick);
        On(PageBench.Tick);
        On(PageOptimize.Tick);
        _pump = new SensorPump(_monitor, bus, () => _gpuBenchActive);
        _pump.Start();
    }

    private void RegisterModules()
    {
        // INÍCIO: a mandala de categorias (a mesma navegação do menu lateral)
        PageHome.Attach(NavigateTo);

        // PAINEL: lê o resto do app; os botões levam às páginas
        PageDash.Attach(new DashView.Host
        {
            Live = _live, Gpus = () => _gpus, CpuName = () => _cpuName, Settings = _settings,
            Tweaks = () => PageOptimize.Tweaks, Drives = () => PageSystem.Drives, Navigate = NavigateTo,
            NavigateRoute = route => { if (ModuleRoutes.FirstOrDefault(kv => kv.Value == route).Key is { } page) NavigateTo(page); },
            // Lastro: o núcleo soma os indicadores de saúde de todos os módulos (Fase 3.4)
            Lastro = () => Core?.Modules.RecalculateLastro() ?? new Core.Modules.HealthState(),
            HealthDetails = () => Core?.State.Get<Core.Modules.HealthState>().All ?? Array.Empty<Core.Modules.IndicatorResult>(),
        });

        // FORJA · Stress test: ligado já na abertura (o ciclo de 1 s alimenta o gráfico mesmo com a página fechada)
        PageStress.Attach(_live);
        PageStress.RunningChanged += running => SideStress.Text = running ? "rodando" : "parado";
        PageStress.Finished += (title, detail, state) => AddEvent("", title, detail, state);

        // FORJA · Overclock: histórico em AppSettings.BenchRecords (o PAINEL e o relatório também leem)
        PageOc.Attach(_live, _settings, SaveSettings,
                      () => PageStress.IsRunning || AnyBenchRunning,
                      pause => _gpuBenchActive = pause,
                      () => _cpuName);

        // FORJA · Benchmark: render de CPU, cena de jogo, ray tracing, simulação, memória e disco
        PageBench.Attach(new BenchView.Host
        {
            Live = _live, Settings = _settings, Save = SaveSettings, CpuName = () => _cpuName,
            OtherBusy = () => PageStress.IsRunning || PageOc.IsRunning,
            PauseSensors = pause => _gpuBenchActive = pause,
            GetOverlay = () => PageOverlay.IsOn, SetOverlay = on => PageOverlay.IsOn = on,
            AddEvent = AddEvent,
        });
        PageBench.Init();

        PageCleanup.Attach(AddEvent);
        PageUninstall.Attach(AddEvent, AppSettings.Folder);
        PageUpdates.Attach(AddEvent);

        // SENTINELA · Placa de vídeo, Discos e Energia (Sensores é ligado no construtor, junto com o SensorMonitor)
        PageGpu.Attach(_monitor);
        PageDisks.Attach(_monitor);
        PageEnergy.Attach(_monitor);
        PageSystem.Attach(new SystemView.Host
        {
            Monitor = _monitor, Gpus = () => _gpus, Settings = _settings, LastNetwork = () => PageNet.LastResult,
        });

        // RADAR · teste de conexão: espera benchmark e Stress test terminarem
        PageNet.Attach(() => AnyBenchRunning || PageStress.IsRunning || PageOc.IsRunning);

        if (Core == null) return;
        Core.Modules.Add(new HomeModule(PageHome));
        Core.Modules.Add(new PainelModule(PageDash));
        Core.Modules.Add(_sentinela = new SentinelaModule(PageDisks, PageEnergy, _monitor));
        Core.Modules.Add(new CofreModule(PageVault, this));
        Core.Modules.Add(new PrensaModule(PagePdf));
        Core.Modules.Add(new MuralhaModule(PageShield, AddEvent));
        Core.Modules.Add(new RadarModule(PageNet));
        Core.Modules.Add(new ArsenalModule(PageOptimize, PageCleanup, PageUninstall, PageStartup, PageApps, PageUpdates, () => _settings, () => { try { _settings.Save(); } catch (System.IO.IOException) { } }));
        Core.Modules.Add(new OficinaModule(PageMakerPorts, PageMakerDrv, () => _settings.ComDbBackup, () => { _settings.ComDbBackup = null; try { _settings.Save(); } catch (System.IO.IOException) { } }));
        Core.Modules.Add(new AlicerceModule(PageDriver));
        Core.Modules.Add(new PrismaModule(PageInteg, _monitor));
        Core.Modules.Add(new ArenaModule(PageLauncher, PageGames, PageBottleneck, () => _settings, () => { try { _settings.Save(); } catch (System.IO.IOException) { } }));
        Core.Modules.Add(new VigiaModule(PageOverlay));

        Core.Modules.Add(new ForjaModule(PageStress, PageOc, PageBench));
    }

    /// <summary>Chamado por Nav_Checked. Erros do módulo vão para o log e para a barra de status, sem derrubar o app.</summary>
    private async void NavigateModules(string page)
    {
        if (Core == null) return;
        string route = ModuleRoutes.TryGetValue(page, out var r) ? r : "legado/" + page;
        try
        {
            await Core.Modules.NavigateAsync(route);
            Core.Log.Write(LogLevel.Debug, "Página: " + route);
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Navegação para {route} falhou", ex);
            PageSensors.Status = "Não foi possível abrir esta página: " + ex.Message;
        }
    }

    /// <summary>Engrenagem da barra de título: Configurações do app (por enquanto, como o COFRE abre).</summary>
    private void Settings_Click(object sender, System.Windows.RoutedEventArgs e) => new SettingsWindow(this).ShowDialog();

    /// <summary>Ao fechar a janela: encerra os módulos na thread da interface (eles mexem em controles).</summary>
    private void ShutdownModules()
    {
        if (Core == null) return;
        var t = Core.Modules.ShutdownAsync();
        if (!t.IsCompleted) Core.Log.Warn("Algum módulo terminou de encerrar em segundo plano");
    }
}

/// <summary>Captura da janela pela tela (GDI BitBlt), usada só no modo --capturas.</summary>
internal static class ScreenCopy
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    public static System.Windows.Media.Imaging.BitmapSource Of(System.Windows.Window w)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var tl = w.PointToScreen(new System.Windows.Point(0, 0));
        int x = (int)tl.X, y = (int)tl.Y, wd = (int)(w.ActualWidth * dpi.DpiScaleX), ht = (int)(w.ActualHeight * dpi.DpiScaleY);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, wd, ht);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, wd, ht, screen, x, y, 0x00CC0020);
        SelectObject(mem, old);
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }
}
