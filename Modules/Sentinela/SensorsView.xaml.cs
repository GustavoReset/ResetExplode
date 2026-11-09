using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace FortKnox;

/// <summary>SENTINELA · Sensores (Fase 3.3l; antes no MainWindow.xaml.cs): todos os sensores ao vivo, com filtro por tipo e busca.</summary>
public partial class SensorsView : UserControl
{
    private ICollectionView? _view;
    private string? _category;   // filtro por chip (null = todos)
    private string _search = ""; // filtro por texto
    private bool _chips;

    public SensorsView() => InitializeComponent();

    /// <summary>Liga a grade às linhas do SensorMonitor (a lista começa vazia e se preenche quando a detecção termina).</summary>
    public void Attach(SensorMonitor monitor)
    {
        _view = CollectionViewSource.GetDefaultView(monitor.Rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Hardware)));
        SensorGrid.ItemsSource = _view;
    }

    /// <summary>Linha de status no topo da página (também usada pela janela principal para avisos de navegação).</summary>
    public string Status { get => StatusText.Text; set => StatusText.Text = value; }

    /// <summary>Depois da detecção: um chip por tipo de sensor.</summary>
    public void BuildChips(SensorMonitor monitor)
    {
        if (_chips) return;
        _chips = true;
        AddChip("Todos", null, isChecked: true);
        foreach (var c in monitor.Rows.Select(r => r.Category).Distinct().OrderBy(x => x))
            AddChip(c, c);
    }

    private void AddChip(string label, string? category, bool isChecked = false)
    {
        var chip = new RadioButton
        {
            Content = label,
            GroupName = "Category",
            Style = (System.Windows.Style)FindResource("Chip"),
            IsChecked = isChecked
        };
        chip.Checked += (_, _) => { _category = category; ApplySensorFilter(); };
        CategoryChips.Children.Add(chip);
    }

    private void SensorSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _search = SensorSearch.Text.Trim();
        ApplySensorFilter();
    }

    private void ApplySensorFilter()
    {
        if (_view == null) return;
        if (_category == null && _search.Length == 0) { _view.Filter = null; return; }
        _view.Filter = o =>
        {
            var r = (SensorRow)o;
            return (_category == null || r.Category == _category)
                && (_search.Length == 0
                    || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                    || r.Hardware.Contains(_search, StringComparison.OrdinalIgnoreCase));
        };
    }
}
