using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Commit et push des dossiers liés, à la demande de l'utilisateur : ce qu'il
/// taperait dans la console du dossier, sans quitter l'application. Seuls les
/// dossiers liés sont concernés — « in » et « out » vivent dans le projet, qui
/// n'est pas un dépôt à publier.
/// </summary>
public sealed partial class ExchangePresenter
{
    /// <summary>
    /// Donne le dépôt git d'un côté de l'échange, s'il est lié et versionné.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <returns>La racine du dépôt, ou <c>null</c></returns>
    public static string? RepositoryOf(ObfuscationProject project, ExchangeSide side)
    {
        ArgumentNullException.ThrowIfNull(project);

        string? folder = LinkedFolder(project, side);
        return folder is not null && Directory.Exists(folder) ? GitRepository.RootOf(folder) : null;
    }

    /// <summary>
    /// Donne l'état git d'un côté de l'échange, lu dans les fichiers du dépôt.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <returns>L'état, ou <c>null</c> si ce côté n'est pas un dossier lié versionné</returns>
    public static GitState? GitStateOf(ObfuscationProject project, ExchangeSide side)
    {
        string? root = RepositoryOf(project, side);
        return root is null ? null : GitRepository.Describe(root);
    }

    /// <summary>
    /// Commite tout ce qui a changé dans le dossier lié d'un côté.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <param name="message">Message du commit ; le message par défaut s'il est vide</param>
    /// <returns>Le résultat de git</returns>
    /// <exception cref="InvalidOperationException">Si ce côté n'est pas un dossier lié versionné</exception>
    public static GitResult Commit(ObfuscationProject project, ExchangeSide side, string? message)
    {
        return GitRepository.Commit(VersionedFolder(project, side), message);
    }

    /// <summary>
    /// Pousse la branche courante du dépôt d'un côté.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <returns>Le résultat de git</returns>
    /// <exception cref="InvalidOperationException">Si ce côté n'est pas un dossier lié versionné</exception>
    public static GitResult Push(ObfuscationProject project, ExchangeSide side)
    {
        return GitRepository.Push(VersionedFolder(project, side));
    }

    /// <summary>
    /// Donne le dossier lié d'un côté, ou <c>null</c> s'il n'y en a pas.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <returns>Le dossier lié</returns>
    private static string? LinkedFolder(ObfuscationProject project, ExchangeSide side)
    {
        if (side == ExchangeSide.Source)
        {
            return project.IsSourceLinked ? project.SourcePath : null;
        }

        return project.IsTargetLinked ? project.TargetPath : null;
    }

    /// <summary>
    /// Donne le dossier lié d'un côté, après avoir vérifié qu'il est dans un
    /// dépôt git.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté de l'échange</param>
    /// <returns>Le dossier lié</returns>
    /// <exception cref="InvalidOperationException">Si ce côté n'est pas un dossier lié versionné</exception>
    private static string VersionedFolder(ObfuscationProject project, ExchangeSide side)
    {
        ArgumentNullException.ThrowIfNull(project);

        string folder = LinkedFolder(project, side)
            ?? throw new InvalidOperationException("Ce côté n'est pas un dossier lié : rien à commiter.");

        return RepositoryOf(project, side) is null
            ? throw new InvalidOperationException($"« {folder} » n'est pas dans un dépôt git.")
            : folder;
    }
}
