using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FortKnox.Core.Config;
using LibreHardwareMonitor.Hardware;

namespace FortKnox;

/// <summary>Preferências da VIGIA (seção "vigia" do modules.json).</summary>
public sealed class VigiaSettings
{
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }
}

/// <summary>
/// VIGIA · Overlay de jogo (Fase 3.3f; antes MainWindow.Overlay.cs e o trecho de overlay do MainWindow.xaml.cs):
/// o que mostrar, posição e escala, e a janela transparente atualizada a cada 500 ms.
/// O atalho global Ctrl+Shift+O continua registrado pela janela principal e chama <see cref="IsOn"/>.
/// </summary>
public partial class OverlayView : UserControl
{
    private LiveReadings _live = null!;
    private FrameMonitor _frames = null!;
    private ISettings<VigiaSettings> _prefs = null!;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _timer;
    private HashSet<string>? _show;
    private bool _ready;

    public OverlayView() => InitializeComponent();

    /// <summary>Ligado pelo VigiaModule logo na abertura do app (o atalho funciona antes de a página ser visitada).</summary>
    public void Attach(LiveReadings live, FrameMonitor frames, ISettings<VigiaSettings> prefs)
    {
        _live = live;
        _frames = frames;
        _prefs = prefs;
        foreach (var rb in OvCornerPanel.Children.OfType<RadioButton>())
            rb.IsChecked = (string)rb.Tag == prefs.Value.OverlayCorner;
        foreach (var rb in OvScalePanel.Children.OfType<RadioButton>())
            rb.IsChecked = Math.Abs(double.Parse((string)rb.Tag, CultureInfo.InvariantCulture) - prefs.Value.OverlayScale) < 0.01;
        BuildPicker();
        _ready = true;
        UpdateStatus(null);
        if (IsOn) ChkOverlay_Changed(this, new RoutedEventArgs()); // atalho apertado antes de a GPU terminar de carregar
    }

    /// <summary>Overlay ligado? (o atalho e os benchmarks, que desligam o overlay durante a medição, usam isto).</summary>
    public bool IsOn
    {
        get => ChkOverlay.IsChecked == true;
        set => ChkOverlay.IsChecked = value;
    }

    public void Shutdown()
    {
        _timer?.Stop();
        _overlay?.Close();
        _overlay = null;
    }

    private HashSet<string> Show => _show ??= _prefs?.Value.OverlayItems is { } l ? l.ToHashSet() : OverlayMetrics.Defaults();

    // ------------------------------------------------------------------ opções

