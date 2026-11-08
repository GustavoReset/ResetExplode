namespace FortKnox;

/// <summary>Substituto mínimo: PasswordVault só usa Folder para o caminho padrão (os testes passam o caminho).</summary>
internal static class AppSettings
{
    public static string Folder => Path.Combine(Path.GetTempPath(), "FortKnoxTests");
}
