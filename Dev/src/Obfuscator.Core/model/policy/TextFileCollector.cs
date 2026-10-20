using System.IO.Enumeration;

namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Lien symbolique ou jonction rencontré pendant une collecte, et laissé de
/// côté.
/// </summary>
/// <param name="RelativePath">Chemin du lien, relatif à la racine explorée, avec des barres obliques</param>
/// <param name="Target">Ce que le lien désigne, s'il a pu être lu</param>
public sealed record SkippedLink(string RelativePath, string? Target);

/// <summary>
/// Résultat d'une collecte : les fichiers retenus, et les liens écartés.
/// </summary>
/// <param name="Files">Fichiers à traiter, triés</param>
/// <param name="Links">Liens symboliques et jonctions rencontrés, jamais suivis</param>
public sealed record CollectionResult(IReadOnlyList<string> Files, IReadOnlyList<SkippedLink> Links);

/// <summary>
/// Sélectionne les fichiers à traiter dans un dossier.
///
/// L'outil doit accepter n'importe quel fichier texte, y compris sans extension
/// connue — un export d'équipement réseau s'appelle volontiers
/// <c>running-config</c>. La règle est donc inversée par rapport à une liste
/// blanche d'extensions : tout fichier est accepté sauf s'il est manifestement
/// binaire, trop volumineux, ou situé dans les dossiers de travail de l'outil.
///
/// Les liens symboliques et les jonctions ne sont jamais suivis : un lien posé
/// dans un dossier peut mener n'importe où sur le disque — jusque dans le
/// dossier en clair, depuis celui de l'IA. Ils sont écartés et rapportés, pour
/// que l'appelant puisse le dire. Les fichiers à la demande d'un service de
/// synchronisation, eux aussi signalés comme points d'analyse au système, ne
/// sont pas des liens et restent lus.
/// </summary>
public sealed class TextFileCollector
{
    /// <summary>
    /// Taille maximale d'un fichier traité. Au-delà, l'analyse par expressions
    /// régulières devient coûteuse et le fichier n'a de toute façon plus
    /// vocation à être collé dans une conversation.
    /// </summary>
    public const long DefaultMaximumBytes = 8 * 1024 * 1024;

