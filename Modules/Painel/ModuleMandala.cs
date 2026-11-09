using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FortKnox.UI;

namespace FortKnox;

/// <summary>
/// Mandala de módulos do PAINEL: a porta de entrada visual do app.
/// Começa COMPRIMIDA (só o logotipo vertical no núcleo). Clique no núcleo: os 12 módulos abrem em órbita.
/// Hover acende o módulo; clique abre as ferramentas dele (módulo com uma ferramenta só abre direto).
/// Clique na ferramenta: a mandala some e a página abre (<see cref="Navigate"/> com a Tag da página do menu).
/// Esc volta um nível. O símbolo nunca gira; só há movimento nas transições (abrir, escolher, sair) e um
/// convite curto de 2 pulsos na primeira exibição. Sem laços contínuos.
/// </summary>
public sealed class ModuleMandala : UserControl
{
    private sealed record Tool(string Name, string Glyph, string Page, string Tip);
    private sealed record Module(string Name, string Glyph, string Tip, Tool[] Tools);

    // Módulos e páginas reais do app (as Tags do menu lateral). Ordem = ordem do menu.
    private static readonly Module[] Modules =
    {
        new("SENTINELA", "", "Monitoramento do hardware", new Tool[]
        {
            new("Sistema", "", "PageSystem", "Processador, memória, placa-mãe e Windows"),
            new("Sensores", "", "PageSensors", "Temperaturas, voltagens e ventoinhas"),
            new("Placa de vídeo", "", "PageGpu", "Especificações e sensores da GPU"),
            new("Discos", "", "PageDisks", "Saúde S.M.A.R.T., desgaste e temperatura"),
            new("Energia", "", "PageEnergy", "Bateria, fonte e consumo"),
        }),
        new("FORJA", "", "Desempenho e testes", new Tool[]
        {
            new("Benchmark", "", "PageBench", "CPU, GPU, memória e disco"),
            new("Stress test", "", "PageStress", "Carga máxima com corte por temperatura"),
            new("Overclock", "", "PageOc", "Energia, teto de clock e estabilidade"),
        }),
        new("ARENA", "", "Jogos", new Tool[]
        {
            new("Biblioteca", "", "PageLauncher", "Todos os jogos instalados"),
            new("FPS estimado", "", "PageGames", "Quanto cada jogo rende neste PC"),
            new("Gargalo", "", "PageBottleneck", "O que limita o seu FPS"),
        }),
        new("ARSENAL", "", "Manutenção do Windows", new Tool[]
        {
            new("Otimização", "", "PageOptimize", "Memória, programas e ajustes para jogos"),
            new("Limpeza", "", "PageCleanup", "Temporários, caches e Lixeira"),
            new("Desinstalar", "", "PageUninstall", "Remove programas e restos"),
            new("Inicialização", "", "PageStartup", "O que abre com o Windows"),
            new("Instalar apps", "", "PageApps", "Os essenciais, versão oficial"),
            new("Atualizar apps", "", "PageUpdates", "Versões novas pelo winget"),
        }),
        new("OFICINA", "", "Portas e dispositivos", new Tool[]
        {
            new("Portas COM", "", "PageMakerPorts", "Arduino, ESP32 e conversores seriais"),
            new("Drivers seriais", "", "PageMakerDrv", "CH340, CP210x, FTDI e Prolific"),
        }),
        new("RADAR", "", "Rede e internet", new Tool[] { new("Conexão", "", "PageNet", "Latência, traceroute e consumo por programa") }),
        new("VIGIA", "", "Overlay em jogo", new Tool[] { new("Overlay", "", "PageOverlay", "FPS, frametime e sensores sobre o jogo") }),
        new("ALICERCE", "", "Drivers", new Tool[] { new("Drivers", "", "PageDriver", "Verificar, baixar e fazer backup") }),
        new("PRISMA", "", "Iluminação RGB", new Tool[] { new("Iluminação", "", "PageInteg", "iCUE, OpenRGB e Razer Chroma") }),
        new("COFRE", "", "Senhas", new Tool[] { new("Senhas", "", "PageVault", "Cofre local com Windows Hello") }),
        new("PRENSA", "", "PDF", new Tool[] { new("PDF", "", "PagePdf", "Mesclar, dividir, comprimir e extrair") }),
        new("MURALHA", "", "Proteção contra ameaças", new Tool[] { new("Varredura", "", "PageShield", "Verificação e quarentena") }),
    };

    // Palco lógico (escala para caber na largura do cartão)
    private const double W = 900, H = 760, CX = W / 2, CY = H / 2, R1 = 212, R2 = 318, CoreR = 92, ClosedH = 290;

    /// <summary>Pede a abertura de uma página do menu (Tag, ex.: "PageCleanup").</summary>
    public event Action<string>? Navigate;

