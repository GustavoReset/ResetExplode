using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FortKnox;
using Path = System.Windows.Shapes.Path;

namespace FortKnox.UI;

/// <summary>
/// Mandala de categorias da página INÍCIO: 4 áreas fixas (Office, Software, Redes e Defesa) em órbita
/// e, ao escolher uma área, as ferramentas reais do <see cref="AppCatalog"/> em um segundo anel.
/// O logotipo no núcleo fica sempre fixo; só os anéis decorativos, os nós e os rótulos se movem.
/// Clique no núcleo abre/recolhe; clique na categoria abre/fecha as ferramentas; Esc volta um nível.
/// </summary>
public sealed class CategoryMandala : UserControl
{
    // Palco lógico desenhado em 1100 x 760 e escalado pelo Viewbox.
    private const double W = 1100, H = 760, CX = W / 2, CY = 378;
    private const double R1 = 200, R2 = 330, CoreR = 118;
    private const double CategorySize = 94, ToolSize = 44;
    private const double ToolLabelWidth = 150, ToolLabelRadius = 34;
    private const double MaxToolArc = 320, ToolStep = 16;
    private const double SectorInner = 256, SectorOuter = 376;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static bool Motion => SystemParameters.ClientAreaAnimation;

    private sealed class CategoryVisual
    {
        public CategoryVisual(ToolCategory data, int index, double angle, double x, double y,
                              Button button, Line link, ScaleTransform scale, TranslateTransform move)
        {
            Data = data; Index = index; Angle = angle; X = x; Y = y;
            Button = button; Link = link; Scale = scale; Move = move;
        }

        public ToolCategory Data { get; }
        public int Index { get; }
        public double Angle { get; }
        public double X { get; }
        public double Y { get; }
        public Button Button { get; }
        public Line Link { get; }
        public ScaleTransform Scale { get; }
        public TranslateTransform Move { get; }
    }

    private sealed class ToolVisual
    {
        public ToolVisual(ToolItem data, int index, double angle, double x, double y,
                          double fromX, double fromY, Button button, TextBlock label, Path link,
                          ScaleTransform scale, TranslateTransform move)
        {
            Data = data; Index = index; Angle = angle; X = x; Y = y; FromX = fromX; FromY = fromY;
            Button = button; Label = label; Link = link; Scale = scale; Move = move;
        }

        public ToolItem Data { get; }
        public int Index { get; }
        public double Angle { get; }
        public double X { get; }
        public double Y { get; }
        public double FromX { get; }
        public double FromY { get; }
        public Button Button { get; }
        public TextBlock Label { get; }
        public Path Link { get; }
        public ScaleTransform Scale { get; }
        public TranslateTransform Move { get; }
    }

    private readonly Canvas _stage = new();
    private readonly Canvas _decor = new(), _ticks = new(), _arcs = new();
    private readonly RotateTransform _ticksSpin = new(0, CX, CY), _arcsSpin = new(0, CX, CY);
    private readonly Canvas _linkLayer = new(), _toolLinkLayer = new();
    private readonly Canvas _categoryLayer = new(), _toolLayer = new(), _toolLabelLayer = new();
    private readonly Path _sectorFill = new(), _sectorStroke = new();
    private readonly List<CategoryVisual> _categories = new();
    private readonly Button _core;
    private readonly TextBlock _coreText;

    private List<ToolVisual> _currentTools = new();
    private int _selected = -1, _hoverCategory = -1, _hoverTool = -1, _toolsGeneration;
    private bool _open, _spinning, _navigating, _sectorShown;

    /// <summary>Pede ao Shell que abra a página da ferramenta escolhida (Tag, ex.: "PageCleanup").</summary>
    public event Action<string>? Navigate;

    /// <summary>Verdadeiro enquanto a mandala está aberta (o núcleo mostra "RECOLHER").</summary>
    public bool IsOpen => _open;

    public CategoryMandala()
    {
        Focusable = false;
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Mandala de categorias do Fort Knox");

        _stage.Width = W;
        _stage.Height = H;
        _stage.ClipToBounds = true;
        _stage.Background = Brushes.Transparent;

        BuildDecor();
        _stage.Children.Add(_decor);

        ConfigureSector();
        _stage.Children.Add(_sectorFill);
        _stage.Children.Add(_sectorStroke);

        _linkLayer.IsHitTestVisible = false;
        _toolLinkLayer.IsHitTestVisible = false;
        _categoryLayer.IsHitTestVisible = false;
        _toolLabelLayer.IsHitTestVisible = false;
        _stage.Children.Add(_linkLayer);
        _stage.Children.Add(_toolLinkLayer);
        _stage.Children.Add(_categoryLayer);
        _stage.Children.Add(_toolLayer);
        _stage.Children.Add(_toolLabelLayer);

        var core = BuildCore();
        _core = core.Button;
        _coreText = core.Label;
        Place(_core, CX - CoreR, CY - CoreR);
        _stage.Children.Add(_core);

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _stage,
        };

