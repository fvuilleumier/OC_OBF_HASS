using System.Buffers;
using System.Text.RegularExpressions;

namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Chemins relatifs échangés entre la source et la cible : leur forme, leur
/// validité, et la garantie qu'ils restent dans leur dossier.
///
/// Un chemin de la cible vient en partie de l'IA — un fichier qu'elle a créé —
/// ou d'une restauration — un segment pseudonymisé rendu à son nom réel. L'un
/// et l'autre se traitent comme une entrée non maîtrisée : un segment
/// <c>..</c>, une valeur restaurée qui contiendrait une barre, un nom réservé
/// de Windows, ou un dossier lien sur le chemin feraient écrire ailleurs que
/// prévu — jusque dans un autre dossier.
/// </summary>
public static partial class RelativePaths
{
    /// <summary>Longueur maximale d'un élément de chemin sous Windows.</summary>
    private const int MaximumSegmentLength = 255;

    /// <summary>Longueur au-delà de laquelle un chemin complet devient fragile sous Windows.</summary>
    private const int MaximumFullLength = 259;

    private const int TimeoutMilliseconds = 1000;

    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create("<>:\"|?*\\/");

    /// <summary>Dossiers où un échange n'écrit jamais.</summary>
    private static readonly string[] ForbiddenFolders = [".git", Policy.ObfuscationProject.MetadataFolderName];

    /// <summary>Nom réservé de Windows, avec ou sans extension : <c>CON</c>, <c>nul.txt</c>, <c>COM1</c>.</summary>
    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\..*)?$", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex ReservedName();

    /// <summary>
    /// Met un chemin relatif sous sa forme d'échange : barres obliques, sans
    /// barre ni <c>./</c> de tête.
    /// </summary>
    /// <param name="relative">Chemin relatif</param>
    /// <returns>Le chemin normalisé</returns>
    public static string Normalize(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        string normalized = relative.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    /// <summary>
    /// Donne le chemin d'un fichier relatif à un dossier, sous sa forme
    /// d'échange.
    /// </summary>
    /// <param name="root">Dossier de référence</param>
    /// <param name="path">Chemin du fichier</param>
    /// <returns>Le chemin relatif</returns>
    public static string Of(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Normalize(Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)));
    }

    /// <summary>
    /// Découpe un chemin relatif en éléments.
    /// </summary>
    /// <param name="relative">Chemin relatif, sous sa forme d'échange</param>
    /// <returns>Les éléments, du premier dossier au fichier</returns>
    public static string[] Segments(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return relative.Split('/');
    }

    /// <summary>
    /// Vérifie qu'un chemin relatif peut être écrit sans risque : chaque
    /// élément est un nom valide sous Windows, aucun ne remonte l'arborescence,
    /// et aucun n'entre dans un dossier où un échange n'écrit jamais.
    /// </summary>
    /// <param name="relative">Chemin relatif, sous sa forme d'échange</param>
    /// <returns>La raison du refus, ou <c>null</c> si le chemin est sûr</returns>
    public static string? Problem(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        if (relative.Length == 0)
        {
            return "chemin vide";
        }

        string[] segments = Segments(relative);
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            string? problem = SegmentProblem(segment);
            if (problem is not null)
            {
                return problem;
            }

            if (index < segments.Length - 1 && ForbiddenFolders.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return $"« {segment} » est un dossier réservé";
            }
        }

        return null;
    }

    /// <summary>
    /// Vérifie un élément de chemin.
    /// </summary>
    /// <param name="segment">Élément examiné</param>
    /// <returns>La raison du refus, ou <c>null</c> si l'élément est valide</returns>
    public static string? SegmentProblem(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (segment.Length == 0 || segment is "." or "..")
        {
            return "élément de chemin vide ou qui remonte l'arborescence";
        }

        if (segment.Length > MaximumSegmentLength)
        {
            return "nom trop long";
        }

        if (segment.AsSpan().IndexOfAny(InvalidCharacters) >= 0 || segment.Any(char.IsControl))
        {
            return $"« {segment} » contient un caractère interdit dans un nom de fichier";
        }

        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return $"« {segment} » se termine par un point ou une espace";
        }

        return ReservedName().IsMatch(segment) ? $"« {segment} » est un nom réservé de Windows" : null;
    }

    /// <summary>
    /// Donne le chemin absolu d'un chemin relatif, après s'être assuré qu'il
    /// reste dans son dossier.
    /// </summary>
    /// <param name="root">Dossier de référence</param>
    /// <param name="relative">Chemin relatif, sous sa forme d'échange</param>
    /// <returns>Le chemin absolu</returns>
    /// <exception cref="InvalidOperationException">Si le chemin sort du dossier</exception>
    public static string Resolve(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relative);

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Le chemin « {relative} » sort du dossier « {folder} ».");
        }

        return full;
    }

    /// <summary>
    /// Indique si un chemin complet dépasse la longueur que Windows tolère
    /// partout.
    /// </summary>
    /// <param name="full">Chemin absolu</param>
    /// <returns><c>true</c> si le chemin est trop long</returns>
    public static bool IsTooLong(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        return full.Length > MaximumFullLength;
    }

    /// <summary>
    /// Cherche un lien symbolique ou une jonction sur le chemin d'un fichier,
    /// entre son dossier de référence et lui, le fichier compris : écrire à
    /// travers un tel lien écrirait ailleurs, potentiellement dans l'autre
    /// dossier de l'échange.
    /// </summary>
    /// <param name="root">Dossier de référence</param>
    /// <param name="full">Chemin absolu du fichier</param>
    /// <returns>Le premier lien trouvé, ou <c>null</c></returns>
    public static string? LinkOnPath(string root, string full)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(full);

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? current = Path.GetFullPath(full);

        while (current is not null && current.Length > folder.Length)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}
