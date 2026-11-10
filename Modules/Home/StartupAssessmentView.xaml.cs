using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FortKnox;

public partial class StartupAssessmentView : UserControl
{
    private static readonly (string Section, string Question)[] Questions =
    {
        ("NÍVEL BÁSICO  ·  OPERAÇÃO E SISTEMA",
            "Consegue navegar pelo sistema operacional para criar, renomear, mover, organizar e excluir pastas e arquivos?"),
        ("NÍVEL BÁSICO  ·  OPERAÇÃO E SISTEMA",
            "Consegue alternar entre janelas abertas e utilizar atalhos de teclado essenciais (como Ctrl+C, Ctrl+V, Alt+Tab)?"),
        ("NÍVEL INTERMEDIÁRIO  ·  PRODUTIVIDADE E ARQUIVOS",
            "Consegue criar e formatar documentos de texto (Word/Docs) ou utilizar fórmulas básicas em planilhas (Excel/Sheets, ex: SOMA)?"),
        ("NÍVEL INTERMEDIÁRIO  ·  PRODUTIVIDADE E ARQUIVOS",
            "Consegue comprimir/descomprimir arquivos (.zip/.rar) e enviá-los como anexos em e-mails ou na nuvem?"),
        ("NÍVEL AVANÇADO  ·  AUTONOMIA E RESOLUÇÃO DE PROBLEMAS",
            "Consegue baixar, instalar ou remover programas e periféricos (ex: impressoras, áudio) no sistema sem assistência técnica?"),
        ("NÍVEL AVANÇADO  ·  AUTONOMIA E RESOLUÇÃO DE PROBLEMAS",
            "Consegue solucionar falhas simples do cotidiano (ex: redefinir rede, encerrar processos travados no Gerenciador de Tarefas ou buscar soluções por conta própria)?")
    };

    private static readonly string[] ScaleDescriptions =
    {
        "Não sei realizar",
        "Realizo com dificuldade / apoio de terceiros",
        "Realizo com autonomia básica",
        "Realizo com facilidade e rapidez",
        "Domino totalmente e sei ensinar/orientar outros"
    };

    private readonly int[] _answers = new int[ProficiencyAssessment.QuestionCount];
    private readonly bool[] _answered = new bool[ProficiencyAssessment.QuestionCount];
    private int _questionIndex;
    private bool _updatingSlider = true;
    private bool _showingResult;

    public event EventHandler<AssessmentSubmissionEventArgs>? SubmitRequested;
    public event EventHandler? ContinueRequested;

    public StartupAssessmentView()
    {
        InitializeComponent();
        ShowQuestion();
    }

    public void ShowSaveError(string message)
    {
        SaveErrorText.Text = message;
        SaveErrorText.Visibility = Visibility.Visible;
    }

    private void ShowQuestion()
    {
        _showingResult = false;
        _updatingSlider = true;
        SectionText.Text = Questions[_questionIndex].Section;
        QuestionText.Text = Questions[_questionIndex].Question;
        QuestionCountText.Text = $"{_questionIndex + 1:00} / {Questions.Length:00}";
        QuestionProgress.Value = _questionIndex + 1;
        AnswerSlider.Value = _answered[_questionIndex] ? _answers[_questionIndex] : 3;
        SelectionText.Text = _answered[_questionIndex]
            ? $"{_answers[_questionIndex]}  ·  {ScaleDescriptions[_answers[_questionIndex] - 1]}"
            : "Escolha uma opção para continuar";
        NextButton.Content = _questionIndex == Questions.Length - 1 ? "Enviar" : "Próximo";
        NextButton.IsEnabled = _answered[_questionIndex];
        BackButton.Visibility = _questionIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        QuestionPanel.Visibility = Visibility.Visible;
        QuestionContent.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Visible;
        ContinueButton.Visibility = Visibility.Collapsed;
        SaveErrorText.Visibility = Visibility.Collapsed;
        _updatingSlider = false;
    }

    private void AnswerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _showingResult) return;
        int answer = (int)Math.Round(AnswerSlider.Value);
        _answers[_questionIndex] = answer;
        _answered[_questionIndex] = true;
        SelectionText.Text = $"{answer}  ·  {ScaleDescriptions[answer - 1]}";
        NextButton.IsEnabled = true;
        SaveErrorText.Visibility = Visibility.Collapsed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_questionIndex == 0) return;
        _questionIndex--;
        ShowQuestion();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_answered[_questionIndex]) return;
        if (_questionIndex < Questions.Length - 1)
        {
            _questionIndex++;
            ShowQuestion();
            return;
        }

        var result = ProficiencyAssessment.Calculate(_answers);
        var submission = new AssessmentSubmissionEventArgs(result);
        SubmitRequested?.Invoke(this, submission);
        if (!submission.Saved)
        {
            ShowSaveError(submission.ErrorMessage ?? "Não foi possível salvar o resultado. Tente novamente.");
            return;
        }

        _showingResult = true;
        QuestionPanel.Visibility = Visibility.Collapsed;
        QuestionContent.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Visible;
        ResultLevel.Text = result.Level;
        ResultProfile.Text = result.Profile;
        ResultScore.Text = $"{result.Total} / 30";
        ResultPercentage.Text = result.Percentage.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + "%";
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e) => ContinueRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class AssessmentSubmissionEventArgs(ProficiencyAssessmentResult result) : EventArgs
{
    public ProficiencyAssessmentResult Result { get; } = result;
    public bool Saved { get; set; }
    public string? ErrorMessage { get; set; }
}
