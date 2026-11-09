using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FortKnox.UI;

/// <summary>
/// Moldura com cantos chanfrados a 45° (Brandbook v3: geometria industrial, sem cantos arredondados).
/// Substitui o <see cref="Border"/> em cartões, botões, campos e selos. Corta o canto superior esquerdo e o
/// inferior direito (o mesmo recorte do símbolo e dos componentes HTML); <see cref="AllCorners"/> corta os quatro.
/// Desenha fundo e contorno no mesmo polígono, então a borda acompanha a diagonal, e recorta o conteúdo nele.
/// </summary>
public class ChamferBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    /// <summary>Espessura do contorno. Thickness para trocar um Border sem mudar o XAML; o traço é uniforme (usa o lado esquerdo).</summary>
    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Tamanho do chanfro em px. Cartão 12, controle 6, selo 4.</summary>
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ChamferBorder)d).UpdateClip()));

    public static readonly DependencyProperty AllCornersProperty = DependencyProperty.Register(
        nameof(AllCorners), typeof(bool), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ChamferBorder)d).UpdateClip()));

    /// <summary>Recorta o conteúdo no polígono (padrão). Desligue quando um filho precisa vazar (ex.: dica posicionada).</summary>
    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(true, (d, _) => ((ChamferBorder)d).UpdateClip()));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Thickness BorderThickness { get => (Thickness)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    private double Stroke => BorderThickness.Left;
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public bool AllCorners { get => (bool)GetValue(AllCornersProperty); set => SetValue(AllCornersProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    /// <summary>Polígono chanfrado num retângulo (usado também por outros controles da marca).</summary>
    public static StreamGeometry Shape(Rect r, double cut, bool all)
    {
        double c = Math.Max(0, Math.Min(cut, Math.Min(r.Width, r.Height) / 2));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + c, r.Top), true, true);
            if (all)
            {
                ctx.LineTo(new Point(r.Right - c, r.Top), true, false);
                ctx.LineTo(new Point(r.Right, r.Top + c), true, false);
            }
            else ctx.LineTo(new Point(r.Right, r.Top), true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - c), true, false);
            ctx.LineTo(new Point(r.Right - c, r.Bottom), true, false);
            if (all)
            {
                ctx.LineTo(new Point(r.Left + c, r.Bottom), true, false);
                ctx.LineTo(new Point(r.Left, r.Bottom - c), true, false);
            }
            else ctx.LineTo(new Point(r.Left, r.Bottom), true, false);
            ctx.LineTo(new Point(r.Left, r.Top + c), true, false);
        }
        g.Freeze();
        return g;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double b = Stroke * 2;
        var p = Padding;
        var inner = new Size(Math.Max(0, constraint.Width - b - p.Left - p.Right), Math.Max(0, constraint.Height - b - p.Top - p.Bottom));
        var child = Child;
        Size desired = default;
        if (child != null) { child.Measure(inner); desired = child.DesiredSize; }
        return new Size(desired.Width + b + p.Left + p.Right, desired.Height + b + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double t = Stroke;
        var p = Padding;
        Child?.Arrange(new Rect(t + p.Left, t + p.Top,
            Math.Max(0, arrangeSize.Width - 2 * t - p.Left - p.Right), Math.Max(0, arrangeSize.Height - 2 * t - p.Top - p.Bottom)));
        return arrangeSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    private void UpdateClip()
    {
        // Recorte no polígono externo: o contorno é desenhado por dentro, então não é cortado.
        Clip = ClipContent && ActualWidth > 0 ? Shape(new Rect(0, 0, ActualWidth, ActualHeight), Cut, AllCorners) : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double t = Stroke;
        var pen = t > 0 && BorderBrush != null ? new Pen(BorderBrush, t) : null;
        // Meio pixel para dentro: o traço cai inteiro dentro da peça (nítido e sem ser recortado)
        var r = pen != null ? new Rect(t / 2, t / 2, Math.Max(0, w - t), Math.Max(0, h - t)) : new Rect(0, 0, w, h);
        dc.DrawGeometry(Background, pen, Shape(r, Math.Max(0, Cut - t / 2), AllCorners));
    }
}
