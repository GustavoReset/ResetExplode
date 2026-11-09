namespace FortKnox;

/// <summary>Só o que o StartupManager usa do AppSettings do app (o original traz dependências de WPF e DPAPI).</summary>
public static class AppSettings
{
    public static string Folder => Path.Combine(Path.GetTempPath(), "FortKnoxTests");
}
