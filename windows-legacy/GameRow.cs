using System.ComponentModel;

namespace ResetExplode;

/// <summary>Cartão de um jogo na aba Jogos.</summary>
public sealed class GameRow : INotifyPropertyChanged
{
    public GameInfo Game { get; }
    public string Name => Game.Name;
    public string Genre => Game.Genre;

    public GameRow(GameInfo game) => Game = game;

    private double _fps, _low;
    private string _source = "", _detail = "";

    public double Fps => _fps;
    public string FpsText => _fps > 0 ? $"{_fps:0}" : "—";
    public string LowText => _low > 0 ? $"1% low {_low:0}" : "";
    public string Source => _source;   // "HowManyFPS" ou "Estimativa"
    public string Detail => _detail;   // gargalo, erro da API...

    /// <summary>Faixas comuns em calculadoras de FPS: 144+ excelente, 90+ ótimo, 60+ jogável.</summary>
    public string Rating => _fps switch
    {
        <= 0 => "",
        >= 144 => "EXCELENTE",
        >= 90 => "ÓTIMO",
        >= 60 => "JOGÁVEL",
        >= 30 => "FRACO",
        _ => "INJOGÁVEL"
    };

    /// <summary>Preenchimento da barra (escala até 240 FPS).</summary>
    public double Bar => Math.Clamp(_fps / 240.0, 0, 1);

    public void Set(double fps, double low, string source, string detail)
    {
        _fps = fps; _low = low; _source = source; _detail = detail;
        foreach (var p in new[] { nameof(FpsText), nameof(LowText), nameof(Source), nameof(Detail), nameof(Rating), nameof(Bar) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