    /// <summary>Cria as caixas de seleção, agrupadas por componente.</summary>
    private void BuildPicker()
    {
        OvItemsPanel.Children.Clear();
        foreach (var group in OverlayMetrics.All.GroupBy(m => m.Group))
        {
            OvItemsPanel.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4),
            });
            var wrap = new WrapPanel();
            foreach (var m in group)
            {
                var cb = new CheckBox { Content = m.Label, Tag = m.Key, IsChecked = Show.Contains(m.Key), Margin = new Thickness(0, 0, 18, 6) };
                cb.Click += (_, _) =>
                {
                    if (cb.IsChecked == true) Show.Add(m.Key); else Show.Remove(m.Key);
                    SaveItems();
                };
                wrap.Children.Add(cb);
            }
            OvItemsPanel.Children.Add(wrap);
        }
    }

    private void SaveItems()
    {
        var items = OverlayMetrics.All.Select(m => m.Key).Where(Show.Contains).ToList();
        _prefs.Update(p => p.OverlayItems = items);
        Refresh();
    }

    private void OvPreset_Click(object sender, RoutedEventArgs e)
    {
        _show = (string)((FrameworkElement)sender).Tag switch
        {
            "all" => OverlayMetrics.All.Select(m => m.Key).ToHashSet(),
            "min" => new HashSet<string> { "fps", "frametime", "low1" },
            _ => OverlayMetrics.Defaults(),
        };
        SaveItems();
        BuildPicker();
    }

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void OvOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        string corner = CheckedTag(OvCornerPanel) ?? "TL";
        double scale = double.Parse(CheckedTag(OvScalePanel) ?? "1", CultureInfo.InvariantCulture);
        _prefs.Update(p => { p.OverlayCorner = corner; p.OverlayScale = scale; });
        ApplyOptions();
    }

    private void ApplyOptions()
    {
        if (_overlay == null) return;
        _overlay.Corner = _prefs.Value.OverlayCorner;
        _overlay.SetScale(_prefs.Value.OverlayScale);
        _overlay.Reposition();
    }

    // ------------------------------------------------------------------ janela do overlay

    private void ChkOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (ChkOverlay.IsChecked == true)
        {
            _overlay ??= new OverlayWindow();
            ApplyOptions();
            _overlay.Show();
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher);
            _timer.Start();
            Refresh();
        }
        else
        {
            _timer?.Stop();
            _overlay?.Hide();
        }
    }

    private void Refresh()
    {
        var f = _frames.GetForegroundStats();
        if (_overlay != null && _overlay.IsVisible)
        {
            _overlay.Update(f, BuildLines(f), Show, Show.Contains("clock") ? DateTime.Now.ToString("HH:mm") : null);
            _overlay.KeepOnTop(); // o jogo pode ter passado na frente (janela sem borda "sempre no topo")
        }
        UpdateStatus(f);
    }

    private void UpdateStatus(FrameStats? f)
    {
        if (_frames.Error != null) OvStatus.Text = $"Detecção de FPS indisponível: {_frames.Error}";
        else if (f != null) OvStatus.Text = $"Ativa · {f.ProcessName}: {f.Fps:0} FPS ({f.FrameTimeMs:0.0} ms) · 1% low {f.Low1Fps:0}";
        else OvStatus.Text = "Ativa · aguardando um jogo em primeiro plano";
    }

    /// <summary>Monta as linhas GPU / VRAM / CPU / RAM só com o que foi escolhido; linha vazia não aparece.</summary>
    private List<OverlayLine> BuildLines(FrameStats? f)
    {
        var show = Show;
        var s = BottleneckAnalyzer.Sample(_live.Monitor, _live.MainGpu, f);
        var lines = new List<OverlayLine>();
        string T(float? v) => v is float x ? $"{x:0} °C" : "—";
        string P(double? v) => v is double x ? $"{x:0}%" : "—";
        void Line(string label, List<string> parts) { if (parts.Count > 0) lines.Add(new OverlayLine(label, string.Join("  ", parts))); }

        var gpu = new List<string>();
        if (show.Contains("gpu_load")) gpu.Add(P(s.GpuLoad));
        if (show.Contains("gpu_temp")) gpu.Add(T(s.GpuTemp));
        if (show.Contains("gpu_hotspot")) gpu.Add("HS " + T(_live.GpuRow(SensorType.Temperature, "GPU Hot Spot")?.RawValue));
        if (show.Contains("gpu_clock")) gpu.Add(_live.MainGpuClock(Nvml.ClockGraphics, "GPU Core") is uint c ? $"{c} MHz" : "— MHz");
        if (show.Contains("gpu_memclock")) gpu.Add(_live.MainGpuClock(Nvml.ClockMem, "GPU Memory") is uint mc ? $"mem {mc} MHz" : "mem —");
        if (show.Contains("gpu_power")) gpu.Add(_live.MainGpuPower() is float w ? $"{w:0} W" : "— W");
        if (show.Contains("gpu_volt")) gpu.Add(_live.GpuRow(SensorType.Voltage, "GPU Core")?.RawValue is float v ? $"{v:0.000} V" : "— V");
        if (show.Contains("gpu_fan"))
            gpu.Add(_live.GpuRow(SensorType.Fan, "GPU Fan", true)?.RawValue is float rpm && rpm > 0 ? $"{rpm:0} RPM"
                  : _live.GpuRow(SensorType.Control, "GPU Fan", true)?.RawValue is float pct ? $"fan {pct:0}%" : "fan —");
        if (show.Contains("gpu_perfcap") && _live.MainGpuPerfCap() is { Length: > 0 } cap) gpu.Add(cap);
        Line("GPU", gpu);

        var vram = new List<string>();
        var vr = _live.MainGpuVram();
        if (show.Contains("vram_used")) vram.Add(vr is { } a ? $"{a.Used:0.0} GB" : "—");
        if (show.Contains("vram_pct")) vram.Add(P(s.VramPct));
        if (show.Contains("vram_temp")) vram.Add(T(_live.GpuRow(SensorType.Temperature, "GPU Memory")?.RawValue));
        Line("VRAM", vram);

        var cpu = new List<string>();
        var (avg, max) = _live.CpuClocks();
        if (show.Contains("cpu_load")) cpu.Add(P(s.CpuTotal));
        if (show.Contains("cpu_maxcore")) cpu.Add(s.CpuMaxCore is float mx ? $"núcleo {mx:0}%" : "núcleo —");
        if (show.Contains("cpu_temp")) cpu.Add(T(s.CpuTemp));
        if (show.Contains("cpu_clock")) cpu.Add(avg is float ca ? $"{ca / 1000:0.00} GHz" : "— GHz");
        if (show.Contains("cpu_clockmax")) cpu.Add(max is float cm ? $"pico {cm / 1000:0.00} GHz" : "pico —");
        if (show.Contains("cpu_power")) cpu.Add(_live.Monitor.CpuPower is float cw && cw > 0 ? $"{cw:0} W" : "— W"); // 0 = sem leitura (precisa de administrador)
        Line("CPU", cpu);

        var ram = new List<string>();
        var (total, availB) = Optimizer.MemoryStatus();
        if (show.Contains("ram_used")) ram.Add(total > 0 ? $"{(total - availB) / 1073741824.0:0.0} GB" : "—");
        if (show.Contains("ram_pct")) ram.Add(P(s.RamPct));
        Line("RAM", ram);
        return lines;
    }
}
