using System.ComponentModel;
using System.Globalization;

namespace ResetExplode;

public enum SensorType { Clock, Temperature, Load, Fan, Power, Voltage, Current, Data }

/// <summary>Linha do painel de sensores, com notificação de mudança para a UI.</summary>
public class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public string Category => Type switch
    {
        SensorType.Clock => "Clock", SensorType.Temperature => "Temperatura", SensorType.Load => "Carga", SensorType.Fan => "Ventoinha",
        SensorType.Power => "Energia", SensorType.Voltage => "Tensão", SensorType.Current => "Corrente", _ => "Dados"
    };

    private float? _value, _min, _max;
    public float? RawValue => _value;
    public string Value => Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    public void Update(float? value)
    {
        _value = value;
        if (value != null) { _min = _min == null ? value : Math.Min(_min.Value, value.Value); _max = _max == null ? value : Math.Max(_max.Value, value.Value); }
        foreach (var n in new[] { nameof(Value), nameof(Min), nameof(Max) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private string Format(float? v)
    {
        if (v == null) return "—";
        var c = CultureInfo.CurrentCulture;
        return Type switch
        {
            SensorType.Clock => $"{v.Value.ToString("0", c)} MHz",
            SensorType.Temperature => $"{v.Value.ToString("0.#", c)} °C",
            SensorType.Load => $"{v.Value.ToString("0.#", c)} %",
            SensorType.Fan => $"{v.Value.ToString("0", c)} RPM",
            SensorType.Power => $"{v.Value.ToString("0.#", c)} W",
            SensorType.Voltage => $"{v.Value.ToString("0.###", c)} V",
            SensorType.Current => $"{v.Value.ToString("0.##", c)} A",
            _ => v.Value.ToString("0.#", c)
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