        BuildCategories();
        ApplyCollapsedState();

        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;
        Loaded += (_, _) => { if (_open) StartSpin(); };
        Unloaded += (_, _) => StopSpin();
    }

    // ---------------- API pública ----------------

    /// <summary>Abre a mandala no nível das categorias.</summary>
    public void Open()
    {
        if (_open) return;
        _open = true;
        _navigating = false;
        _stage.Opacity = 1;
        _core.IsHitTestVisible = true;
        _core.IsTabStop = true;
        _categoryLayer.IsHitTestVisible = true;
        UpdateCoreState();

        Stop(_decor, OpacityProperty);
        Tween(_decor, OpacityProperty, _decor.Opacity, 1, 450);
        ShowCategories(animate: true);
        StartSpin();
    }

    /// <summary>Recolhe a mandala (fecha as ferramentas e os dois níveis).</summary>
    public void Collapse()
    {
        if (!_open) return;
        bool hadFocus = IsKeyboardFocusWithin;
        if (hadFocus) _core.Focus();

        if (!Motion)
        {
            ApplyCollapsedState();
            return;
        }

        _open = false;
        _navigating = false;
        ++_toolsGeneration;
        var oldTools = _currentTools;
        _currentTools = new List<ToolVisual>();
        _selected = -1;
        _hoverTool = -1;
        _hoverCategory = -1;
        _categoryLayer.IsHitTestVisible = false;
        UpdateCoreState();
        StopSpin();

        Tween(_decor, OpacityProperty, _decor.Opacity, 0.4, 420);
        HideSector(animate: true);
        FadeOutToolSet(oldTools, animate: true);

        int count = _categories.Count;
        for (int i = 0; i < count; i++)
        {
            var c = _categories[i];
            c.Button.IsHitTestVisible = false;
            c.Button.IsTabStop = false;
            int delay = (count - 1 - i) * 20;
            Tween(c.Button, OpacityProperty, c.Button.Opacity, 0, 380, delay);
            Tween(c.Scale, ScaleTransform.ScaleXProperty, c.Scale.ScaleX, 0.82, 380, delay);
            Tween(c.Scale, ScaleTransform.ScaleYProperty, c.Scale.ScaleY, 0.82, 380, delay);
            Tween(c.Move, TranslateTransform.XProperty, c.Move.X, CX - c.X, 420, delay);
            Tween(c.Move, TranslateTransform.YProperty, c.Move.Y, CY - c.Y, 420, delay);
            Tween(c.Link, OpacityProperty, c.Link.Opacity, 0, 260, delay);
        }
    }

    /// <summary>Volta ao estado inicial sem animação (usado ao sair da Home).</summary>
    public void Reset()
    {
        if (IsKeyboardFocusWithin) _core.Focus();
        _navigating = false;
        ApplyCollapsedState();
    }

    /// <summary>Usado só pelo modo de revisão visual (--capturas): abre e escolhe uma categoria.</summary>
    public void SelectCategoryForCapture(int index)
    {
        if (index < 0 || index >= AppCatalog.Categories.Count) return;
        if (!_open) Open();
        SelectCategory(index, animate: false, allowToggle: false);
    }

    // ---------------- construção ----------------

    private (Button Button, TextBlock Label) BuildCore()
    {
        var logo = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var symbol = new Image { Width = 66, Height = 69, SnapsToDevicePixels = true };
        symbol.SetResourceReference(Image.SourceProperty, "BrandSymbol");
        var word = new Image { Height = 10.5, Margin = new Thickness(0, 12, 0, 0), SnapsToDevicePixels = true };
        word.SetResourceReference(Image.SourceProperty, "BrandWordmark");
        var label = new TextBlock
        {
            Text = "ABRIR FERRAMENTAS",
            FontSize = 7.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 13, 0, 0),
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        logo.Children.Add(symbol);
        logo.Children.Add(word);
        logo.Children.Add(label);

        var button = new Button
        {
            Width = CoreR * 2,
            Height = CoreR * 2,
            Content = logo,
            Focusable = true,
            IsTabStop = true,
            ToolTip = "Abrir ferramentas",
        };
        button.SetResourceReference(StyleProperty, "Mandala2Core");
        AutomationProperties.SetAutomationId(button, "MandalaCore");
        AutomationProperties.SetHelpText(button, "Enter ou Espaço alterna; Esc volta um nível.");
        button.Click += (_, _) => { if (_navigating) return; if (_open) Collapse(); else Open(); };
        button.MouseEnter += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        button.MouseLeave += (_, _) =>
        {
            if (!button.IsKeyboardFocused) label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        };
        button.GotKeyboardFocus += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        button.LostKeyboardFocus += (_, _) =>
        {
            if (!button.IsMouseOver) label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        };
        return (button, label);
    }

    private void BuildDecor()
    {
        _decor.IsHitTestVisible = false;
        _decor.Opacity = 0.4;

        var glow = new Ellipse { Width = 380, Height = 380, Opacity = 0.10, IsHitTestVisible = false };
        glow.SetResourceReference(Shape.FillProperty, "AccentSoft");
        Place(glow, CX - 190, CY - 190);
        _decor.Children.Add(glow);

        AddStaticRing(150, "LineStrong", 1, new DoubleCollection { 2, 6 }, 0.32);
        AddStaticRing(250, "Line", 1, null, 0.20);
        AddStaticRing(352, "Line", 1, new DoubleCollection { 1, 7 }, 0.14);

        _ticks.RenderTransform = _ticksSpin;
        _ticks.IsHitTestVisible = false;
        for (int i = 0; i < 120; i++)
        {
            bool major = i % 10 == 0;
            var (x1, y1) = Polar(128, i * 3);
            var (x2, y2) = Polar(major ? 136 : 132, i * 3);
            var tick = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                StrokeThickness = 1,
                Opacity = major ? 0.55 : 0.30,
                IsHitTestVisible = false,
            };
            tick.SetResourceReference(Shape.StrokeProperty, major ? "Accent" : "LineStrong");
            _ticks.Children.Add(tick);
        }
        _decor.Children.Add(_ticks);

        _arcs.RenderTransform = _arcsSpin;
        _arcs.IsHitTestVisible = false;
        for (int i = 0; i < 6; i++)
        {
            var arc = new Path
            {
                Data = ArcGeometry(272, i * 60 + 8, i * 60 + 44),
                StrokeThickness = 1.2,
                Opacity = 0.26,
                IsHitTestVisible = false,
            };
            arc.SetResourceReference(Shape.StrokeProperty, "LineStrong");
            _arcs.Children.Add(arc);
        }
        for (int i = 0; i < 24; i++)
        {
            var (x, y) = Polar(356, i * 15);
            var dot = new Rectangle
            {
                Width = 3, Height = 3,
                RenderTransform = new RotateTransform(45, 1.5, 1.5),
                Opacity = 0.30,
                IsHitTestVisible = false,
            };
            dot.SetResourceReference(Shape.FillProperty, "LineStrong");
            Place(dot, x - 1.5, y - 1.5);
            _arcs.Children.Add(dot);
        }
        _decor.Children.Add(_arcs);
    }

    private void ConfigureSector()
    {
        _sectorFill.IsHitTestVisible = false;
        _sectorFill.Stroke = null;
        _sectorFill.Opacity = 0;
        _sectorFill.SetResourceReference(Shape.FillProperty, "Accent");

        _sectorStroke.IsHitTestVisible = false;
        _sectorStroke.Fill = null;
        _sectorStroke.StrokeThickness = 1;
        _sectorStroke.Opacity = 0;
        _sectorStroke.SetResourceReference(Shape.StrokeProperty, "Accent");
    }

    private void AddStaticRing(double radius, string brush, double thickness, DoubleCollection? dash, double opacity)
    {
        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            StrokeThickness = thickness,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        if (dash != null) ring.StrokeDashArray = dash;
        ring.SetResourceReference(Shape.StrokeProperty, brush);
        Place(ring, CX - radius, CY - radius);
        _decor.Children.Add(ring);
    }

    private void BuildCategories()
    {
        var categories = AppCatalog.Categories;
        for (int i = 0; i < categories.Count; i++)
        {
            var data = categories[i];
            double angle = i * (360.0 / categories.Count);
            var (x, y) = Polar(R1, angle);

            var (x1, y1) = Polar(CoreR + 4, angle);
            var (x2, y2) = Polar(R1 - 34, angle);
            var link = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                StrokeThickness = 1,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            link.SetResourceReference(Shape.StrokeProperty, "Line");
            _linkLayer.Children.Add(link);

            var icon = new TextBlock
            {
                Text = data.Glyph,
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            var name = new TextBlock
            {
                Text = data.Name.ToUpperInvariant(),
                FontSize = 9,
                MaxWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            };
            name.SetResourceReference(TextBlock.FontFamilyProperty, "Display");
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(icon);
            content.Children.Add(name);

            var button = new Button
            {
                Width = CategorySize,
                Height = CategorySize,
                Tag = 6.0,
                Content = content,
                IsTabStop = false,
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            button.SetResourceReference(StyleProperty, "Mandala2Node");
            button.ToolTip = $"{data.Tagline} · {data.Tools.Count} {(data.Tools.Count == 1 ? "ferramenta" : "ferramentas")}";
            AutomationProperties.SetAutomationId(button, "MandalaCategory" + i);
            AutomationProperties.SetName(button, $"{data.Name}: {data.Tagline}. {data.Tools.Count} ferramentas");
            AutomationProperties.SetHelpText(button, "Enter ou Espaço abre as ferramentas; Esc volta.");
            AutomationProperties.SetPositionInSet(button, i + 1);
            AutomationProperties.SetSizeOfSet(button, categories.Count);

            var scale = new ScaleTransform(0.82, 0.82);
            var move = new TranslateTransform(CX - x, CY - y);
            button.RenderTransform = new TransformGroup { Children = { scale, move } };
            Place(button, x - CategorySize / 2, y - CategorySize / 2);
            _categoryLayer.Children.Add(button);

            int index = i;
            button.Click += (_, _) => SelectCategory(index, animate: true, allowToggle: true);
            button.MouseEnter += (_, _) => SetHoverCategory(index);
            button.MouseLeave += (_, _) => { if (!button.IsKeyboardFocused) SetHoverCategory(-1); };
            button.GotKeyboardFocus += (_, _) => SetHoverCategory(index);
            button.LostKeyboardFocus += (_, _) => { if (!button.IsMouseOver) SetHoverCategory(-1); };

            _categories.Add(new CategoryVisual(data, i, angle, x, y, button, link, scale, move));
        }
    }

    // ---------------- nível 2: ferramentas ----------------

    private void SelectCategory(int index, bool animate, bool allowToggle)
    {
        if (index < 0 || index >= AppCatalog.Categories.Count) return;
        if (!_open) Open();
        if (allowToggle && _selected == index)
        {
            CloseTools(animate, focusCategory: true);
            return;
        }
        if (!allowToggle && _selected == index && _currentTools.Count > 0) return;

        int generation = ++_toolsGeneration;
        var oldTools = _currentTools;
        _currentTools = new List<ToolVisual>();
        _hoverTool = -1;
        _selected = index;
        ShowSector(index, animate);
        ApplyCategoryState();

        if (oldTools.Count == 0)
        {
            BuildTools(index, animate);
            return;
        }

        FadeOutToolSet(oldTools, animate, () =>
        {
            if (generation != _toolsGeneration || !_open || _selected != index) return;
            BuildTools(index, animate);
        });
    }

    private void BuildTools(int index, bool animate)
    {
        if (!_open || _selected != index || index >= _categories.Count) return;
        var data = AppCatalog.Categories[index];
        var category = _categories[index];
        int count = data.Tools.Count;
        if (count == 0) return;

        var (step, span) = ToolLayout(count);
        double startAngle = category.Angle - span / 2;
        var source = PolarPoint(R1 + 6, category.Angle);
        double maxDelay = 0;

        for (int k = 0; k < count; k++)
        {
            var tool = data.Tools[k];
            double angle = startAngle + k * step;
            var (x, y) = Polar(R2, angle);
            var end = PolarPoint(R2 - 23, angle);
            var control = PolarPoint((R1 + R2) / 2 + 10, angle);

            var link = new Path
            {
                Data = CurveGeometry(source, control, end),
                StrokeThickness = 1,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            link.SetResourceReference(Shape.StrokeProperty, "LineStrong");
            _toolLinkLayer.Children.Add(link);

            var icon = new TextBlock
            {
                Text = tool.Glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");

            var button = new Button
            {
                Width = ToolSize,
                Height = ToolSize,
                Tag = 5.0,
                Content = icon,
                IsTabStop = true,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                ToolTip = tool.Description,
            };
            button.SetResourceReference(StyleProperty, "Mandala2Node");
            AutomationProperties.SetAutomationId(button, "MandalaTool" + index + "_" + k);
            AutomationProperties.SetName(button, $"{tool.Name}, {data.Name}");
            AutomationProperties.SetHelpText(button, tool.Description);
            AutomationProperties.SetPositionInSet(button, k + 1);
            AutomationProperties.SetSizeOfSet(button, count);

            var scale = new ScaleTransform(0.82, 0.82);
            double fromX = category.X - x;
            double fromY = category.Y - y;
            var move = new TranslateTransform(fromX, fromY);
            button.RenderTransform = new TransformGroup { Children = { scale, move } };
            Place(button, x - ToolSize / 2, y - ToolSize / 2);
            _toolLayer.Children.Add(button);

            var label = CreateToolLabel(tool.Name, angle);
            _toolLabelLayer.Children.Add(label);

            var visual = new ToolVisual(tool, k, angle, x, y, fromX, fromY, button, label, link, scale, move);
            _currentTools.Add(visual);

            button.Click += (_, _) => ActivateTool(visual);
            button.MouseEnter += (_, _) => SetHoverTool(k);
            button.MouseLeave += (_, _) => { if (!button.IsKeyboardFocused) SetHoverTool(-1); };
            button.GotKeyboardFocus += (_, _) => SetHoverTool(k);
            button.LostKeyboardFocus += (_, _) => { if (!button.IsMouseOver) SetHoverTool(-1); };

            if (Motion && animate)
            {
                int delay = k * 25;
                maxDelay = Math.Max(maxDelay, delay + 500);
                Tween(button, OpacityProperty, 0, 1, 430, delay);
                Tween(scale, ScaleTransform.ScaleXProperty, 0.82, 1, 480, delay);
                Tween(scale, ScaleTransform.ScaleYProperty, 0.82, 1, 480, delay);
                Tween(move, TranslateTransform.XProperty, fromX, 0, 480, delay);
                Tween(move, TranslateTransform.YProperty, fromY, 0, 480, delay);
                Tween(link, OpacityProperty, 0, 0.5, 300, 90 + delay);
                Tween(label, OpacityProperty, 0, 1, 320, 70 + delay);
            }
            else
            {
                button.Opacity = 1;
                scale.ScaleX = scale.ScaleY = 1;
                move.X = move.Y = 0;
                link.Opacity = 0.5;
                label.Opacity = 1;
            }
        }

        if (Motion && animate)
        {
            int generation = _toolsGeneration;
            Later((int)maxDelay + 40, () =>
            {
                if (!_open || _selected != index || generation != _toolsGeneration) return;
                ApplyToolState();
                ApplyCategoryState();
            });
        }
        else ApplyToolState();
    }

    private static (double Step, double Span) ToolLayout(int count)
    {
        if (count <= 1) return (0, 0);
        double step = Math.Min(ToolStep, MaxToolArc / (count - 1));
        return (step, (count - 1) * step);
    }

    private TextBlock CreateToolLabel(string text, double angle)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Width = ToolLabelWidth,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Text");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        double sin = Math.Sin(angle * Math.PI / 180);
        TextAlignment align = sin > 0.35 ? TextAlignment.Left : sin < -0.35 ? TextAlignment.Right : TextAlignment.Center;
        label.TextAlignment = align;

        label.Measure(new Size(ToolLabelWidth, double.PositiveInfinity));
        double height = Math.Max(15, label.DesiredSize.Height);
        var anchor = Polar(R2 + ToolLabelRadius, angle);
        double left = align switch
        {
            TextAlignment.Left => anchor.X,
            TextAlignment.Right => anchor.X - ToolLabelWidth,
            _ => anchor.X - ToolLabelWidth / 2,
        };
        double top = Math.Clamp(anchor.Y - height / 2, 3, H - height - 3);
        left = Math.Clamp(left, 3, W - ToolLabelWidth - 3);
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        return label;
    }

    private void ActivateTool(ToolVisual tool)
    {
        if (_navigating) return;
        _navigating = true;
        _categoryLayer.IsHitTestVisible = false;
        _toolLayer.IsHitTestVisible = false;
        _core.IsHitTestVisible = false;
        _core.IsTabStop = false;
        foreach (var t in _currentTools)
        {
            t.Button.IsHitTestVisible = false;
            t.Button.IsTabStop = false;
        }

        string page = tool.Data.Page;
        if (!Motion)
        {
            FinishNavigation(page);
            return;
        }
        Tween(_stage, OpacityProperty, _stage.Opacity, 0, 280, 0, EaseOut, () => FinishNavigation(page));
    }

    private void FinishNavigation(string page)
    {
        try { Navigate?.Invoke(page); }
        finally
        {
            _navigating = false;
            Stop(_stage, OpacityProperty);
            _stage.Opacity = 1;
            _core.IsHitTestVisible = true;
            _core.IsTabStop = true;
            if (_open) ResetToCategoriesAfterNavigation();
            else ApplyCollapsedState();
        }
    }

    private void ResetToCategoriesAfterNavigation()
    {
        if (IsKeyboardFocusWithin) _core.Focus();
        ++_toolsGeneration;
        PurgeToolLayers();
        _selected = -1;
        _hoverTool = -1;
        _hoverCategory = -1;
        StopCategoryAnimations();
        StopSectorAnimations();
        _sectorShown = false;
        _sectorFill.Opacity = 0;
        _sectorStroke.Opacity = 0;

        _categoryLayer.IsHitTestVisible = true;
        _toolLayer.IsHitTestVisible = true;
        _toolLabelLayer.IsHitTestVisible = false;
        _core.IsHitTestVisible = true;
        _core.IsTabStop = true;

        foreach (var c in _categories)
        {
            c.Button.IsHitTestVisible = true;
            c.Button.IsTabStop = true;
            c.Button.Opacity = 1;
            c.Scale.ScaleX = c.Scale.ScaleY = 1;
            c.Move.X = c.Move.Y = 0;
            c.Link.Opacity = 0.7;
            c.Link.SetResourceReference(Shape.StrokeProperty, "Line");
            c.Button.ClearValue(Control.ForegroundProperty);
            c.Button.ClearValue(Control.BorderBrushProperty);
        }

        _decor.BeginAnimation(OpacityProperty, null);
        _decor.Opacity = 1;
        UpdateCoreState();
        StartSpin();
        ApplyCategoryState();
    }

    private void CloseTools(bool animate, bool focusCategory)
    {
        int oldSelected = _selected;
        if (oldSelected < 0 && _currentTools.Count == 0) return;

        ++_toolsGeneration;
        var oldTools = _currentTools;
        _currentTools = new List<ToolVisual>();
        _selected = -1;
        _hoverTool = -1;
        HideSector(animate);
        FadeOutToolSet(oldTools, animate);
        ApplyCategoryState();

        if (focusCategory && oldSelected >= 0 && oldSelected < _categories.Count)
            _categories[oldSelected].Button.Focus();
    }

    private void FadeOutToolSet(List<ToolVisual> tools, bool animate, Action? finished = null)
    {
        if (tools.Count == 0)
        {
            finished?.Invoke();
            return;
        }

        bool motion = Motion && animate;
        foreach (var t in tools)
        {
            t.Button.IsHitTestVisible = false;
            t.Button.IsTabStop = false;
            t.Label.IsHitTestVisible = false;
            if (motion)
            {
                Tween(t.Button, OpacityProperty, t.Button.Opacity, 0, 250, 0, EaseOut);
                Tween(t.Scale, ScaleTransform.ScaleXProperty, t.Scale.ScaleX, 0.82, 250, 0, EaseOut);
                Tween(t.Scale, ScaleTransform.ScaleYProperty, t.Scale.ScaleY, 0.82, 250, 0, EaseOut);
                Tween(t.Move, TranslateTransform.XProperty, t.Move.X, t.FromX, 250, 0, EaseOut);
                Tween(t.Move, TranslateTransform.YProperty, t.Move.Y, t.FromY, 250, 0, EaseOut);
                Tween(t.Label, OpacityProperty, t.Label.Opacity, 0, 220, 0, EaseOut);
                Tween(t.Link, OpacityProperty, t.Link.Opacity, 0, 220, 0, EaseOut);
            }
        }

        if (!motion)
        {
            RemoveToolVisuals(tools);
            finished?.Invoke();
            return;
        }

        Later(270, () =>
        {
            RemoveToolVisuals(tools);
            finished?.Invoke();
        });
    }

    private void RemoveToolVisuals(List<ToolVisual> tools)
    {
        foreach (var t in tools)
        {
            _toolLayer.Children.Remove(t.Button);
            _toolLabelLayer.Children.Remove(t.Label);
            _toolLinkLayer.Children.Remove(t.Link);
        }
    }

    private void PurgeToolLayers()
    {
        _currentTools.Clear();
        _toolLayer.Children.Clear();
        _toolLabelLayer.Children.Clear();
        _toolLinkLayer.Children.Clear();
    }

    // ---------------- estados visuais ----------------

    private void ApplyCollapsedState()
    {
        _open = false;
        _navigating = false;
        _selected = -1;
        _hoverCategory = -1;
        _hoverTool = -1;
        ++_toolsGeneration;
        PurgeToolLayers();
        StopSpin();
        StopCategoryAnimations();
        StopSectorAnimations();
        _sectorShown = false;
        _sectorFill.Opacity = 0;
        _sectorStroke.Opacity = 0;

        _categoryLayer.IsHitTestVisible = false;
        _toolLayer.IsHitTestVisible = true;
        _toolLabelLayer.IsHitTestVisible = false;
        foreach (var c in _categories)
        {
            c.Button.IsHitTestVisible = false;
            c.Button.IsTabStop = false;
            c.Button.Opacity = 0;
            c.Button.ClearValue(Control.ForegroundProperty);
            c.Button.ClearValue(Control.BorderBrushProperty);
            c.Scale.ScaleX = c.Scale.ScaleY = 0.82;
            c.Move.X = CX - c.X;
            c.Move.Y = CY - c.Y;
            c.Link.Opacity = 0;
            c.Link.SetResourceReference(Shape.StrokeProperty, "Line");
        }

        _decor.BeginAnimation(OpacityProperty, null);
        _decor.Opacity = 0.4;
        _stage.BeginAnimation(OpacityProperty, null);
        _stage.Opacity = 1;
        _core.IsHitTestVisible = true;
        _core.IsTabStop = true;
        UpdateCoreState();
    }

    private void ShowCategories(bool animate)
    {
        StopCategoryAnimations();
        bool motion = Motion && animate;
        int count = _categories.Count;

        foreach (var c in _categories)
        {
            c.Button.ClearValue(Control.ForegroundProperty);
            c.Button.ClearValue(Control.BorderBrushProperty);
            c.Button.IsHitTestVisible = true;
            c.Button.IsTabStop = true;
            c.Link.SetResourceReference(Shape.StrokeProperty, "Line");
            if (!motion)
            {
                c.Button.Opacity = 1;
                c.Scale.ScaleX = c.Scale.ScaleY = 1;
                c.Move.X = c.Move.Y = 0;
                c.Link.Opacity = 0.7;
            }
        }

        if (!motion)
        {
            PinCategories();
            ApplyCategoryState();
            return;
        }

        for (int i = 0; i < count; i++)
        {
            var c = _categories[i];
            int delay = i * 25;
            Tween(c.Button, OpacityProperty, 0, 1, 420, delay);
            Tween(c.Scale, ScaleTransform.ScaleXProperty, 0.82, 1, 480, delay);
            Tween(c.Scale, ScaleTransform.ScaleYProperty, 0.82, 1, 480, delay);
            Tween(c.Move, TranslateTransform.XProperty, CX - c.X, 0, 480, delay);
            Tween(c.Move, TranslateTransform.YProperty, CY - c.Y, 0, 480, delay);
            Tween(c.Link, OpacityProperty, 0, 0.7, 340, 100 + delay);
        }

        Later((count - 1) * 25 + 560, () =>
        {
            if (!_open) return;
            PinCategories();
            ApplyCategoryState();
        });
    }

    /// <summary>
    /// Garante o estado final dos nós (opacidade 1, escala 1, sem deslocamento).
    /// Sem isso, um nó cujo tween termine sem confirmar o valor fica preso no centro, atrás do núcleo.
    /// </summary>
    private void PinCategories()
    {
        foreach (var c in _categories)
        {
            c.Button.BeginAnimation(OpacityProperty, null);
            c.Button.Opacity = 1;
            c.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            c.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            c.Scale.ScaleX = c.Scale.ScaleY = 1;
            c.Move.BeginAnimation(TranslateTransform.XProperty, null);
            c.Move.BeginAnimation(TranslateTransform.YProperty, null);
            c.Move.X = c.Move.Y = 0;
            c.Link.Opacity = 0.7;
        }
    }

    private void SetHoverCategory(int index)
    {
        if (!_open || _hoverCategory == index) return;
        _hoverCategory = index;
        ApplyCategoryState();
    }

    private void SetHoverTool(int index)
    {
        if (!_open || _selected < 0 || _hoverTool == index) return;
        _hoverTool = index;
        ApplyToolState();
    }

    private void ApplyCategoryState()
    {
        if (!_open) return;
        bool toolsOpen = _selected >= 0;
        for (int i = 0; i < _categories.Count; i++)
        {
            var c = _categories[i];
            bool selected = i == _selected;
            bool hovered = i == _hoverCategory;
            bool lit = selected || hovered;
            bool dim = toolsOpen ? !lit : (_hoverCategory >= 0 && !hovered);

            c.Button.Opacity = dim ? 0.4 : 1;
            if (selected)
            {
                c.Button.SetResourceReference(Control.ForegroundProperty, "AccentText");
                c.Button.SetResourceReference(Control.BorderBrushProperty, "Accent");
            }
            else
            {
                c.Button.ClearValue(Control.ForegroundProperty);
                c.Button.ClearValue(Control.BorderBrushProperty);
            }

            c.Link.SetResourceReference(Shape.StrokeProperty, lit ? "Accent" : "Line");
            c.Link.Opacity = lit ? 1 : dim ? 0.25 : 0.7;
        }
    }

    private void ApplyToolState()
    {
        if (!_open || _selected < 0) return;
        bool anyHover = _hoverTool >= 0;
        foreach (var t in _currentTools)
        {
            bool hovered = t.Index == _hoverTool;
            bool dim = anyHover && !hovered;
            t.Button.Opacity = dim ? 0.4 : 1;
            t.Label.Opacity = dim ? 0.4 : 1;
            t.Label.SetResourceReference(TextBlock.ForegroundProperty, hovered ? "AccentText" : "Muted");
            if (hovered)
            {
                t.Button.SetResourceReference(Control.ForegroundProperty, "AccentText");
                t.Button.SetResourceReference(Control.BorderBrushProperty, "Accent");
            }
            else
            {
                t.Button.ClearValue(Control.ForegroundProperty);
                t.Button.ClearValue(Control.BorderBrushProperty);
            }
            t.Link.SetResourceReference(Shape.StrokeProperty, hovered ? "Accent" : "LineStrong");
            t.Link.Opacity = hovered ? 0.95 : dim ? 0.12 : 0.5;
        }
    }

    private void ShowSector(int index, bool animate)
    {
        if (index < 0 || index >= _categories.Count) return;
        var data = AppCatalog.Categories[index];
        int count = data.Tools.Count;
        var (_, span) = ToolLayout(count);
        double margin = count > 1 ? 10 : 26;
        var geometry = SectorGeometry(SectorInner, SectorOuter, _categories[index].Angle, span, margin);
        _sectorFill.Data = geometry;
        _sectorStroke.Data = geometry;

        bool wasShown = _sectorShown;
        _sectorShown = true;
        if (animate && Motion && !wasShown)
        {
            Tween(_sectorFill, OpacityProperty, 0, 0.05, 400);
            Tween(_sectorStroke, OpacityProperty, 0, 0.20, 400);
        }
        else
        {
            SetOpacity(_sectorFill, 0.05);
            SetOpacity(_sectorStroke, 0.20);
        }
    }

    private void HideSector(bool animate)
    {
        _sectorShown = false;
        if (animate && Motion && (_sectorFill.Opacity > 0.001 || _sectorStroke.Opacity > 0.001))
        {
            Tween(_sectorFill, OpacityProperty, _sectorFill.Opacity, 0, 250);
            Tween(_sectorStroke, OpacityProperty, _sectorStroke.Opacity, 0, 250);
        }
        else
        {
            SetOpacity(_sectorFill, 0);
            SetOpacity(_sectorStroke, 0);
        }
    }

    private void UpdateCoreState()
    {
        _coreText.Text = _open ? "RECOLHER" : "ABRIR FERRAMENTAS";
        _core.ToolTip = _open ? "Recolher (Esc)" : "Abrir ferramentas";
        AutomationProperties.SetName(_core, _open
            ? "Recolher ferramentas do Fort Knox"
            : "Abrir ferramentas do Fort Knox");
    }

    // ---------------- acessibilidade ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _navigating) return;
        if (_selected >= 0)
        {
            CloseTools(animate: true, focusCategory: true);
            e.Handled = true;
        }
        else if (_open)
        {
            Collapse();
            e.Handled = true;
        }
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            if (_open) StartSpin();
            return;
        }

        StopSpin();
        // Ao sair da Home por qualquer caminho (menu lateral ou ferramenta), ela volta no nível das categorias.
        if (_open) ResetToCategoriesAfterNavigation();
    }

    // ---------------- anéis decorativos ----------------

    private void StartSpin()
    {
        if (_spinning || !Motion || !_open || !IsVisible) return;
        _spinning = true;
        var ticks = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60)) { RepeatBehavior = RepeatBehavior.Forever };
        var arcs = new DoubleAnimation(360, 0, TimeSpan.FromSeconds(90)) { RepeatBehavior = RepeatBehavior.Forever };
        _ticksSpin.BeginAnimation(RotateTransform.AngleProperty, ticks);
        _arcsSpin.BeginAnimation(RotateTransform.AngleProperty, arcs);
    }

    private void StopSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        _ticksSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        _arcsSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        _ticksSpin.Angle = 0;
        _arcsSpin.Angle = 0;
    }

    // ---------------- animação ----------------

    private void Later(int milliseconds, Action action)
    {
        if (milliseconds <= 0)
        {
            action();
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static void Tween(DependencyObject target, DependencyProperty property, double from, double to,
                              int milliseconds, int delay = 0, IEasingFunction? easing = null, Action? completed = null)
    {
        if (target is not IAnimatable animatable) return;
        if (!Motion || milliseconds <= 0)
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            completed?.Invoke();
            return;
        }

        target.SetValue(property, from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = delay > 0 ? TimeSpan.FromMilliseconds(delay) : null,
            EasingFunction = easing ?? EaseOut,
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            target.SetValue(property, to);
            completed?.Invoke();
        };
        animatable.BeginAnimation(property, animation);
    }

    private static void Stop(DependencyObject target, DependencyProperty property)
    {
        if (target is IAnimatable animatable) animatable.BeginAnimation(property, null);
    }

    private void StopCategoryAnimations()
    {
        foreach (var c in _categories)
        {
            Stop(c.Button, OpacityProperty);
            Stop(c.Scale, ScaleTransform.ScaleXProperty);
            Stop(c.Scale, ScaleTransform.ScaleYProperty);
            Stop(c.Move, TranslateTransform.XProperty);
            Stop(c.Move, TranslateTransform.YProperty);
            Stop(c.Link, OpacityProperty);
        }
    }

    private void StopSectorAnimations()
    {
        Stop(_sectorFill, OpacityProperty);
        Stop(_sectorStroke, OpacityProperty);
    }

    private static void SetOpacity(DependencyObject target, double opacity)
    {
        Stop(target, OpacityProperty);
        target.SetValue(OpacityProperty, opacity);
    }

    // ---------------- geometria ----------------

    private static (double X, double Y) Polar(double radius, double degrees)
    {
        double angle = (degrees - 90) * Math.PI / 180;
        return (CX + radius * Math.Cos(angle), CY + radius * Math.Sin(angle));
    }

    private static Point PolarPoint(double radius, double degrees)
    {
        var (x, y) = Polar(radius, degrees);
        return new Point(x, y);
    }

    private static Geometry ArcGeometry(double radius, double startDegrees, double endDegrees)
    {
        var start = PolarPoint(radius, startDegrees);
        var end = PolarPoint(radius, endDegrees);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, endDegrees - startDegrees > 180,
                          SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry SectorGeometry(double inner, double outer, double centerDegrees,
                                           double span, double margin)
    {
        double start = centerDegrees - span / 2 - margin;
        double end = centerDegrees + span / 2 + margin;
        bool large = end - start > 180;
        var p0 = PolarPoint(outer, start);
        var p1 = PolarPoint(outer, end);
        var p2 = PolarPoint(inner, end);
        var p3 = PolarPoint(inner, start);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(p0, true, true);
            context.ArcTo(p1, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            context.LineTo(p2, true, false);
            context.ArcTo(p3, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CurveGeometry(Point start, Point control, Point end)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.QuadraticBezierTo(control, end, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }
}
