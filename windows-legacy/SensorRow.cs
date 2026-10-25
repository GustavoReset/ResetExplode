using System.ComponentModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Linha do painel de sensores, com notificação de mudança para a UI.</summary>
public class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public HardwareType HardwareType { get; init; }

    private float? _value, _min, _max;
    public float? RawValue => _value;

    /// <summary>Ordem de exibição por tipo (clocks e temperaturas primeiro, como no GPU-Z).</summary>
    public int Order => Type switch
    {
        SensorType.Clock => 0, SensorType.Temperature => 1, SensorType.Load => 2, SensorType.Fan => 3,
        SensorType.Control => 4, SensorType.Power => 5, SensorType.Voltage => 6, SensorType.Current => 7,
        SensorType.SmallData => 8, SensorType.Data => 9, SensorType.Throughput => 10, _ => 11
    };
    public string Value => Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    public void Update(float? value, float? min, float? max)
    {
        _value = value; _min = min; _max = max;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Min)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Max)));
    }

    private string Format(float? v)
    {
        if (v == null) return "—";
        return Type switch
        {
            SensorType.Temperature => $"{v:0.#} °C",
            SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => $"{v:0.#} %",
            SensorType.Clock => $"{v:0} MHz",
            SensorType.Power => $"{v:0.#} W",
            SensorType.Voltage => $"{v:0.###} V",
            SensorType.Current => $"{v:0.###} A",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Flow => $"{v:0.#} L/h",
            SensorType.Frequency => $"{v:0.#} Hz",
            SensorType.Data => $"{v:0.##} GB",
            SensorType.SmallData => $"{v:0.#} MB",
            SensorType.Throughput => $"{v / 1048576f:0.##} MB/s",
            SensorType.Energy => $"{v:0.#} mWh",
            _ => $"{v:0.##}"
        };
    }

    public static string CategoryName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Carga",
        SensorType.Clock => "Clock",
        SensorType.Power => "Consumo",
        SensorType.Voltage => "Tensão",
        SensorType.Current => "Corrente",
        SensorType.Fan => "Ventoinhas",
        SensorType.Control => "Controle",
        SensorType.Level => "Nível",
        SensorType.Data or SensorType.SmallData => "Dados",
        SensorType.Throughput => "Taxa",
        SensorType.Frequency => "Frequência",
        SensorType.Energy => "Energia",
        SensorType.Flow => "Fluxo",
        SensorType.Humidity => "Umidade",
        SensorType.Factor => "Fator",
        _ => t.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
