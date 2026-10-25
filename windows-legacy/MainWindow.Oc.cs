using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResetExplode;

/// <summary>Aba RESET OC: ajustes da GPU (NVML), verificador de estabilidade e guia de overclock na BIOS.</summary>
public partial class MainWindow
{
    public sealed record GuideStep(string Number, string Title, string Where, string Text);

    private uint? _ocClockCap;
    private uint? _ocPendingCap;
    private CancellationTokenSource? _ocCts;
    private bool _ocRunning;

    private nint OcGpu
    {
        get
        {
            if (_gpus.Count <= 0)
            {
                return IntPtr.Zero;
            }
            return _gpus[0].NvmlHandle;
        }
    }

    private void InitOc()
    {
        nint ocGpu = OcGpu;
        OcGpuTitle.Text = ((_gpus.Count > 0) ? ("GPU · " + GameCatalog.ShortGpuName(_gpus[0].Name).ToUpperInvariant()) : "GPU");
        if (ocGpu == IntPtr.Zero)
        {
            OcGpuState.Text = "Ajustes disponíveis só para placas NVIDIA (via driver oficial).";
            Slider ocPower = OcPower;
            bool isEnabled = (BtnOcApply.IsEnabled = false);
            ocPower.IsEnabled = isEnabled;
        }
        else
        {
            (uint, uint)? tuple = Nvml.PowerConstraintsMw(ocGpu);
            if (tuple.HasValue)
            {
                (uint, uint) valueOrDefault = tuple.GetValueOrDefault();
                OcPower.Minimum = (double)valueOrDefault.Item1 / 1000.0;
                OcPower.Maximum = (double)valueOrDefault.Item2 / 1000.0;
            }
            uint? num = Nvml.PowerLimitMw(ocGpu);
            if (num.HasValue)
            {
                uint valueOrDefault2 = num.GetValueOrDefault();
                OcPower.Value = (double)valueOrDefault2 / 1000.0;
            }
            uint valueOrDefault3 = Nvml.MaxClock(ocGpu, 0).GetValueOrDefault(3000u);
            Chip("Sem teto", null, on: true);
            Chip($"Leve · {valueOrDefault3 - 300} MHz", valueOrDefault3 - 300, on: false);
            Chip($"Equilibrado · {valueOrDefault3 - 500} MHz", valueOrDefault3 - 500, on: false);
            Chip($"Econômico · {valueOrDefault3 - 800} MHz", valueOrDefault3 - 800, on: false);
            UpdateOcGpuState();
        }
        BuildOcGuide();
        UpdateOcHistory();
        UpdateRecentErrors();
        void Chip(string label, uint? cap, bool on)
        {
            RadioButton radioButton = new RadioButton
            {
                Content = label,
                GroupName = "OcClock",
                Style = (Style)FindResource("Chip"),
                IsChecked = on
            };
            radioButton.Checked += delegate
            {
                _ocPendingCap = cap;
            };
            OcClockPanel.Children.Add(radioButton);
        }
    }

    private void UpdateOcGpuState()
    {
        nint ocGpu = OcGpu;
        if (ocGpu != IntPtr.Zero)
        {
            uint? num = Nvml.PowerLimitMw(ocGpu);
            object obj;
            if (num.HasValue)
            {
                uint valueOrDefault = num.GetValueOrDefault();
                obj = $"{(double)valueOrDefault / 1000.0:0} W";
            }
            else
            {
                obj = "—";
            }
            string value = (string)obj;
            num = Nvml.PowerDefaultMw(ocGpu);
            object obj2;
            if (num.HasValue)
            {
                uint valueOrDefault2 = num.GetValueOrDefault();
                obj2 = $"{(double)valueOrDefault2 / 1000.0:0} W";
            }
            else
            {
                obj2 = "—";
            }
            string value2 = (string)obj2;
            TextBlock ocGpuState = OcGpuState;
            string obj3 = $"Limite de energia atual {value} (padrão {value2}) · teto de clock: ";
            num = _ocClockCap;
            object obj4;
            if (num.HasValue)
            {
                uint valueOrDefault3 = num.GetValueOrDefault();
                obj4 = $"{valueOrDefault3} MHz";
            }
            else
            {
                obj4 = "nenhum";
            }
            num = Nvml.Clock(ocGpu, 0);
            object obj5;
            if (num.HasValue)
            {
                uint valueOrDefault4 = num.GetValueOrDefault();
                obj5 = $"{valueOrDefault4} MHz";
            }
            else
            {
                obj5 = "—";
            }
            ocGpuState.Text = obj3 + (string?)obj4 + " · clock máximo atual: " + (string?)obj5;
        }
    }

