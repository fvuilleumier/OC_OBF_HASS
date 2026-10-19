namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Retire un fichier sans le détruire : sur un poste Windows, il part à la
/// corbeille, d'où une erreur se rattrape.
///
/// Le cœur ne sait pas atteindre la corbeille, qui est l'affaire de
/// l'interface : il reçoit donc de quoi retirer un fichier, et ne supprime
/// jamais rien lui-même dans les dossiers de l'utilisateur.
/// </summary>
public interface IFileRecycler
{
    /// <summary>
    /// Retire un fichier.
    /// </summary>
    /// <param name="path">Chemin absolu du fichier</param>
    void Recycle(string path);
}

/// <summary>
/// Retire les fichiers en les déplaçant dans un dossier donné, sous un nom
/// horodaté. C'est le repli quand la corbeille n'est pas disponible, et ce qui
/// permet d'éprouver les échanges sans toucher à la corbeille de l'utilisateur.
/// </summary>
/// <param name="folder">Dossier où déposer les fichiers retirés</param>
public sealed class FolderRecycler(string folder) : IFileRecycler
{
    /// <inheritdoc />
    public void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Directory.CreateDirectory(folder);
        string name = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");

        File.Move(path, Path.Combine(folder, name));
    }
}
