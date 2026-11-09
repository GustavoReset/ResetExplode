using System.Windows.Interop;
using FortKnox.Core;

namespace FortKnox;

/// <summary>
/// Ponte entre o menu atual (páginas Page* do MainWindow.xaml) e o registro de módulos do núcleo (Fase 3.2).
/// Páginas que já viraram módulo têm rota; as outras navegam como "legado/PageX" até migrarem (Fase 3.3).
/// </summary>
public partial class MainWindow
{
    private static readonly Dictionary<string, string> ModuleRoutes = new()
    {
        ["PageVault"] = CofreModule.RouteSenhas,
        ["PagePdf"] = PrensaModule.RoutePdf,
        ["PageNet"] = RadarModule.RouteConexao,
    };

    private FortKnoxHost? Core => App.Core;

    private void RegisterModules()
    {
        if (Core == null) return;
        Core.Modules.Add(new CofreModule(PageVault, () => new WindowInteropHelper(this).Handle));
        Core.Modules.Add(new PrensaModule(PagePdf));
        Core.Modules.Add(new RadarModule(NetTools));
    }

    /// <summary>Chamado por Nav_Checked. Erros do módulo vão para o log e para a barra de status, sem derrubar o app.</summary>
    private async void NavigateModules(string page)
    {
        if (Core == null) return;
        string route = ModuleRoutes.TryGetValue(page, out var r) ? r : "legado/" + page;
        try
        {
            await Core.Modules.NavigateAsync(route);
            Core.Log.Write(LogLevel.Debug, "Página: " + route);
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Navegação para {route} falhou", ex);
            StatusText.Text = "Não foi possível abrir esta página: " + ex.Message;
        }
    }

    /// <summary>Ao fechar a janela: encerra os módulos na thread da interface (eles mexem em controles).</summary>
    private void ShutdownModules()
    {
        if (Core == null) return;
        var t = Core.Modules.ShutdownAsync();
        if (!t.IsCompleted) Core.Log.Warn("Algum módulo terminou de encerrar em segundo plano");
    }
}
