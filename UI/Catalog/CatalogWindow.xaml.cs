using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FortKnox.UI;

/// <summary>
/// Catálogo vivo do Design System (FortKnox.exe --catalogo). Mostra cada componente base nos dois temas.
/// Com --png &lt;pasta&gt; grava catalogo-escuro.png e catalogo-claro.png e fecha (usado para revisão e documentação).
/// </summary>
public partial class CatalogWindow : Window
{
    private readonly string? _pngDir;
    private readonly ObservableCollection<ScanFinding> _findings = new();
    private readonly DispatcherTimer _demo = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _tick;

    public sealed record Swatch(string Name, string Token, Brush Brush);
    public sealed record DemoApp(string Name, string Publisher, string Size, HealthStatus Status, string StatusText);

    public CatalogWindow(string? pngDir = null)
    {
        _pngDir = pngDir;
        InitializeComponent();
        (ThemeManager.IsLight ? LightChip : DarkChip).IsChecked = true;
        LoadSwatches();

        DemoGrid.ItemsSource = new[]
        {
            new DemoApp("7-Zip 24.08", "Igor Pavlov", "5,6 MB", HealthStatus.Bom, "Assinado"),
            new DemoApp("Steam", "Valve Corporation", "1,2 GB", HealthStatus.Bom, "Assinado"),
            new DemoApp("Ferramenta antiga.exe", "desconhecido", "312 KB", HealthStatus.Atencao, "Sem assinatura"),
            new DemoApp("Atualizador de driver genérico", "desconhecido", "48,9 MB", HealthStatus.Ruim, "Assinatura inválida"),
        };

        DemoScanner.Findings = _findings;
        DemoScanner.StartCommand = new RelayCommand(StartDemo);
        DemoScanner.CancelCommand = new RelayCommand(() => { _demo.Stop(); DemoScanner.State = ScanState.Cancelled; DemoScanner.Phase = "Varredura cancelada"; });
        _demo.Tick += (_, _) => DemoTick();

        DoneScanner.State = ScanState.Done;
        DoneScanner.Phase = "Pronto. 1,8 GB podem ser liberados.";
        DoneScanner.CurrentItem = "Temporários do Windows, cache de navegadores e lixeira";
        DoneScanner.Progress = 1;
        DoneScanner.ScannedCount = 48213;
        DoneScanner.FindingsCount = 0;
        DoneScanner.Elapsed = TimeSpan.FromSeconds(41);

        if (_pngDir != null) SetSnapshotState();
        else StartDemo();

        ContentRendered += async (_, _) => { if (_pngDir != null) await ExportAsync(); };
    }

    private void LoadSwatches()
    {
        (string Name, string Key)[] keys =
        {
            ("Fundo", "Bg"), ("Cartão", "Card"), ("Linha", "LineStrong"), ("Texto", "Fg"),
            ("Ouro", "Accent"), ("Ouro texto", "AccentText"), ("Bom", "Ok"), ("Atenção", "Warn"),
            ("Ruim", "Danger"), ("Info", "Info"), ("Secundário", "Muted"), ("Ouro suave", "AccentSoft"),
            ("Bom suave", "OkSoft"), ("Atenção suave", "WarnSoft"), ("Ruim suave", "DangerSoft"), ("Info suave", "InfoSoft"),
        };
        Swatches.ItemsSource = keys.Select(k =>
        {
            var b = (Brush)FindResource(k.Key);
            string hex = b is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "";
            return new Swatch(k.Name, $"{k.Key} {hex}", b);
        }).ToList();
    }

    private void Dark_Checked(object sender, RoutedEventArgs e) { if (IsInitialized) SetTheme(false); }
    private void Light_Checked(object sender, RoutedEventArgs e) { if (IsInitialized) SetTheme(true); }

    private void SetTheme(bool light)
    {
        ThemeManager.Apply(light);
        LoadSwatches(); // as amostras guardam o pincel; relê depois da troca
    }

