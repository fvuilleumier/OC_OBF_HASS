namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Nommage des fichiers produits par l'obfuscation.
///
/// Un fichier obfusqué porte le nom de son original suivi de « _obfusc » :
/// posés côte à côte, ou retrouvés des semaines plus tard dans un dossier de
/// téléchargements, l'original et sa version pseudonymisée ne se confondent
/// pas — et c'est la version pseudonymisée, elle seule, qu'on envoie à l'IA.
/// Sa légende, destinée à l'IA elle aussi, reprend ce nom suivi de
/// « .legend.md » : les deux fichiers se suivent dans l'explorateur et se
/// joignent ensemble à la conversation. L'extension d'origine est conservée
/// dans le nom de la légende, pour que deux fichiers ne différant que par leur
/// extension — <c>app.json</c> et <c>app.yaml</c> — gardent chacun la sienne.
/// </summary>
public static class OutputNaming
{
    /// <summary>Suffixe ajouté au nom d'un fichier obfusqué.</summary>
    public const string Suffix = "_obfusc";

    /// <summary>Terminaison du nom d'une légende.</summary>
    public const string LegendExtension = ".legend.md";

    /// <summary>
    /// Donne le nom d'un fichier une fois obfusqué : le suffixe s'insère avant
    /// l'extension, que l'éditeur et l'IA reconnaissent ainsi toujours.
    /// </summary>
    /// <param name="fileName">Nom du fichier d'origine, sans dossier</param>
    /// <returns>Le nom du fichier obfusqué</returns>
    public static string ObfuscatedName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];

        // « .env » n'a pas d'extension, seulement un nom qui commence par un
        // point : le suffixe va à la fin.
        return stem.Length == 0 ? fileName + Suffix : stem + Suffix + extension;
    }

    /// <summary>
    /// Retire le suffixe d'un nom de fichier obfusqué : c'est le nom sous
    /// lequel le fichier revient dans la source. Un nom sans suffixe est rendu
    /// tel quel.
    /// </summary>
    /// <param name="fileName">Nom du fichier obfusqué, sans dossier</param>
    /// <returns>Le nom sans le suffixe</returns>
    public static string ClearName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && fileName.Length > Suffix.Length)
        {
            return fileName[..^Suffix.Length];
        }

        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        return stem.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && stem.Length > Suffix.Length
            ? stem[..^Suffix.Length] + extension
            : fileName;
    }

    /// <summary>
    /// Indique si un nom de fichier est celui d'une légende.
    /// </summary>
    /// <param name="fileName">Nom du fichier, sans dossier</param>
    /// <returns><c>true</c> pour une légende</returns>
    public static bool IsLegend(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.EndsWith(LegendExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Donne le chemin de la légende d'un fichier obfusqué.
    /// </summary>
    /// <param name="obfuscatedPath">Chemin du fichier obfusqué</param>
    /// <returns>Le chemin de sa légende, à côté de lui</returns>
    public static string LegendPath(string obfuscatedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(obfuscatedPath);
        return obfuscatedPath + LegendExtension;
    }

    /// <summary>
    /// Indique si un fichier est une sortie de l'outil : un fichier obfusqué ou
    /// une légende. Une sortie n'est jamais une entrée — l'obfusquer de nouveau
    /// produirait un « _obfusc_obfusc » sans objet.
    /// </summary>
    /// <param name="fileName">Nom du fichier, sans dossier</param>
    /// <returns><c>true</c> s'il s'agit d'une sortie</returns>
    public static bool IsProduced(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.EndsWith(LegendExtension, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(fileName);
        return fileName[..^extension.Length].EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }
}
