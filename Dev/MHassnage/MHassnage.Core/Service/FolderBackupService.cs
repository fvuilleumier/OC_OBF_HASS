namespace MHassnage.Core.Service;

/// <summary>Copie miroir d'un dossier : la destination est vidée puis remplacée intégralement.</summary>
public static class FolderBackupService
{
    /// <returns>Nombre de fichiers copiés.</returns>
    /// <exception cref="InvalidOperationException">Chemins absents ou dangereux (racine, imbriqués).</exception>
    public static int Mirror(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            throw new InvalidOperationException($"Dossier source introuvable : « {source} ».");
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("Dossier de sauvegarde non configuré (Obfuscator:BackupFolder).");

        string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string dst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        // Garde-fous : la destination est supprimée, elle ne doit rien contenir d'autre que la copie
        if (Path.GetPathRoot(dst)?.TrimEnd('\\', '/') == dst.TrimEnd('\\', '/'))
            throw new InvalidOperationException($"Refus : « {dst} » est une racine de disque.");
        if (IsSameOrInside(dst, src) || IsSameOrInside(src, dst))
            throw new InvalidOperationException("Refus : la source et la sauvegarde ne doivent pas être imbriquées.");

        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);

        int count = 0;
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
            count++;
        }
        return count;
    }

    private static bool IsSameOrInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
