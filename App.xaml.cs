using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace FortKnox;

public partial class App : Application
{
    // Uma única instância: abrir o Fort Knox de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catálogo do Design System: FortKnox.exe --catalogo [--claro] [--png <pasta>]. Não abre o app nem lê hardware.
        int cat = Array.IndexOf(e.Args, "--catalogo");
        if (cat >= 0)
        {
            base.OnStartup(e);
            int png = Array.IndexOf(e.Args, "--png");
            ThemeManager.Apply(e.Args.Contains("--claro"));
            new UI.CatalogWindow(png >= 0 && png + 1 < e.Args.Length ? e.Args[png + 1] : null).Show();
            return;
        }

        bool first;
        try { _single = new Mutex(true, @"Local\FortKnox.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            IntPtr h = FindWindow(null, "Fort Knox");
            if (h != IntPtr.Zero)
            {
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
