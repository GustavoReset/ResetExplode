using System.Windows;
using System.Windows.Controls;
using FortKnox.Core.Config;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Dados da OFICINA (seção "oficina" do modules.json).</summary>
public sealed class OficinaSettings
{
    /// <summary>ComDB original (reservas de números COM) antes da primeira "liberação", para "Desfazer liberação".</summary>
    public byte[]? ComDbBackup { get; set; }
}

/// <summary>OFICINA · Portas COM: registros de dispositivos ausentes e números COM presos (antigo trecho do MainWindow.xaml.cs).</summary>
public partial class PortsView : UserControl
{
    private List<UsbDevice> _usb = new();
    private ISettings<OficinaSettings>? _prefs;

    public PortsView() => InitializeComponent();

    public void Attach(ISettings<OficinaSettings> prefs) => _prefs = prefs;

    public void Scan()
    {
        try { _usb = UsbCleaner.List(); }
        catch (Exception ex) { UsbResult.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; return; }

        static bool Relevant(UsbDevice d) => d.Port != null || d.Maker != null;
        var connected = _usb.Where(d => d.Present && Relevant(d)).ToList();
        UsbConnected.Text = connected.Count == 0 ? "Nenhuma placa ou porta serial conectada."
                          : string.Join("\n", connected.Select(d => $"{d.Port ?? "—"} · {d.Name}"));

        var shown = _usb.Where(d => !d.Present && (UsbShowAll.IsChecked == true || Relevant(d))).ToList();
        UsbList.ItemsSource = shown;
        int ghosts = _usb.Count(d => !d.Present);
        UsbSummary.Text = shown.Count == 0
            ? "Nenhum registro antigo encontrado."
            : $"{shown.Count} registros antigos na lista ({ghosts} dispositivos ausentes no total).";

        try
        {
            var (reserved, inUse) = UsbCleaner.ComStatus();
            string R(IEnumerable<int> l) => l.Any() ? string.Join(", ", l.Select(n => $"COM{n}")) : "nenhum";
            UsbComDb.Text = $"{reserved.Count} reservados: {R(reserved)}\nEm uso agora: {R(inUse)}";
        }
        catch (Exception ex) { UsbComDb.Text = ex.Message; }
        BtnComDbRestore.IsEnabled = _prefs?.Value.ComDbBackup != null;
    }

    private void BtnUsbScan_Click(object sender, RoutedEventArgs e) => Scan();
    private void UsbShowAll_Changed(object sender, RoutedEventArgs e) { if (UsbList != null) Scan(); }

    private async void BtnUsbRemove_Click(object sender, RoutedEventArgs e)
    {
        var sel = _usb.Where(d => d.Selected && !d.Present).ToList();
        if (sel.Count == 0) { UsbResult.Text = "Nenhum registro selecionado."; return; }
        if (!AdminGuard.Require(this)) return;
        string names = string.Join(", ", sel.Take(6).Select(d => d.Name)) + (sel.Count > 6 ? $" e mais {sel.Count - 6}" : "");
        if (!FkDialog.Confirm(Window.GetWindow(this), $"Remover {sel.Count} registros de dispositivos desconectados?",
                names + ". Se plugar algum deles de novo, o Windows reinstala automaticamente.", "Remover", kind: DialogKind.Destructive))
            return;

        BtnUsbRemove.IsEnabled = false;
        UsbResult.Text = "Removendo…";
        var (removed, failed) = await Task.Run(() => UsbCleaner.Remove(sel.Select(d => d.InstanceId)));
        BtnUsbRemove.IsEnabled = true;
        UsbResult.Text = $"{removed} registros removidos." +
                         (failed.Count > 0 ? $" Não removidos: {string.Join("; ", failed.Take(5))}" : "") +
                         " Para liberar os números COM que eles prendiam, use \"Liberar números COM presos\".";
        Scan();
    }

    private void BtnComDb_Click(object sender, RoutedEventArgs e)
    {
        if (_prefs == null || !AdminGuard.Require(this)) return;
        var (reserved, inUse) = UsbCleaner.ComStatus();
        var freed = reserved.Except(inUse).ToList();
        if (freed.Count == 0) { UsbResult.Text = "Nenhum número COM preso: todos os reservados estão em uso."; return; }
        if (!FkDialog.Confirm(Window.GetWindow(this), $"Liberar {freed.Count} números COM presos?",
                $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}. " +
                $"Mantidos (em uso): {(inUse.Count > 0 ? string.Join(", ", inUse.Select(n => $"COM{n}")) : "nenhum")}. " +
                "A próxima placa plugada recebe um número baixo (COM3, COM4…). O valor atual fica guardado para Desfazer liberação.", "Liberar"))
            return;
        try
        {
            var old = UsbCleaner.RebuildComDb();
            _prefs.Update(p => p.ComDbBackup ??= old); // guarda o original da primeira vez
            UsbResult.Text = $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}. Plugue a placa de novo para ela pegar um número baixo.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        Scan();
    }

    private void BtnComDbRestore_Click(object sender, RoutedEventArgs e)
    {
        var backup = _prefs?.Value.ComDbBackup;
        if (backup == null || !AdminGuard.Require(this)) return;
        try
        {
            UsbCleaner.RestoreComDb(backup);
            _prefs!.Update(p => p.ComDbBackup = null);
            UsbResult.Text = "Reservas de números COM restauradas para como estavam.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        Scan();
    }
}
