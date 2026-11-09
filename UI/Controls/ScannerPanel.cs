using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FortKnox.UI;

public enum ScanState { Idle, Running, Done, Cancelled, Error }

/// <summary>Um achado de varredura (MURALHA, Limpeza, Desinstalar, ALICERCE…). Motivo sempre em linguagem clara.</summary>
public sealed record ScanFinding(string Name, string Detail, HealthStatus Status);

/// <summary>
/// Painel de varredura padrão do Fort Knox: radar com o símbolo Bastião, fase, item atual, progresso, contadores e achados ao vivo.
/// Só apresentação: quem varre é o serviço do módulo, que atualiza estas propriedades (via ViewModel) e reage aos comandos.
/// </summary>
public class ScannerPanel : Control
{
    private RotateTransform? _sweep;

    public static readonly DependencyProperty TitleProperty = Reg(nameof(Title), typeof(string), "VARREDURA");
    public static readonly DependencyProperty PhaseProperty = Reg(nameof(Phase), typeof(string), "Pronto para verificar");
    public static readonly DependencyProperty CurrentItemProperty = Reg(nameof(CurrentItem), typeof(string), "");
    /// <summary>0 a 1; NaN = indeterminado.</summary>
    public static readonly DependencyProperty ProgressProperty = Reg(nameof(Progress), typeof(double), 0.0);
    public static readonly DependencyProperty ScannedCountProperty = Reg(nameof(ScannedCount), typeof(long), 0L);
    public static readonly DependencyProperty FindingsCountProperty = Reg(nameof(FindingsCount), typeof(int), 0);
    public static readonly DependencyProperty ElapsedProperty = Reg(nameof(Elapsed), typeof(TimeSpan), TimeSpan.Zero);
    public static readonly DependencyProperty StateProperty = Reg(nameof(State), typeof(ScanState), ScanState.Idle);
    public static readonly DependencyProperty FindingsProperty = DependencyProperty.Register(nameof(Findings), typeof(IEnumerable), typeof(ScannerPanel));
    public static readonly DependencyProperty StartCommandProperty = DependencyProperty.Register(nameof(StartCommand), typeof(ICommand), typeof(ScannerPanel));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(ScannerPanel));
    public static readonly DependencyProperty StartTextProperty = DependencyProperty.Register(nameof(StartText), typeof(string), typeof(ScannerPanel), new PropertyMetadata("Verificar agora"));

    private static readonly DependencyPropertyKey ScannedTextKey = RegRo(nameof(ScannedText), "0");
    private static readonly DependencyPropertyKey FindingsTextKey = RegRo(nameof(FindingsText), "0");
    private static readonly DependencyPropertyKey ElapsedTextKey = RegRo(nameof(ElapsedText), "00:00");
    private static readonly DependencyPropertyKey PercentTextKey = RegRo(nameof(PercentText), "");
    private static readonly DependencyPropertyKey IsIndeterminateKey = DependencyProperty.RegisterReadOnly(
        nameof(IsIndeterminate), typeof(bool), typeof(ScannerPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty ScannedTextProperty = ScannedTextKey.DependencyProperty;
    public static readonly DependencyProperty FindingsTextProperty = FindingsTextKey.DependencyProperty;
    public static readonly DependencyProperty ElapsedTextProperty = ElapsedTextKey.DependencyProperty;
    public static readonly DependencyProperty PercentTextProperty = PercentTextKey.DependencyProperty;
    public static readonly DependencyProperty IsIndeterminateProperty = IsIndeterminateKey.DependencyProperty;
    private static readonly DependencyPropertyKey BarValueKey = DependencyProperty.RegisterReadOnly(
        nameof(BarValue), typeof(double), typeof(ScannerPanel), new PropertyMetadata(0.0));
    /// <summary>Progress já limitado a 0..1 (a ProgressBar não aceita NaN).</summary>
    public static readonly DependencyProperty BarValueProperty = BarValueKey.DependencyProperty;
    public double BarValue => (double)GetValue(BarValueProperty);

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Phase { get => (string)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public string CurrentItem { get => (string)GetValue(CurrentItemProperty); set => SetValue(CurrentItemProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public long ScannedCount { get => (long)GetValue(ScannedCountProperty); set => SetValue(ScannedCountProperty, value); }
    public int FindingsCount { get => (int)GetValue(FindingsCountProperty); set => SetValue(FindingsCountProperty, value); }
    public TimeSpan Elapsed { get => (TimeSpan)GetValue(ElapsedProperty); set => SetValue(ElapsedProperty, value); }
    public ScanState State { get => (ScanState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public IEnumerable? Findings { get => (IEnumerable?)GetValue(FindingsProperty); set => SetValue(FindingsProperty, value); }
    public ICommand? StartCommand { get => (ICommand?)GetValue(StartCommandProperty); set => SetValue(StartCommandProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public string StartText { get => (string)GetValue(StartTextProperty); set => SetValue(StartTextProperty, value); }
    public string ScannedText => (string)GetValue(ScannedTextProperty);
    public string FindingsText => (string)GetValue(FindingsTextProperty);
    public string ElapsedText => (string)GetValue(ElapsedTextProperty);
    public string PercentText => (string)GetValue(PercentTextProperty);
    public bool IsIndeterminate => (bool)GetValue(IsIndeterminateProperty);

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static DependencyProperty Reg(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(ScannerPanel), new PropertyMetadata(def, (d, _) => ((ScannerPanel)d).Refresh()));
    private static DependencyPropertyKey RegRo(string name, string def) =>
        DependencyProperty.RegisterReadOnly(name, typeof(string), typeof(ScannerPanel), new PropertyMetadata(def));

    private void Refresh()
    {
        SetValue(ScannedTextKey, ScannedCount.ToString("N0", PtBr));      // padrão brasileiro: ponto de milhar
        SetValue(FindingsTextKey, FindingsCount.ToString("N0", PtBr));
        var e = Elapsed;
        SetValue(ElapsedTextKey, e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss"));
        bool indet = double.IsNaN(Progress);
        SetValue(IsIndeterminateKey, indet);
        SetValue(BarValueKey, indet ? 0.0 : Math.Clamp(Progress, 0, 1));
        SetValue(PercentTextKey, indet || State == ScanState.Idle ? "" : $"{Math.Clamp(Progress, 0, 1) * 100:0} %");
        UpdateSweep();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _sweep = GetTemplateChild("PART_Sweep") is FrameworkElement f ? f.RenderTransform as RotateTransform : null;
        UpdateSweep();
    }

    /// <summary>O feixe do radar gira só enquanto varre e só se o Windows estiver com "Efeitos de animação" ligados.</summary>
    private void UpdateSweep()
    {
        if (_sweep == null || _sweep.IsFrozen) return;
        if (State == ScanState.Running && SystemParameters.ClientAreaAnimation)
        {
            if (_sweep.HasAnimatedProperties) return;
            _sweep.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _sweep.BeginAnimation(RotateTransform.AngleProperty, null);
            _sweep.Angle = 45;
        }
    }
}
