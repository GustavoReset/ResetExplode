using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FortKnox.UI;

/// <summary>
/// Indicador de atividade da marca (versão nativa do componente "radar/anéis" da identidade).
/// O símbolo Bastião fica FIXO no centro; só os elementos secundários se movem: um anel tracejado gira devagar
/// e um feixe ouro percorre a órbita. Gira apenas com <see cref="IsActive"/> = true, com a peça visível e com
/// "Efeitos de animação" do Windows ligados. Parado, mostra os anéis estáticos (ou nada, em <see cref="HideWhenIdle"/>).
/// Abaixo de 28 px vira o indicador compacto (só órbita + feixe, sem símbolo) para linhas de status.
/// </summary>
public class FkActivity : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(FkActivity), new FrameworkPropertyMetadata(false, (d, _) => ((FkActivity)d).Sync()));

    /// <summary>Cor do feixe: ouro (padrão, foco/execução); Info para rede; Ok/Warn para veredito.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(FkActivity), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HideWhenIdleProperty = DependencyProperty.Register(
        nameof(HideWhenIdle), typeof(bool), typeof(FkActivity), new FrameworkPropertyMetadata(false, (d, _) => ((FkActivity)d).Sync()));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool HideWhenIdle { get => (bool)GetValue(HideWhenIdleProperty); set => SetValue(HideWhenIdleProperty, value); }

    // Ângulos animados (render-only): anel lento e feixe
    private static readonly DependencyProperty RingAngleProperty = DependencyProperty.Register(
        "RingAngle", typeof(double), typeof(FkActivity), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty BeamAngleProperty = DependencyProperty.Register(
        "BeamAngle", typeof(double), typeof(FkActivity), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _running;

    public FkActivity()
    {
        Width = 64; Height = 64;
        IsHitTestVisible = false;
        Focusable = false;
        IsVisibleChanged += (_, _) => Sync();
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
    }

    private void Sync()
    {
        bool run = IsActive && IsVisible && SystemParameters.ClientAreaAnimation;
        if (run && !_running)
        {
            _running = true;
            BeginAnimation(RingAngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });
            BeginAnimation(BeamAngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (!run && _running) Stop();
        InvalidateVisual();
    }

    private void Stop()
    {
        _running = false;
        BeginAnimation(RingAngleProperty, null);
        BeginAnimation(BeamAngleProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        bool active = IsActive;
        if (!active && HideWhenIdle) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var line = TryFindResource("LineStrong") as Brush ?? Brushes.Gray;
        var accent = Accent ?? TryFindResource("Accent") as Brush ?? Brushes.Goldenrod;
        bool compact = s < 28;
        double r = s / 2 - (compact ? 1.5 : 2);
        double ringAngle = (double)GetValue(RingAngleProperty), beamAngle = (double)GetValue(BeamAngleProperty);

        // Órbita externa tracejada (gira devagar só durante o processo)
        var dash = new Pen(line, compact ? 1.5 : 1) { DashStyle = compact ? DashStyles.Solid : new DashStyle(new double[] { 2, 3 }, 0) };
        dc.PushTransform(new RotateTransform(ringAngle, c.X, c.Y));
        dc.DrawEllipse(null, dash, c, r, r);
        dc.Pop();

        if (!compact)
        {
            // Anel interno fino e estático: moldura do símbolo
            dc.DrawEllipse(null, new Pen(line, 1), c, r * 0.68, r * 0.68);
            // Símbolo FIXO no centro (nunca gira nem pulsa)
            if (TryFindResource("BrandSymbol") is ImageSource sym)
            {
                double sw = s * 0.34, sh = sw * 1.045;
                dc.DrawImage(sym, new Rect(c.X - sw / 2, c.Y - sh / 2, sw, sh));
            }
        }

        if (!active) return;
        // Feixe: arco de 70° em ouro com cauda (dois traços de opacidade decrescente)
        dc.PushTransform(new RotateTransform(beamAngle, c.X, c.Y));
        DrawArc(dc, c, r, -70, 0, new Pen(accent, compact ? 2 : 2.2) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Square });
        dc.PushOpacity(0.35);
        DrawArc(dc, c, r, -130, -70, new Pen(accent, compact ? 2 : 2.2));
        dc.Pop();
        if (!compact) dc.DrawEllipse(accent, null, Polar(c, r, 0), 2.4, 2.4);
        dc.Pop();
    }

    private static Point Polar(Point c, double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double from, double to, Pen pen)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(c, r, from), false, false);
            ctx.ArcTo(Polar(c, r, to), new Size(r, r), 0, Math.Abs(to - from) > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>
/// Linha de status com indicador embutido. Substitui o TextBlock de status das telas mantendo a mesma API
/// (<c>Status.Text = "Lendo os discos…"</c>): enquanto o texto termina em reticências o processo está ativo e o
/// indicador compacto gira; quando a frase vira resultado, o indicador some. <see cref="IsBusy"/> força o estado.
/// </summary>
public class StatusLine : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusLine), new FrameworkPropertyMetadata("", (d, _) => ((StatusLine)d).Update()));
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool?), typeof(StatusLine), new FrameworkPropertyMetadata(null, (d, _) => ((StatusLine)d).Update()));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(StatusLine), new PropertyMetadata(TextWrapping.Wrap));
    private static readonly DependencyPropertyKey ActiveKey = DependencyProperty.RegisterReadOnly(
        nameof(Active), typeof(bool), typeof(StatusLine), new PropertyMetadata(false));
    public static readonly DependencyProperty ActiveProperty = ActiveKey.DependencyProperty;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value ?? ""); }
    /// <summary>null = automático (texto terminando em "…").</summary>
    public bool? IsBusy { get => (bool?)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public bool Active => (bool)GetValue(ActiveProperty);

    static StatusLine()
    {
        FocusableProperty.OverrideMetadata(typeof(StatusLine), new FrameworkPropertyMetadata(false));
    }

    private void Update()
    {
        var t = Text?.TrimEnd() ?? "";
        SetValue(ActiveKey, IsBusy ?? (t.EndsWith('…') || t.EndsWith("...")));
    }
}
