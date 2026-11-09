using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FortKnox.UI;

/// <summary>As mesmas palavras de status em todo o app (Tom de voz v3): Bom, Atenção, Ruim. Info e Neutro para o resto.</summary>
public enum HealthStatus { Neutro, Info, Bom, Atencao, Ruim }

public static class HealthStatusText
{
    public static string Of(HealthStatus s) => s switch
    {
        HealthStatus.Bom => "Bom",
        HealthStatus.Atencao => "Atenção",
        HealthStatus.Ruim => "Ruim",
        HealthStatus.Info => "Info",
        _ => "—",
    };
}

/// <summary>Selo de status: ponto + palavra, nas cores de status da marca. Text vazio = palavra padrão do status.</summary>
public class StatusBadge : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(HealthStatus), typeof(StatusBadge),
        new FrameworkPropertyMetadata(HealthStatus.Neutro, (d, _) => ((StatusBadge)d).UpdateText()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusBadge),
        new FrameworkPropertyMetadata(null, (d, _) => ((StatusBadge)d).UpdateText()));

    private static readonly DependencyPropertyKey DisplayTextKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(StatusBadge), new PropertyMetadata("—"));
    public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

    public HealthStatus Status { get => (HealthStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    private void UpdateText() => SetValue(DisplayTextKey, string.IsNullOrEmpty(Text) ? HealthStatusText.Of(Status) : Text);
}

/// <summary>Estado vazio: ícone, título, explicação e (opcional) uma ação. Ex.: "Nenhum jogo encontrado ainda."</summary>
public class EmptyState : Control
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyState));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
}

/// <summary>ICommand mínimo para ViewModels e controles (sem pacote de terceiros).</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run) : this(_ => run()) { }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _run(p);
}
