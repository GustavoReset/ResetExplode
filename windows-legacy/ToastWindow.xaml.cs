using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Aviso no canto inferior direito da tela (dispositivo conectado/removido). Não rouba o foco.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(5) };

    public ToastWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x8000000); // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };
        SizeChanged += (_, _) => Place();
        _hide.Tick += (_, _) => { _hide.Stop(); Fade(0, () => Hide()); };
    }

    /// <summary>Mostra (ou atualiza, se já visível) o aviso por 5 s. icon: glifo Segoe MDL2.</summary>
    public void Notify(string title, string text, string icon)
    {
        TTitle.Text = title;
        TText.Text = text;
        TIcon.Text = icon;
        if (!IsVisible) { Show(); Place(); }
        Fade(1, null);
        _hide.Stop();
        _hide.Start();
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
    }

    private void Fade(double to, Action? done)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(180));
        if (done != null) anim.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, anim);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
}
