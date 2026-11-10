namespace FortKnox;

public sealed record ProficiencyAssessmentResult(
    IReadOnlyList<int> Answers,
    int Total,
    double Percentage,
    string Level,
    string Profile);

public static class ProficiencyAssessment
{
    public const int QuestionCount = 6;

    public static ProficiencyAssessmentResult Calculate(IReadOnlyList<int> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count != QuestionCount)
            throw new ArgumentException($"A avaliação exige exatamente {QuestionCount} respostas.", nameof(answers));
        if (answers.Any(answer => answer is < 1 or > 5))
            throw new ArgumentOutOfRangeException(nameof(answers), "Cada resposta deve estar entre 1 e 5.");

        int total = answers.Sum();
        (string level, string profile) = total switch
        {
            <= 12 => ("Iniciante / Baixa Proficiência",
                "Depende de suporte direto para tarefas cotidianas. Necessita de treinamento básico em navegação de arquivos e atalhos."),
            <= 20 => ("Intermediário / Proficiência Operacional",
                "Realiza tarefas diárias de escritório e navegação web com autonomia, mas necessita de auxílio para instalar programas ou resolver falhas técnicas."),
            <= 26 => ("Avançado / Proficiência Fluente",
                "Utiliza a máquina com alta produtividade, domina atalhos, lida bem com arquivos e soluciona a maioria dos imprevistos normais do sistema."),
            _ => ("Especialista / Autônomo",
                "Possui total domínio do ambiente operacional, configura dispositivos, soluciona problemas avançados e serve de referência/suporte para outros usuários.")
        };

        return new ProficiencyAssessmentResult(answers.ToArray(), total, total / 30d * 100, level, profile);
    }
}