    private void OcPower_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OcPowerValue != null)
        {
            OcPowerValue.Text = $"{OcPower.Value:0} W";
        }
    }

    private void BtnOcApply_Click(object sender, RoutedEventArgs e)
    {
        nint ocGpu = OcGpu;
        if (ocGpu == IntPtr.Zero || !RequireAdmin())
        {
            return;
        }
        List<string> list = new List<string>();
        int code = Nvml.SetPowerLimitMw(ocGpu, (uint)(OcPower.Value * 1000.0));
        string text = Nvml.Explain(code);
        list.Add((text != null) ? ("Energia: não aplicado (" + text + ").") : $"Energia: {OcPower.Value:0} W aplicado.");
        uint? ocPendingCap = _ocPendingCap;
        int code2;
        if (ocPendingCap.HasValue)
        {
            uint valueOrDefault = ocPendingCap.GetValueOrDefault();
            code2 = Nvml.LockGpuClocks(ocGpu, 210u, valueOrDefault);
        }
        else
        {
            code2 = Nvml.ResetGpuClocks(ocGpu);
        }
        string text2 = Nvml.Explain(code2);
        if (text2 != null)
        {
            list.Add("Teto de clock: não aplicado (" + text2 + ").");
        }
        else
        {
            _ocClockCap = _ocPendingCap;
            ocPendingCap = _ocClockCap;
            object item;
            if (ocPendingCap.HasValue)
            {
                uint valueOrDefault2 = ocPendingCap.GetValueOrDefault();
                item = $"Teto de clock: {valueOrDefault2} MHz aplicado.";
            }
            else
            {
                item = "Teto de clock: removido.";
            }
            list.Add((string)item);
        }
        OcGpuResult.Text = string.Join(" ", list) + " Rode o verificador para confirmar a estabilidade.";
        UpdateOcGpuState();
    }

    private void BtnOcReset_Click(object sender, RoutedEventArgs e)
    {
        nint ocGpu = OcGpu;
        if (ocGpu == IntPtr.Zero || !RequireAdmin())
        {
            return;
        }
        uint? num = Nvml.PowerDefaultMw(ocGpu);
        int num2;
        if (num.HasValue)
        {
            uint valueOrDefault = num.GetValueOrDefault();
            num2 = Nvml.SetPowerLimitMw(ocGpu, valueOrDefault);
        }
        else
        {
            num2 = 3;
        }
        int code = num2;
        int num3 = Nvml.ResetGpuClocks(ocGpu);
        num = Nvml.PowerDefaultMw(ocGpu);
        if (num.HasValue)
        {
            uint valueOrDefault2 = num.GetValueOrDefault();
            OcPower.Value = (double)valueOrDefault2 / 1000.0;
        }
        foreach (RadioButton item in OcClockPanel.Children.OfType<RadioButton>())
        {
            item.IsChecked = item.Content as string == "Sem teto";
        }
        if (num3 == 0)
        {
            _ocClockCap = null;
        }
        TextBlock ocGpuResult = OcGpuResult;
        string text = Nvml.Explain(code) ?? Nvml.Explain(num3);
        ocGpuResult.Text = ((text != null) ? ("Restauração parcial: " + text + ".") : "GPU de volta ao padrão de fábrica.");
        UpdateOcGpuState();
    }

    private void UpdateRecentErrors()
    {
        HardwareErrors hardwareErrors = OcCheck.ErrorsSince(DateTime.Now.AddDays(-7.0));
        OcRecentErrors.Text = ((hardwareErrors.Error != null) ? ("Não foi possível ler o registro: " + hardwareErrors.Error) : ((hardwareErrors.Total == 0) ? "Nenhum erro de hardware nem reinício do driver de vídeo nos últimos 7 dias." : $"Últimos 7 dias: {hardwareErrors.Whea} erros WHEA · {hardwareErrors.GpuResets} reinícios do driver de vídeo.\nÚltimo: {hardwareErrors.LastDetail}"));
    }

    private void UpdateOcHistory()
    {
        List<BenchRecord> source = _settings.BenchRecords.Where((BenchRecord r) => r.Test == "oc").ToList();
        OcHistory.ItemsSource = source.Select((BenchRecord r) => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, r.Score)).ToList();
    }

    private async void BtnOcCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_ocRunning)
        {
            _ocCts?.Cancel();
            return;
        }
        if (TestRunning || AnyBenchRunning)
        {
            OcStatus.Text = "Termine o outro teste antes.";
            return;
        }
        string text = CheckedTag(OcTargetPanel) ?? "cpu";
        bool flag = ((text == "cpu" || text == "both") ? true : false);
        bool cpu = flag;
        flag = ((text == "gpu" || text == "both") ? true : false);
        bool gpu = flag;
        TimeSpan duration = TimeSpan.FromMinutes(int.Parse(CheckedTag(OcDurPanel) ?? "15"));
        string label = ((OcLabel.Text.Trim().Length > 0) ? OcLabel.Text.Trim() : "sem descrição");
        _ocCts = new CancellationTokenSource();
        CancellationToken ct = _ocCts.Token;
        _ocRunning = true;
        BtnOcCheck.Content = "CANCELAR";
        BtnOcCheck.Style = (Style)FindResource("OutlineButton");
        OcVerdictCard.Visibility = Visibility.Collapsed;
        StackPanel ocTargetPanel = OcTargetPanel;
        flag = (OcDurPanel.IsEnabled = false);
        ocTargetPanel.IsEnabled = flag;
        DateTime start = DateTime.Now;
        double? cpuScore = null;
        double? gpuScore = null;
        float maxCpu = 0f;
        float maxGpu = 0f;
        double sumCpuW = 0.0;
        double sumGpuW = 0.0;
        int nCpuW = 0;
        int nGpuW = 0;
        string fail = null;
        StressTester stress = new StressTester();
        GpuBurn burn = new GpuBurn();
        try
        {
            if (cpu)
            {
                OcStatus.Text = "Medindo a nota da CPU (render multi-core)…";
                BenchResult benchResult = await Task.Run(() => _bench.Run(Environment.ProcessorCount, TimeSpan.Zero, ct), ct);
                if (!ct.IsCancellationRequested)
                {
                    cpuScore = benchResult.Score;
                }
            }
            if (gpu && !ct.IsCancellationRequested)
            {
                OcStatus.Text = "Medindo a nota da GPU (cena de jogo, 30 s)…";
                GpuGameBench gb = new GpuGameBench
                {
                    Duration = TimeSpan.FromSeconds(30.0),
                    AdapterName = ((_gpus.Count > 0) ? _gpus[0].Name : "")
                };
                _gpuBenchActive = true;
                using (ct.Register(gb.Cancel))
                {
                    GpuGameResult gpuGameResult = await Task.Factory.StartNew((Func<GpuGameResult>)gb.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (gpuGameResult != null)
                    {
                        gpuScore = gpuGameResult.Score;
                    }
                    else if (!gb.Cancelled)
                    {
                        fail = "a cena de GPU falhou (" + gb.Error + ")";
                    }
                }
                _gpuBenchActive = false;
            }
            if (fail == null && !ct.IsCancellationRequested)
            {
                if (cpu)
                {
                    stress.Start(cpu: true, ram: true);
                }
                if (gpu)
                {
                    burn.Start(new GpuBurnSettings
                    {
                        Width = 1920,
                        Height = 1080,
                        Msaa = 4,
                        Shells = 128,
                        Iterations = 48,
                        AdapterName = ((_gpus.Count > 0) ? _gpus[0].Name : "")
                    });
                }
                Stopwatch sw = Stopwatch.StartNew();
                float cw = default(float);
                float gw = default(float);
                while (sw.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    float? cpuTemperature = _monitor.CpuTemperature;
                    float? num = MainGpuTemp();
                    if (cpuTemperature.HasValue)
                    {
                        float valueOrDefault = cpuTemperature.GetValueOrDefault();
                        maxCpu = Math.Max(maxCpu, valueOrDefault);
                    }
                    if (num.HasValue)
                    {
                        float valueOrDefault2 = num.GetValueOrDefault();
                        maxGpu = Math.Max(maxGpu, valueOrDefault2);
                    }
                    float? cpuPower = _monitor.CpuPower;
                    int num2;
                    if (cpuPower.HasValue)
                    {
                        cw = cpuPower.GetValueOrDefault();
                        num2 = 1;
                    }
                    else
                    {
                        num2 = 0;
                    }
                    if (((uint)num2 & (cpu ? 1u : 0u)) != 0)
                    {
                        sumCpuW += (double)cw;
                        nCpuW++;
                    }
                    cpuPower = MainGpuPower();
                    int num3;
                    if (cpuPower.HasValue)
                    {
                        gw = cpuPower.GetValueOrDefault();
                        num3 = 1;
                    }
                    else
                    {
                        num3 = 0;
                    }
                    if (((uint)num3 & (gpu ? 1u : 0u)) != 0)
                    {
                        sumGpuW += (double)gw;
                        nGpuW++;
                    }
                    OcProgress.ScaleX = sw.Elapsed / duration;
                    OcStatus.Text = $"Stress {((cpu && gpu) ? "CPU + RAM + GPU" : (cpu ? "CPU + RAM" : "GPU"))} · {sw.Elapsed:mm\\:ss} de {duration:mm\\:ss} · máx. CPU {((maxCpu > 0f) ? $"{maxCpu:0} °C" : "—")} · máx. GPU {((maxGpu > 0f) ? $"{maxGpu:0} °C" : "—")}";
                    if (cpu && cpuTemperature >= 95f)
                    {
                        fail = $"CPU chegou a {cpuTemperature:0} °C (limite de segurança 95 °C)";
                        break;
                    }
                    if (gpu && num >= 88f)
                    {
                        fail = $"GPU chegou a {num:0} °C (limite de segurança 88 °C)";
                        break;
                    }
                    if (cpu && stress.MemoryErrors > 0)
                    {
                        fail = $"{stress.MemoryErrors} erros de memória (RAM/EXPO instável)";
                        break;
                    }
                    if (gpu && !burn.Running)
                    {
                        fail = ((burn.Error != null) ? ("a GPU falhou (" + burn.Error + ")") : "a janela do teste de GPU foi fechada");
                        break;
                    }
                    if ((int)sw.Elapsed.TotalSeconds % 5 == 0)
                    {
                        HardwareErrors hardwareErrors = OcCheck.ErrorsSince(start);
                        if (hardwareErrors.Total > 0)
                        {
                            fail = $"o Windows registrou {hardwareErrors.Whea} erros WHEA e {hardwareErrors.GpuResets} reinícios do driver de vídeo";
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stress.Stop();
            burn.Stop();
            _gpuBenchActive = false;
        }
        _ocRunning = false;
        BtnOcCheck.Content = "INICIAR VERIFICAÇÃO";
        BtnOcCheck.Style = (Style)FindResource("PrimaryButton");
        StackPanel ocTargetPanel2 = OcTargetPanel;
        flag = (OcDurPanel.IsEnabled = true);
        ocTargetPanel2.IsEnabled = flag;
        OcProgress.ScaleX = 0.0;
        if (ct.IsCancellationRequested && fail == null)
        {
            OcStatus.Text = "Verificação cancelada.";
            return;
        }
        HardwareErrors hardwareErrors2 = OcCheck.ErrorsSince(start);
        if (fail == null && hardwareErrors2.Total > 0)
        {
            fail = $"o Windows registrou {hardwareErrors2.Whea} erros WHEA e {hardwareErrors2.GpuResets} reinícios do driver de vídeo";
        }
        bool flag4 = fail == null;
        OcStatus.Text = $"Concluído em {DateTime.Now - start:mm\\:ss}.";
        OcVerdict.Text = (flag4 ? "ESTÁVEL" : "INSTÁVEL");
        OcVerdictWhy.Text = (flag4 ? $"Nenhum erro em {duration.TotalMinutes:0} min de stress. Para Curve Optimizer, use o PC normalmente por um dia e confira os erros recentes." : ("Motivo: " + fail + ". Volte um passo no ajuste e teste de novo."));
        OcVerdictCard.Background = (flag4 ? Card : Fg);
        TextBlock ocVerdict = OcVerdict;
        Brush foreground = (OcVerdictWhy.Foreground = (flag4 ? Fg : Ink));
        ocVerdict.Foreground = foreground;
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Configuração", label)
        };
        if (cpuScore.HasValue)
        {
            double valueOrDefault3 = cpuScore.GetValueOrDefault();
            list.Add(new KeyValuePair<string, string>("Nota CPU (render)", $"{valueOrDefault3:N0} pts"));
        }
        if (gpuScore.HasValue)
        {
            double valueOrDefault4 = gpuScore.GetValueOrDefault();
            list.Add(new KeyValuePair<string, string>("Nota GPU (cena)", $"{valueOrDefault4:N0} pts"));
        }
        if (maxCpu > 0f)
        {
            list.Add(new KeyValuePair<string, string>("Temp. máx. CPU", $"{maxCpu:0} °C"));
        }
        if (maxGpu > 0f)
        {
            list.Add(new KeyValuePair<string, string>("Temp. máx. GPU", $"{maxGpu:0} °C"));
        }
        if (nCpuW > 0)
        {
            list.Add(new KeyValuePair<string, string>("Consumo médio CPU", $"{sumCpuW / (double)nCpuW:0} W"));
        }
        if (nGpuW > 0)
        {
            list.Add(new KeyValuePair<string, string>("Consumo médio GPU", $"{sumGpuW / (double)nGpuW:0} W"));
        }
        list.Add(new KeyValuePair<string, string>("Erros WHEA / resets de vídeo", $"{hardwareErrors2.Whea} / {hardwareErrors2.GpuResets}"));
        OcResult.ItemsSource = list;
        OcResult.Foreground = (flag4 ? Fg : Ink);
        OcVerdictCard.Visibility = Visibility.Visible;
        string[] obj = new string[5]
        {
            flag4 ? "estável" : "INSTÁVEL",
            " · ",
            label,
            null,
            null
        };
        object obj2;
        if (cpuScore.HasValue)
        {
            double valueOrDefault5 = cpuScore.GetValueOrDefault();
            obj2 = $" · CPU {valueOrDefault5:N0}";
        }
        else
        {
            obj2 = "";
        }
        obj[3] = (string)obj2;
        object obj3;
        if (gpuScore.HasValue)
        {
            double valueOrDefault6 = gpuScore.GetValueOrDefault();
            obj3 = $" · GPU {valueOrDefault6:N0}";
        }
        else
        {
            obj3 = "";
        }
        obj[4] = (string)obj3;
        string summary = string.Concat(obj);
        _settings.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "oc", flag4 ? 1.0 : 0.15, summary));
        _settings.Save();
        UpdateOcHistory();
        UpdateRecentErrors();
    }

    private void BuildOcGuide()
    {
        (string Maker, string Model) tuple = OcCheck.Motherboard();
        string item = tuple.Maker;
        string item2 = tuple.Model;
        bool flag = item.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);
        bool flag2 = item.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool flag3 = item.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || item.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        string text = GameCatalog.ShortCpuName(_cpuName);
        bool num = _cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        string text2 = ((flag ? "Gigabyte" : (item.Split(' ').FirstOrDefault() ?? "")) + " " + item2).Trim();
        OcGuideTitle.Text = "GUIA: OVERCLOCK NA BIOS · " + text2.ToUpperInvariant() + " · " + text.ToUpperInvariant();
        OcGuideIntro.Text = "Faça uma mudança por vez e rode o verificador depois de cada uma. Os menus podem mudar um pouco com a versão da BIOS. Valores são pontos de partida seguros, não garantia: cada chip é diferente.";
        string text3 = (flag ? "Tweaker → Advanced CPU Settings → Precision Boost Overdrive  (ou Settings → AMD Overclocking)" : (flag2 ? "Advanced → AMD Overclocking → Precision Boost Overdrive  (ou AI Tweaker → PBO)" : (flag3 ? "OC → Advanced CPU Configuration → AMD Overclocking → Precision Boost Overdrive" : "Advanced → AMD Overclocking → Precision Boost Overdrive")));
        string where = (flag ? "Tweaker → Extreme Memory Profile (X.M.P.) → EXPO1" : (flag2 ? "AI Tweaker → Ai Overclock Tuner → EXPO I" : (flag3 ? "OC → A-XMP/EXPO → Profile 1" : "procure EXPO / XMP no menu de memória")));
        List<GuideStep> list = new List<GuideStep>
        {
            new GuideStep("0", "Antes de começar", flag ? "Save & Exit → Save Profiles" : "Save & Exit → Save Profile", "Salve o perfil atual da BIOS. Se o PC não ligar depois de um ajuste, espere 3 tentativas (muitas placas voltam sozinhas) ou limpe a CMOS (jumper CLR_CMOS na placa ou tirando a bateria por 1 minuto)."),
            new GuideStep("1", "Ative o perfil da memória (EXPO)", where, "É o maior ganho em jogos para a maioria dos PCs: a RAM sai do padrão lento e vai para a velocidade da etiqueta. Teste: verificador \"CPU + RAM\" por 15 min (o teste de RAM acusa erro se o perfil não for estável).")
        };
        if (num)
        {
            list.Add(new GuideStep("2", "Precision Boost Overdrive (PBO)", text3, "Precision Boost Overdrive → Advanced. PBO Limits → Motherboard. Isso libera os limites de energia para o boost durar mais. Teste com o verificador."));
            list.Add(new GuideStep("3", "Curve Optimizer (undervolt da CPU)", text3 + " → Curve Optimizer", "All Cores → Negative → 10. Teste. Se passar, tente 15, 20, 25… até falhar; volte 5 a partir de onde falhou. No " + text + " costuma ficar entre -15 e -30. Instabilidade de Curve Optimizer aparece muito em uso leve: use o PC normalmente por um dia e veja \"Erros de hardware recentes\" aqui — erro WHEA = volte um passo."));
            list.Add(new GuideStep("4", "Max CPU Boost Clock Override (opcional)", text3, "Depois do Curve Optimizer estável: +100 MHz, teste; depois +200 MHz. Ganho pequeno; pode exigir reduzir o Curve Optimizer."));
            list.Add(new GuideStep("5", "Alternativa fria: Eco Mode", text3 + " → Eco Mode", "Em vez de mais desempenho: limita a CPU a 65 W (ou 105 W). Perde ~5–10% em tarefas pesadas multi-core, quase nada em jogos, e roda bem mais frio e silencioso."));
        }
        else
        {
            list.Add(new GuideStep("2", "Processador Intel", "Tweaker / Ai Tweaker / OC → CPU", "Para Intel, prefira undervolt (offset negativo de tensão) e limites de energia (PL1/PL2) em vez de aumentar multiplicadores. Teste cada passo com o verificador."));
        }
        OcGuide.ItemsSource = list;
    }
}
