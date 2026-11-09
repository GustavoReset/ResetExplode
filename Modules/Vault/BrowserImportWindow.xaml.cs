using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace FortKnox;

public partial class BrowserImportWindow : Window
{
    public BrowserDefinition? SelectedBrowser { get; private set; }
    public string? SelectedExecutable { get; private set; }
    public string? SelectedCsv { get; private set; }

    public BrowserImportWindow()
    {
        InitializeComponent();
        foreach (var browser in BrowserPasswordImporter.Browsers)
            BrowserGrid.Children.Add(CreateBrowserButton(browser));
    }

    private Button CreateBrowserButton(BrowserDefinition browser)
    {
        var image = new System.Windows.Controls.Image { Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0) };
        string? exe = BrowserPasswordImporter.FindExecutable(browser);
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            try
            {
                using System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(exe)!;
                using var bitmap = icon.ToBitmap();
                var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var source = new BitmapImage();
                source.BeginInit();
                source.StreamSource = stream;
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.EndInit();
                image.Source = source;
            }
            catch { }
        }

        var letter = new TextBlock
        {
            Text = browser.Letter,
            Width = 38,
            Height = 38,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        if (image.Source != null) letter.Visibility = Visibility.Collapsed;

        var text = new TextBlock { Text = browser.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        content.Children.Add(image);
        content.Children.Add(letter);
        content.Children.Add(text);
        Grid.SetColumn(text, 1);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(image, 0);
        Grid.SetColumn(letter, 0);

        var button = new Button
        {
            Style = (Style)FindResource("OutlineButton"),
            Content = content,
            Width = 178,
            Height = 58,
            Margin = new Thickness(0, 0, 10, 10),
            Tag = browser
        };
        button.Click += Browser_Click;
        return button;
    }

    private async void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not BrowserDefinition browser) return;
        SelectedBrowser = browser;
        SelectedExecutable = BrowserPasswordImporter.FindExecutable(browser);

        if (string.IsNullOrEmpty(SelectedExecutable))
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Indicar o executável do {browser.Name}",
                Filter = "Executável (*.exe)|*.exe|Todos os arquivos|*.*",
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) != true) return;
            SelectedExecutable = picker.FileName;
        }

        DialogResult = true;
    }

    private void ChooseCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolher arquivo CSV exportado pelo navegador",
            Filter = "CSV (*.csv)|*.csv|Todos os arquivos|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        SelectedCsv = picker.FileName;
        DialogResult = true;
    }
}
