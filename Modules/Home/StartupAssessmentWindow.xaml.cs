using System.IO;
using System.Windows;

namespace FortKnox;

public partial class StartupAssessmentWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _openMainWindow;

    public StartupAssessmentWindow(AppSettings settings, Action openMainWindow)
    {
        _settings = settings;
        _openMainWindow = openMainWindow;
        InitializeComponent();
        ThemeManager.Apply(_settings.LightTheme);
        Assessment.SubmitRequested += SaveAssessmentResult;
        Assessment.ContinueRequested += (_, _) =>
        {
            _openMainWindow();
            Close();
        };
    }

    private void SaveAssessmentResult(object? sender, AssessmentSubmissionEventArgs e)
    {
        _settings.ProficiencyAssessmentAnswers = e.Result.Answers.ToList();
        _settings.ProficiencyAssessmentScore = e.Result.Total;
        _settings.ProficiencyAssessmentPercentage = e.Result.Percentage;
        _settings.ProficiencyAssessmentLevel = e.Result.Level;
        _settings.ProficiencyAssessmentProfile = e.Result.Profile;
        _settings.ProficiencyAssessmentCompleted = true;

        try
        {
            _settings.Save();
            e.Saved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _settings.ProficiencyAssessmentCompleted = false;
            e.ErrorMessage = $"Não foi possível salvar a avaliação em {AppSettings.Folder}: {ex.Message}";
        }
    }
}
