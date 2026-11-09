using FortKnox.Core;
using FortKnox.Core.Events;

namespace FortKnox;

/// <summary>
/// Shell: liga o menu (páginas Page* do MainWindow.xaml, cada uma uma View de módulo) ao registro de módulos do núcleo,
/// monta as dependências de cada View e o ciclo de 1 s dos sensores. Desde a Fase 3.3n todas as páginas são módulos.
/// </summary>
public partial class MainWindow
{
    private static readonly Dictionary<string, string> ModuleRoutes = new()
    {
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

    /// <summary>Abre uma página do menu pelo nome da página (Tag do item), como se a pessoa tivesse clicado.</summary>
    private void NavigateTo(string page)
    {
        static IEnumerable<System.Windows.Controls.RadioButton> Items(System.Windows.DependencyObject root)
        {
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            {
                if (child is System.Windows.Controls.RadioButton rb) yield return rb;
                foreach (var inner in Items(child)) yield return inner;
            }
        }
        if (Items(Sidebar).FirstOrDefault(r => r.Tag as string == page) is { } nav) nav.IsChecked = true;
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
