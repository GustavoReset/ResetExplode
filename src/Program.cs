using Avalonia;

namespace ResetExplode;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--report"))
        {
            var mon = new SensorMonitor(); mon.Update(); Thread.Sleep(1000); mon.Update();
            Console.WriteLine(Report.Build(SystemInfoCollector.Collect(), mon.Rows, BatteryInfo.Read().Concat(DiskHealth.Read())));
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
