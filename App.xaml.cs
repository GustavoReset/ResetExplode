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

        // Núcleo (Fase 3.1): log, barramento, estado, trabalhos, preferências por módulo e Diário de Ferrolhos.
        // Abre em milissegundos; os módulos passam a usá-lo a partir da Fase 3.2. Nada muda na tela.
        Core = new global::FortKnox.Core.FortKnoxHost(AppSettings.Folder, SynchronizationContext.Current);
        Core.Log.Info($"Fort Knox {typeof(App).Assembly.GetName().Version} iniciado");
        DispatcherUnhandledException += (_, ex) => Core.Log.Error("Exceção não tratada na interface", ex.Exception);

        new MainWindow().Show();
    }

    /// <summary>Núcleo do app. Null só no modo --catalogo.</summary>
    public static global::FortKnox.Core.FortKnoxHost? Core { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancela trabalhos e encerra módulos (até 3 s) antes de o processo terminar.
        if (Core is { } core) Task.Run(() => core.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        base.OnExit(e);
    }
}
