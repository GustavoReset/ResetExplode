using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FortKnox.UI;

public enum DialogKind { Info, Confirm, Destructive }

/// <summary>
/// Cartão de diálogo (título, mensagem, botões). Usado pelo FkDialog e no catálogo.
/// Destructive: botão vermelho e o foco começa em Cancelar (Enter não apaga nada por engano).
/// </summary>
public class DialogCard : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(DialogCard), new PropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(DialogCard), new PropertyMetadata(""));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DialogKind), typeof(DialogCard), new PropertyMetadata(DialogKind.Confirm));
    public static readonly DependencyProperty ConfirmTextProperty = DependencyProperty.Register(nameof(ConfirmText), typeof(string), typeof(DialogCard), new PropertyMetadata("Confirmar"));
    public static readonly DependencyProperty CancelTextProperty = DependencyProperty.Register(nameof(CancelText), typeof(string), typeof(DialogCard), new PropertyMetadata("Cancelar"));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public DialogKind Kind { get => (DialogKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string ConfirmText { get => (string)GetValue(ConfirmTextProperty); set => SetValue(ConfirmTextProperty, value); }
    public string CancelText { get => (string)GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }

    public event Action<bool>? Answered;
    private Button? _confirm, _cancel;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_confirm != null) _confirm.Click -= OnConfirm;
        if (_cancel != null) _cancel.Click -= OnCancel;
        _confirm = GetTemplateChild("PART_Confirm") as Button;
        _cancel = GetTemplateChild("PART_Cancel") as Button;
        if (_confirm != null) _confirm.Click += OnConfirm;
        if (_cancel != null) _cancel.Click += OnCancel;
    }

    private void OnConfirm(object s, RoutedEventArgs e) => Answered?.Invoke(true);
    private void OnCancel(object s, RoutedEventArgs e) => Answered?.Invoke(false);

    public void FocusDefault() => (Kind == DialogKind.Destructive ? _cancel : _confirm)?.Focus();
}

/// <summary>
/// Diálogo modal no visual Fort Knox, no lugar do MessageBox do Windows.
/// <code>if (FkDialog.Confirm(this, "Remover o driver CH340 4.0?", "Guardamos uma cópia…", "Remover", kind: DialogKind.Destructive)) …</code>
/// </summary>
public sealed class FkDialog : Window
{
    private bool _result;

    private FkDialog(Window? owner, DialogCard card)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = card.Title;
        if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        card.Margin = new Thickness(24); // espaço para a sombra
        card.Answered += ok => { _result = ok; Close(); };
        Content = card;
        Loaded += (_, _) => card.FocusDefault();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = false; Close(); } };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Confirmar",
                               string cancelText = "Cancelar", DialogKind kind = DialogKind.Confirm)
    {
        var d = new FkDialog(owner, new DialogCard { Title = title, Message = message, ConfirmText = confirmText, CancelText = cancelText, Kind = kind });
        d.ShowDialog();
        return d._result;
    }

    public static void Info(Window? owner, string title, string message, string okText = "Entendi")
        => Confirm(owner, title, message, okText, "", DialogKind.Info);
}
