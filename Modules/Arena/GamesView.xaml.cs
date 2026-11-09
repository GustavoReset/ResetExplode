using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FortKnox;

/// <summary>ARENA · FPS estimado (Fase 3.3j; antes no MainWindow.xaml.cs): FPS esperado em 32 jogos para a GPU + CPU deste PC.</summary>
public partial class GamesView : UserControl
{
    private readonly HowManyFpsClient _fpsApi = new();
    private List<GameRow> _gameRows = new();
    private ListCollectionView? _gameView;
    private CancellationTokenSource? _gameFetch;
    private AppSettings _settings = null!;
    private Action _save = () => { };
    private string _cpuName = "", _gpuName = "";
    private double _cpuIdx = 80, _gpuIdx = 50;

    public GamesView() => InitializeComponent();

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    /// <summary>Chamado quando CPU e GPU foram detectadas. A chave da HowManyFPS fica cifrada (DPAPI) no AppSettings.</summary>
    public void Init(AppSettings settings, Action save, string cpuName, string gpuName, double cpuIdx, double gpuIdx)
    {
        _settings = settings; _save = save;
        _cpuName = cpuName; _gpuName = gpuName; _cpuIdx = cpuIdx; _gpuIdx = gpuIdx;
        GamesSub.Text = $"FPS esperado no seu PC · {GameCatalog.ShortGpuName(_gpuName)} · {GameCatalog.ShortCpuName(_cpuName)}";

        _fpsApi.ApiKey = _settings.HowManyFpsKey;
        if (_fpsApi.Configured) ApiKeyBox.Password = _settings.HowManyFpsKey;

        _gameRows = GameCatalog.Games.Select(g => new GameRow(g)).ToList();
        _gameView = new ListCollectionView(_gameRows);
        GameList.ItemsSource = _gameView;
        RefreshGames();
    }

    /// <summary>Ao fechar o app: para as consultas à API.</summary>
    public void CancelAll() { try { _gameFetch?.Cancel(); } catch (ObjectDisposedException) { } }

    private void GameOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_gameView != null) RefreshGames(); // também dispara durante InitializeComponent
    }

    private void GameSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_gameView == null) return;
        string q = GameSearch.Text.Trim();
        _gameView.Filter = q.Length == 0 ? null : o => ((GameRow)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApiKeyBox_Changed(object sender, RoutedEventArgs e)
        => ApiKeyHint.Visibility = ApiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void BtnApiKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.HowManyFpsKey = ApiKeyBox.Password;
        _save();
        _fpsApi.ApiKey = _settings.HowManyFpsKey;
        RefreshGames();
    }

    /// <summary>Recalcula a estimativa local de todos os jogos e, com chave configurada, busca os dados reais na API.</summary>
    private void RefreshGames()
    {
        string res = CheckedTag(GameResPanel) ?? "1920x1080";
        string preset = CheckedTag(GamePresetPanel) ?? "Alto";
        string up = CheckedTag(GameUpscalePanel) ?? "Native";

        foreach (var row in _gameRows)
        {
            var est = GameCatalog.Estimate(row.Game, _gpuIdx, _cpuIdx, res, preset, up);
            string limiter = est.Limiter switch
            {
                Bottleneck.Cpu => "gargalo: CPU",
                Bottleneck.Gpu => "gargalo: GPU",
                Bottleneck.Engine => $"trava do jogo em {row.Game.EngineCap:0} FPS",
                _ => ""
            };
            row.Set(est.Avg, 0, "ESTIMATIVA", limiter);
        }

        _gameFetch?.Cancel();
        if (_fpsApi.Configured)
        {
            GameSourceTitle.Text = "Fonte: HowManyFPS (dados reais)";
            GameSourceText.Text = "FPS médio e 1% low medidos em sessões reais de jogo. Respostas ficam em cache por 7 dias. " +
                                  "A API usa as configurações gráficas dela; \"Gráficos\" vale só para a estimativa.";
            BtnApiKey.Content = "ATUALIZAR CHAVE";
            _gameFetch = new CancellationTokenSource();
            _ = FetchGamesAsync(res, up, _gameFetch.Token);
        }
        else
        {
            GameSourceTitle.Text = "Fonte: estimativa local (aproximada)";
            GameSourceText.Text = "Calculada a partir do desempenho relativo da sua GPU e CPU. Para FPS medido em jogo, " +
                                  "peça uma chave da API em info@howmanyfps.com e cole ao lado.";
            BtnApiKey.Content = "SALVAR CHAVE";
        }
    }

    private async Task FetchGamesAsync(string res, string up, CancellationToken ct)
    {
        string cpu = GameCatalog.ShortCpuName(_cpuName), gpu = GameCatalog.ShortGpuName(_gpuName);
        int ok = 0;
        foreach (var row in _gameRows)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var r = await _fpsApi.GetAsync(cpu, gpu, row.Name, res, up, ct);
                row.Set(r.AvgFps, r.Min1Fps, "HOWMANYFPS", "");
                ok++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Set(row.Fps, 0, "ESTIMATIVA", $"API: {ex.Message}");
                if (ex.Message.Contains("chave") || ex.Message.Contains("cota"))
                {
                    GameSourceText.Text = $"A API recusou a consulta ({ex.Message}). Mostrando a estimativa local.";
                    return; // não adianta insistir nos outros jogos
                }
            }
        }
        if (!ct.IsCancellationRequested)
            GameSourceText.Text = $"{ok} de {_gameRows.Count} jogos com dados da HowManyFPS; os demais usam a estimativa local.";
    }

}
