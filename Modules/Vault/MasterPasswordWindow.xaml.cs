using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FortKnox;

/// <summary>Pede a senha mestra do COFRE. 5 erros seguidos bloqueiam novas tentativas por 30 s (vale até fechar o app).</summary>
public partial class MasterPasswordWindow : Window
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private static int _failures;
    private static DateTime _blockedUntil = DateTime.MinValue;

    private bool _ok;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private MasterPasswordWindow(Window? owner)
    {
        InitializeComponent();
        if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DevNote.Visibility = MasterPassword.IsDevPassword ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        _tick.Tick += (_, _) => UpdateBlock();
        Loaded += (_, _) => { UpdateBlock(); Pwd.Focus(); };
        Closed += (_, _) => { _tick.Stop(); Pwd.Clear(); };
    }

    /// <summary>true = senha correta. Chamar na thread da interface.</summary>
    public static bool Ask(Window? owner)
    {
        var w = new MasterPasswordWindow(owner);
        w.ShowDialog();
        return w._ok;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => TryOpen();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Pwd_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TryOpen(); }

    private void TryOpen()
    {
        if (DateTime.UtcNow < _blockedUntil) return;
        if (MasterPassword.Verify(Pwd.Password))
        {
            _failures = 0;
            _ok = true;
            Close();
            return;
        }
        _failures++;
        Pwd.Clear();
        if (_failures >= MaxAttempts)
        {
            _failures = 0;
            _blockedUntil = DateTime.UtcNow + Cooldown;
            UpdateBlock();
        }
        else ShowError($"Senha incorreta. Restam {MaxAttempts - _failures} tentativa(s) antes de uma pausa de 30 s.");
        Pwd.Focus();
    }

    private void UpdateBlock()
    {
        var left = _blockedUntil - DateTime.UtcNow;
        bool blocked = left > TimeSpan.Zero;
        Pwd.IsEnabled = BtnOpen.IsEnabled = !blocked;
        if (blocked)
        {
            ShowError($"Muitas tentativas erradas. Tente de novo em {Math.Ceiling(left.TotalSeconds):0} s.");
            _tick.Start();
        }
        else if (_tick.IsEnabled)
        {
            _tick.Stop();
            Error.Visibility = Visibility.Collapsed;
            Pwd.Focus();
        }
    }

    private void ShowError(string text) { Error.Text = text; Error.Visibility = Visibility.Visible; }
}