    private void StartDemo()
    {
        _tick = 0;
        _findings.Clear();
        DemoScanner.FindingsCount = 0;
        DemoScanner.State = ScanState.Running;
        DemoScanner.Phase = "Verificando inicialização e downloads";
        _demo.Start();
    }

    private static readonly string[] DemoPaths =
    {
        @"C:\Users\voce\Downloads\setup_player.exe", @"C:\Users\voce\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
        @"C:\Program Files\Steam\steam.exe", @"C:\Users\voce\Downloads\ferramenta_antiga.exe", @"C:\Windows\System32\drivers\etc\hosts",
    };

    private void DemoTick()
    {
        _tick++;
        double p = Math.Min(1, _tick / 160.0);
        DemoScanner.Progress = p;
        DemoScanner.ScannedCount = _tick * 37;
        DemoScanner.Elapsed = TimeSpan.FromMilliseconds(_tick * 120);
        DemoScanner.CurrentItem = DemoPaths[_tick / 6 % DemoPaths.Length];
        if (_tick == 50) AddFinding(new ScanFinding("ferramenta_antiga.exe", "Sem assinatura digital, em Downloads. Verifique a origem antes de abrir.", HealthStatus.Atencao));
        if (_tick == 110) AddFinding(new ScanFinding("hosts", "Redireciona 12 sites de bancos. Item suspeito: revise antes de remover.", HealthStatus.Ruim));
        if (p >= 1)
        {
            _demo.Stop();
            DemoScanner.State = ScanState.Done;
            DemoScanner.Phase = "Pronto. 2 itens precisam da sua atenção.";
            DemoScanner.CurrentItem = "";
        }
    }

    private void AddFinding(ScanFinding f) { _findings.Add(f); DemoScanner.FindingsCount = _findings.Count; }

    /// <summary>Quadro fixo para as capturas: varredura no meio, com achados.</summary>
    private void SetSnapshotState()
    {
        DemoScanner.State = ScanState.Running;
        DemoScanner.Phase = "Verificando inicialização e downloads";
        DemoScanner.CurrentItem = DemoPaths[0];
        DemoScanner.Progress = 0.62;
        DemoScanner.ScannedCount = 3672;
        DemoScanner.Elapsed = TimeSpan.FromSeconds(12);
        AddFinding(new ScanFinding("ferramenta_antiga.exe", "Sem assinatura digital, em Downloads. Verifique a origem antes de abrir.", HealthStatus.Atencao));
        AddFinding(new ScanFinding("hosts", "Redireciona 12 sites de bancos. Item suspeito: revise antes de remover.", HealthStatus.Ruim));
    }

    private void OpenDestructive_Click(object sender, RoutedEventArgs e)
        => FkDialog.Confirm(this, "Remover o driver CH340 4.0?", "Guardamos uma cópia para você voltar quando quiser.", "Remover", kind: DialogKind.Destructive);

    private void OpenInfo_Click(object sender, RoutedEventArgs e)
        => FkDialog.Info(this, "Proteção em tempo real ligada", "A MURALHA verifica novos arquivos em Downloads, na área de trabalho e nos pendrives.");

    private async Task ExportAsync()
    {
        try
        {
            Directory.CreateDirectory(_pngDir!);
            foreach (var (light, name) in new[] { (false, "catalogo-escuro.png"), (true, "catalogo-claro.png") })
            {
                SetTheme(light);
                ThemeSwitch.Visibility = Visibility.Collapsed;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                // Mede a folha inteira (não só a parte visível da janela) e desenha em 1:1.
                Sheet.Measure(new Size(Sheet.ActualWidth, double.PositiveInfinity));
                Sheet.Arrange(new Rect(new Point(0, 0), new Size(Sheet.ActualWidth, Sheet.DesiredSize.Height)));
                Sheet.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(Sheet.ActualWidth), (int)Math.Ceiling(Sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bmp.Render(Sheet);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                await using var fs = File.Create(Path.Combine(_pngDir!, name));
                enc.Save(fs);
            }
        }
        finally { Close(); }
    }
}
