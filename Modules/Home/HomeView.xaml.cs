using System.Windows.Controls;

namespace FortKnox;

/// <summary>
/// Página INÍCIO (Home): apenas a mandala de categorias (Office, Software, Redes e Defesa).
/// A ferramenta escolhida abre pela mesma navegação do menu lateral.
/// </summary>
public partial class HomeView : UserControl
{
    private Action<string>? _navigate;

    public HomeView()
    {
        InitializeComponent();
        Mandala.Navigate += page => _navigate?.Invoke(page);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Mandala.Open(); };
        Loaded += (_, _) => Mandala.Open();
    }

    /// <summary>Liga a mandala à navegação do Shell (Tag da página, ex.: "PageCleanup").</summary>
    public void Attach(Action<string> navigate) => _navigate = navigate;

    /// <summary>Reabre a mandala no nível das categorias (usado ao voltar de uma ferramenta).</summary>
    public void Open() => Mandala.Open();

    /// <summary>Usado só pelo modo de revisão visual (--capturas).</summary>
    public void SelectCategoryForCapture(int index) => Mandala.SelectCategoryForCapture(index);
}
