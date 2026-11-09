using System.Windows;
using FortKnox.UI;

namespace FortKnox;

/// <summary>Mensagem padrão quando a ação exige o app elevado (usado pelas Views dos módulos).</summary>
internal static class AdminGuard
{
    public static bool Require(DependencyObject from)
    {
        if (SensorMonitor.IsElevated) return true;
        FkDialog.Info(Window.GetWindow(from), "Precisa de administrador", "Esta ação precisa do Fort Knox aberto como administrador.");
        return false;
    }
}
