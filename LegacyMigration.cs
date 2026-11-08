using System.IO;

namespace FortKnox;

/// <summary>
/// Renomeio do app (ResetExplode → FortKnox): copia, uma única vez, a pasta de dados antiga para a nova.
/// Só roda se a nova ainda não existe; nunca sobrescreve e NÃO apaga a antiga (o usuário decide quando limpar).
/// A cópia é feita numa pasta temporária e só vira "FortKnox" no fim: se o app for fechado no meio, nada fica pela metade.
/// </summary>
public static class LegacyMigration
{
    public const string LegacyFolderName = "ResetExplode";

    /// <returns>true se copiou agora.</returns>
    public static bool Migrate(string legacy, string target)
    {
        try
        {
            if (Directory.Exists(target) || !Directory.Exists(legacy)) return false;
            string tmp = target + ".migrando";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true); // sobra de uma tentativa interrompida
            Directory.CreateDirectory(tmp);
            foreach (var dir in Directory.GetDirectories(legacy, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(tmp, Path.GetRelativePath(legacy, dir)));
            foreach (var file in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(tmp, Path.GetRelativePath(legacy, file)), overwrite: false);
            Directory.Move(tmp, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // sem migração, o app simplesmente começa com configurações novas
        }
    }
}