    private readonly Grid _host = new() { ClipToBounds = true };
    private readonly Canvas _stage = new() { Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Canvas _links = new(), _mods = new(), _tlinks = new(), _tools = new();
    private readonly Button _core;
    private readonly Ellipse _ping;
    private readonly TextBlock _caption;
    private readonly List<Button> _modButtons = new();
    private bool _open;
    private int _selected = -1;
    private bool _invited;

    private static bool Motion => SystemParameters.ClientAreaAnimation;

    public ModuleMandala()
    {
        Focusable = true;
        _stage.LayoutTransform = _scale;
        _host.Children.Add(_stage);
        Content = _host;

        // Anéis estáticos (moldura); nada gira
        AddRing(CoreR + 22, "LineStrong", 1, new DoubleCollection { 2, 4 });
        AddRing(CoreR + 30, "Line", 1, null);
        var ticks = new Canvas();
        for (int i = 0; i < 72; i++)
        {
            bool major = i % 6 == 0;
            var (x1, y1) = Polar(CoreR + 36, i * 5);
            var (x2, y2) = Polar(CoreR + (major ? 44 : 40), i * 5);
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, Opacity = major ? 0.8 : 0.4 };
            l.SetResourceReference(Shape.StrokeProperty, major ? "Accent" : "LineStrong");
            ticks.Children.Add(l);
        }
        _stage.Children.Add(ticks);
        _stage.Children.Add(_links);
        _stage.Children.Add(_tlinks);

        // Convite: anel ouro que pulsa 2 vezes na primeira exibição e para
        _ping = new Ellipse { Width = CoreR * 2, Height = CoreR * 2, StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false,
                              RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        _ping.SetResourceReference(Shape.StrokeProperty, "Accent");
        Place(_ping, CX - CoreR, CY - CoreR);
        _stage.Children.Add(_ping);

        // Núcleo: logotipo vertical FIXO (símbolo + FORT ⬟ KNOX)
        var logo = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var sym = new Image { Width = 66, Height = 69 };
        sym.SetResourceReference(Image.SourceProperty, "BrandSymbol");
        var word = new Image { Height = 10.5, Margin = new Thickness(0, 12, 0, 0) };
        word.SetResourceReference(Image.SourceProperty, "BrandWordmark");
        logo.Children.Add(sym);
        logo.Children.Add(word);
        _core = new Button { Width = CoreR * 2, Height = CoreR * 2, Tag = 26.0, Content = logo, ToolTip = "Abrir módulos" };
        _core.SetResourceReference(StyleProperty, "MandalaNode");
        AutomationProperties.SetName(_core, "Abrir módulos do Fort Knox");
        _core.Click += (_, _) => { if (_open) Collapse(); else Expand(); };
        Place(_core, CX - CoreR, CY - CoreR);
        _stage.Children.Add(_mods);
        _stage.Children.Add(_tools);
        _stage.Children.Add(_core);

        // Legenda mínima no rodapé do palco (só com a mandala aberta)
        _caption = new TextBlock { FontSize = 11, Width = W, TextAlignment = TextAlignment.Center, Opacity = 0 };
        _caption.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Place(_caption, 0, H - 26);
        _stage.Children.Add(_caption);

        BuildModules();
        _host.Height = ClosedH;
        SizeChanged += (_, e) => Rescale(e.NewSize.Width);
        Loaded += (_, _) => Invite();
        PreviewKeyDown += OnKey;
    }

    // ---------------- construção ----------------

    private void BuildModules()
    {
        double step = 360.0 / Modules.Length;
        for (int i = 0; i < Modules.Length; i++)
        {
            var m = Modules[i];
            var (x, y) = Polar(R1, i * step);
            var (lx, ly) = Polar(CoreR + 2, i * step);
            var link = new Line { X1 = lx, Y1 = ly, X2 = x, Y2 = y, StrokeThickness = 1 };
            link.SetResourceReference(Shape.StrokeProperty, "Line");
            _links.Children.Add(link);

            var b = Node(m.Name, m.Glyph, 78, 22, true);
            b.ToolTip = m.Tools.Length == 1 ? $"{m.Tip} · abre {m.Tools[0].Name}" : $"{m.Tip} · {m.Tools.Length} ferramentas";
            int idx = i;
            b.Click += (_, _) => SelectModule(idx);
            b.MouseEnter += (_, _) => Hover(idx);
            b.MouseLeave += (_, _) => Hover(-1);
            Place(b, x - 39, y - 39);
            _mods.Children.Add(b);
            _modButtons.Add(b);
        }
        _links.Opacity = 0; _mods.Opacity = 0;
        _links.Visibility = _mods.Visibility = Visibility.Collapsed;
    }

    /// <summary>Nó chanfrado: ícone em cima, rótulo embaixo (Michroma nos módulos, Inter nas ferramentas).</summary>
    private static Button Node(string label, string glyph, double size, double glyphSize, bool module)
    {
        var icon = new TextBlock { Text = glyph, FontSize = glyphSize, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        var text = new TextBlock { Text = label, FontSize = module ? 8.5 : 10.5, HorizontalAlignment = HorizontalAlignment.Center,
                                   Margin = new Thickness(0, module ? 7 : 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = size + 30 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, module ? "Display" : "Text");
        var sp = new StackPanel();
        sp.Children.Add(icon);
        if (module) sp.Children.Add(text);
        var b = new Button { Width = size, Height = size, Tag = module ? 12.0 : 9.0, Content = sp, RenderTransformOrigin = new Point(0.5, 0.5),
                             RenderTransform = new ScaleTransform(1, 1) };
        b.SetResourceReference(StyleProperty, "MandalaNode");
        AutomationProperties.SetName(b, label);
        if (!module)
        {
            // Ferramenta: rótulo fora do nó (legível sem aumentar o nó)
            b.Content = icon;
            b.Resources["label"] = text;
        }
        return b;
    }

    private void AddRing(double r, string brushKey, double thickness, DoubleCollection? dash)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dash != null) e.StrokeDashArray = dash;
        e.SetResourceReference(Shape.StrokeProperty, brushKey);
        Place(e, CX - r, CY - r);
        _stage.Children.Add(e);
    }

    // ---------------- estados ----------------

    public void Expand()
    {
        if (_open) return;
        _open = true;
        _core.ToolTip = "Fechar (Esc)";
        _links.Visibility = _mods.Visibility = Visibility.Visible;
        AnimateHeight(H * _scale.ScaleY);
        Fade(_links, 1, 260);
        Fade(_mods, 1, 220);
        Fade(_caption, 1, 220);
        _caption.Text = $"{Modules.Length} MÓDULOS · {Modules.Sum(m => m.Tools.Length)} FERRAMENTAS";
        // Entrada em sequência curta (só nesta transição)
        for (int i = 0; i < _modButtons.Count; i++) Pop(_modButtons[i], i * 18);
        Focus();
    }

    public void Collapse()
    {
        ClearTools();
        _open = false;
        _selected = -1;
        _core.ToolTip = "Abrir módulos";
        Hover(-1);
        Fade(_links, 0, 160);
        Fade(_caption, 0, 160);
        Fade(_mods, 0, 160, () => { if (!_open) _links.Visibility = _mods.Visibility = Visibility.Collapsed; });
        AnimateHeight(ClosedH * _scale.ScaleY);
    }

    private void SelectModule(int i)
    {
        var m = Modules[i];
        if (m.Tools.Length == 1) { Open(m.Tools[0]); return; }
        if (_selected == i) { ClearTools(); _selected = -1; Hover(-1); UpdateCaption(); return; }
        ClearTools();
        _selected = i;
        Hover(-1);
        double step = 360.0 / Modules.Length, baseA = i * step, spread = 19;
        var (mx, my) = Polar(R1, baseA);
        for (int k = 0; k < m.Tools.Length; k++)
        {
            var t = m.Tools[k];
            double a = baseA + (k - (m.Tools.Length - 1) / 2.0) * spread;
            var (x, y) = Polar(R2, a);
            var link = new Line { X1 = mx, Y1 = my, X2 = x, Y2 = y, StrokeThickness = 1, Opacity = 0.8 };
            link.SetResourceReference(Shape.StrokeProperty, "Accent");
            _tlinks.Children.Add(link);

            var b = Node(t.Name, t.Glyph, 48, 18, false);
            b.ToolTip = t.Tip;
            b.Click += (_, _) => Open(t);
            Place(b, x - 24, y - 24);
            _tools.Children.Add(b);
            Pop(b, k * 25);

            // Rótulo da ferramenta, para fora da órbita
            var label = (TextBlock)b.Resources["label"];
            b.Resources.Remove("label");
            label.MaxWidth = 140;
            label.Margin = new Thickness(0);
            var (lx, ly) = Polar(R2 + 44, a);
            label.Width = 140;
            label.TextAlignment = TextAlignment.Center;
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            label.IsHitTestVisible = false;
            Place(label, lx - 70, ly - 8);
            _tools.Children.Add(label);
            b.MouseEnter += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            b.MouseLeave += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
        Fade(_tlinks, 1, 200);
        UpdateCaption();
        Highlight();
    }

    /// <summary>Usado só pelo modo de revisão visual (--capturas).</summary>
    public void SelectForCapture(int module) { if (!_open) Expand(); SelectModule(module); }

    private void Open(Tool t)
    {
        // A mandala some e a ferramenta abre; ao voltar ao PAINEL ela está comprimida de novo
        Fade(_host, 0, 200, () =>
        {
            Navigate?.Invoke(t.Page);
            ResetClosed();
            _host.Opacity = 1;
        });
    }

    /// <summary>Volta ao estado inicial sem animação (ao sair do PAINEL).</summary>
    public void ResetClosed()
    {
        ClearTools();
        _open = false; _selected = -1;
        foreach (var e in new UIElement[] { _links, _mods, _caption, _host }) e.BeginAnimation(OpacityProperty, null);
        _links.Opacity = _mods.Opacity = _caption.Opacity = 0;
        _links.Visibility = _mods.Visibility = Visibility.Collapsed;
        _host.BeginAnimation(HeightProperty, null);
        _host.Height = ClosedH * _scale.ScaleY;
        Hover(-1);
    }

    private void ClearTools()
    {
        _tools.Children.Clear();
        _tlinks.Children.Clear();
        _tlinks.Opacity = 0;
    }

    private void UpdateCaption()
    {
        _caption.Text = _selected < 0
            ? $"{Modules.Length} MÓDULOS · {Modules.Sum(m => m.Tools.Length)} FERRAMENTAS"
            : $"{Modules[_selected].Name} · {Modules[_selected].Tip.ToUpperInvariant()}";
    }

    // Hover acende um módulo e apaga os demais; com um módulo escolhido, ele fica aceso
    private void Hover(int i)
    {
        if (!_open) return;
        int focus = i >= 0 ? i : _selected;
        for (int k = 0; k < _modButtons.Count; k++)
        {
            var b = _modButtons[k];
            b.Opacity = focus < 0 || k == focus ? 1 : 0.38;
            ((Line)_links.Children[k]).Opacity = focus < 0 ? 1 : k == focus ? 1 : 0.25;
            if (k == focus) ((Line)_links.Children[k]).SetResourceReference(Shape.StrokeProperty, "Accent");
            else ((Line)_links.Children[k]).SetResourceReference(Shape.StrokeProperty, "Line");
        }
        Highlight();
    }

    private void Highlight()
    {
        for (int k = 0; k < _modButtons.Count; k++)
        {
            var b = _modButtons[k];
            if (k == _selected)
            {
                b.SetResourceReference(ForegroundProperty, "AccentText");
                b.SetResourceReference(BorderBrushProperty, "Accent");
            }
            else { b.ClearValue(ForegroundProperty); b.ClearValue(BorderBrushProperty); }
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_open) return;
        if (_selected >= 0) { ClearTools(); _selected = -1; Hover(-1); UpdateCaption(); }
        else Collapse();
        e.Handled = true;
    }

    // ---------------- movimento (só nas transições) ----------------

    private void Invite()
    {
        if (_invited || !Motion) return;
        _invited = true;
        var st = (ScaleTransform)_ping.RenderTransform;
        var dur = TimeSpan.FromSeconds(1.4);
        var grow = new DoubleAnimation(1, 1.32, dur) { BeginTime = TimeSpan.FromSeconds(0.8), RepeatBehavior = new RepeatBehavior(2), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.7, 0, dur) { BeginTime = TimeSpan.FromSeconds(0.8), RepeatBehavior = new RepeatBehavior(2) };
        fade.Completed += (_, _) => _ping.BeginAnimation(OpacityProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _ping.BeginAnimation(OpacityProperty, fade);
    }

    private void Rescale(double width)
    {
        double s = Math.Min(1, Math.Max(0.45, (width - 8) / W));
        _scale.ScaleX = _scale.ScaleY = s;
        _host.BeginAnimation(HeightProperty, null);
        _host.Height = (_open ? H : ClosedH) * s;
    }

    private void AnimateHeight(double to)
    {
        if (!Motion) { _host.BeginAnimation(HeightProperty, null); _host.Height = to; return; }
        var a = new DoubleAnimation(_host.ActualHeight > 0 ? _host.ActualHeight : _host.Height, to, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => _host.Height = to;
        _host.Height = to;
        _host.BeginAnimation(HeightProperty, a);
    }

    private static void Fade(UIElement e, double to, int ms, Action? done = null)
    {
        if (!Motion) { e.BeginAnimation(OpacityProperty, null); e.Opacity = to; done?.Invoke(); return; }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => { e.Opacity = to; done?.Invoke(); };
        e.BeginAnimation(OpacityProperty, a);
    }

    private static void Pop(FrameworkElement e, int delayMs)
    {
        if (!Motion || e.RenderTransform is not ScaleTransform st) return;
        var a = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(220))
        { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ---------------- geometria ----------------

    private static (double X, double Y) Polar(double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180;
        return (CX + r * Math.Cos(a), CY + r * Math.Sin(a));
    }

    private static void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); }
}