    /// <summary>Nombre d'octets examinés pour décider si un fichier est binaire.</summary>
    private const int SniffLength = 8192;

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".zip", ".7z", ".gz", ".tar", ".rar", ".png", ".jpg", ".jpeg",
        ".gif", ".bmp", ".ico", ".pdf", ".docx", ".xlsx", ".pptx", ".mp4", ".mp3", ".iso",
        ".bin", ".dat", ".db", ".sqlite", ".vhdx", ".ova",
    };

    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ObfuscationProject.MetadataFolderName, ".git", ".vs", "bin", "obj", "node_modules",
    };

    /// <summary>Taille maximale acceptée, en octets.</summary>
    public long MaximumBytes { get; set; } = DefaultMaximumBytes;

    /// <summary>
    /// Vrai pour écarter les sorties de l'outil — fichiers obfusqués et
    /// légendes — quand on recense des entrées. Le contrôle d'un dossier de
    /// sortie, lui, doit au contraire les retenir.
    /// </summary>
    public bool SkipProducedFiles { get; init; }

    /// <summary>
    /// Chemins tenus à l'écart, relatifs à la racine explorée et écrits avec
    /// des barres obliques. Un chemin désigne un fichier, ou un dossier et tout
    /// ce qu'il contient ; un dossier exclu n'est pas parcouru.
    /// </summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];

    /// <summary>
    /// Recense les fichiers traitables d'un chemin, qu'il désigne un fichier ou
    /// un dossier.
    /// </summary>
    /// <param name="path">Fichier ou dossier à explorer</param>
    /// <returns>Les chemins retenus, triés pour que l'ordre de traitement soit stable</returns>
    /// <exception cref="FileNotFoundException">Si le chemin n'existe pas</exception>
    public IReadOnlyList<string> Collect(string path)
    {
        return CollectDetailed(path).Files;
    }

    /// <summary>
    /// Recense les fichiers traitables d'un chemin, et les liens écartés en
    /// chemin.
    /// </summary>
    /// <param name="path">Fichier ou dossier à explorer</param>
    /// <returns>Les fichiers retenus, triés, et les liens rencontrés</returns>
    /// <exception cref="FileNotFoundException">Si le chemin n'existe pas</exception>
    public CollectionResult CollectDetailed(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            return new CollectionResult(IsWanted(path) && IsTextFile(path) ? [path] : [], []);
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"Chemin introuvable : {path}", path);
        }

        string root = Path.GetFullPath(path);
        List<string> exclusions = [.. ExcludedPaths.Select(NormalizeExclusion).Where(entry => entry.Length > 0)];
        List<SkippedLink> links = [];

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            MatchType = MatchType.Win32,
            ReturnSpecialDirectories = false,
        };

        FileSystemEnumerable<string> entries = new(path, static (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(), options)
        {
            // Un dossier n'est parcouru que s'il n'est ni un dossier de travail,
            // ni exclu, ni un lien : ce qu'il contient n'est alors même pas lu.
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                if (ExcludedFolders.Contains(entry.FileName.ToString()))
                {
                    return false;
                }

                string relative = RelativeOf(root, entry.ToFullPath());
                if (IsLink(ref entry, out string? target))
                {
                    links.Add(new SkippedLink(relative, target));
                    return false;
                }

                return !IsExcluded(exclusions, relative);
            },
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if (entry.IsDirectory || AtomicFile.IsTemporary(entry.FileName.ToString()))
                {
                    return false;
                }

                string relative = RelativeOf(root, entry.ToFullPath());
                if (IsLink(ref entry, out string? target))
                {
                    links.Add(new SkippedLink(relative, target));
                    return false;
                }

                return !IsExcluded(exclusions, relative);
            },
        };

        List<string> files = [.. entries.Where(candidate => IsWanted(candidate) && IsTextFile(candidate))];
        files.Sort(StringComparer.OrdinalIgnoreCase);
        links.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        return new CollectionResult(files, links);
    }

    /// <summary>
    /// Détermine si un fichier est un texte traitable.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <returns><c>true</c> si le fichier doit être traité</returns>
    public bool IsTextFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileInfo info = new(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaximumBytes)
        {
            return false;
        }

        if (ExcludedExtensions.Contains(info.Extension))
        {
            return false;
        }

        // Un octet nul dans les premiers kilo-octets est le signe le plus fiable
        // d'un contenu binaire, tous formats texte confondus.
        byte[] buffer = new byte[Math.Min(SniffLength, (int)info.Length)];
        using FileStream stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        return Array.IndexOf(buffer, (byte)0, 0, read) < 0;
    }

    /// <summary>
    /// Met un chemin d'exclusion sous sa forme de comparaison : barres
    /// obliques, sans barre de tête ni de fin.
    /// </summary>
    /// <param name="exclusion">Chemin tel qu'il est enregistré</param>
    /// <returns>Le chemin normalisé, vide s'il ne désigne rien</returns>
    public static string NormalizeExclusion(string exclusion)
    {
        ArgumentNullException.ThrowIfNull(exclusion);

        string normalized = exclusion.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    /// <summary>
    /// Indique si un chemin relatif est exclu : il est lui-même exclu, ou se
    /// trouve dans un dossier exclu.
    /// </summary>
    /// <param name="exclusions">Exclusions normalisées</param>
    /// <param name="relative">Chemin relatif, avec des barres obliques</param>
    /// <returns><c>true</c> si le chemin est tenu à l'écart</returns>
    public static bool IsExcluded(IReadOnlyCollection<string> exclusions, string relative)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(relative);

        foreach (string exclusion in exclusions)
        {
            if (relative.Equals(exclusion, StringComparison.OrdinalIgnoreCase)
                || (relative.Length > exclusion.Length
                    && relative[exclusion.Length] == '/'
                    && relative.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Écarte au besoin les sorties de l'outil, avant tout accès au contenu.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <returns><c>true</c> si le fichier reste candidat</returns>
    private bool IsWanted(string path)
    {
        return !SkipProducedFiles || !OutputNaming.IsProduced(Path.GetFileName(path));
    }

    /// <summary>
    /// Donne le chemin d'une entrée relatif à la racine explorée, avec des
    /// barres obliques.
    /// </summary>
    /// <param name="root">Racine explorée, en chemin absolu</param>
    /// <param name="full">Chemin absolu de l'entrée</param>
    /// <returns>Le chemin relatif</returns>
    private static string RelativeOf(string root, string full)
    {
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    /// <summary>
    /// Indique si une entrée est un lien symbolique ou une jonction. Un point
    /// d'analyse qui n'en est pas un — un fichier à la demande d'un service de
    /// synchronisation — n'en est pas un ; un lien illisible est tenu pour un
    /// lien, dans le doute.
    /// </summary>
    /// <param name="entry">Entrée examinée</param>
    /// <param name="target">Ce que le lien désigne, s'il a pu être lu</param>
    /// <returns><c>true</c> pour un lien à ne pas suivre</returns>
    private static bool IsLink(ref FileSystemEntry entry, out string? target)
    {
        target = null;
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            target = entry.ToFileSystemInfo().LinkTarget;
            return target is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
