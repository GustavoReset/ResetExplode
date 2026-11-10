using Xunit;

namespace FortKnox;

public sealed class ProficiencyAssessmentTests
{
    [Theory]
    [InlineData(6, "Iniciante / Baixa Proficiência")]
    [InlineData(12, "Iniciante / Baixa Proficiência")]
    [InlineData(13, "Intermediário / Proficiência Operacional")]
    [InlineData(20, "Intermediário / Proficiência Operacional")]
    [InlineData(21, "Avançado / Proficiência Fluente")]
    [InlineData(26, "Avançado / Proficiência Fluente")]
    [InlineData(27, "Especialista / Autônomo")]
    [InlineData(30, "Especialista / Autônomo")]
    public void CalculateClassifiesScoreBoundaries(int total, string expectedLevel)
    {
        int[] answers = Enumerable.Repeat(1, ProficiencyAssessment.QuestionCount).ToArray();
        int remaining = total - ProficiencyAssessment.QuestionCount;
        for (int i = 0; i < answers.Length && remaining > 0; i++)
        {
            int increment = Math.Min(4, remaining);
            answers[i] += increment;
            remaining -= increment;
        }

        var result = ProficiencyAssessment.Calculate(answers);

        Assert.Equal(total, result.Total);
        Assert.Equal(total / 30d * 100, result.Percentage);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5 })]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6 })]
    public void CalculateRejectsInvalidAnswers(int[] answers)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProficiencyAssessment.Calculate(answers));
    }
}
