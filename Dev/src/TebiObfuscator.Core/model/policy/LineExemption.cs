using System.Globalization;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Exception posée sur une occurrence précise : cette valeur, à cette ligne, de
/// ce fichier, reste en clair.
///
/// La granularité est volontairement fine. Retirer une valeur des règles la
/// laisserait en clair partout, ce qui est rarement l'intention : on veut
/// généralement garder lisible une adresse d'exemple à un endroit précis, sans
/// exposer la même adresse ailleurs dans le lot — c'est l'affaire de la liste
/// blanche du projet. Une ligne pouvant porter plusieurs fois la même valeur,
/// l'exception retient aussi le rang de l'occurrence sur sa ligne : garder la
/// deuxième <c>10.0.0.1</c> d'une ligne laisse la première masquée.
///
/// La contrepartie est assumée : l'exception est attachée à un numéro de ligne,
/// donc modifier le fichier source au-dessus la décale. Elle vise l'aller-retour
/// d'une session de travail, pas une politique durable — pour cela, il y a la
/// liste blanche. Le rapatriement, qui sait où chaque ligne est passée, recale
/// lui-même les exceptions du fichier qu'il réécrit.
///
/// Le fichier est désigné par son chemin dans la source, précédé d'une barre :
/// <c>/zigbee2mqtt/configuration.yaml</c>. Deux fichiers de même nom, dans deux
/// dossiers, ne partagent ainsi pas leurs exceptions. Une exception posée avant
/// que le chemin ne soit retenu ne porte que le nom du fichier : elle vaut
/// toujours pour tout fichier de ce nom.
/// </summary>
/// <param name="File">Fichier concerné : son chemin dans la source précédé d'une barre, ou son seul nom</param>
/// <param name="Line">Numéro de ligne, à partir de 1</param>
/// <param name="Value">Valeur exacte à laisser en clair</param>
public sealed record LineExemption(string File, int Line, string Value)
{
    /// <summary>Marque d'une clé de fichier qui porte son chemin dans la source.</summary>
    public const char PathKeyMark = '/';

    /// <summary>
    /// Donne la clé sous laquelle un fichier est désigné par ses exceptions :
    /// son chemin relatif à la source, précédé d'une barre, ou son seul nom
    /// s'il est hors de la source.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <param name="sourceRoot">Dossier source du projet</param>
    /// <returns>La clé du fichier</returns>
    public static string KeyFor(string path, string? sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(sourceRoot), Path.GetFullPath(path)).Replace('\\', '/');
            bool outside = relative == ".."
                || relative.StartsWith("../", StringComparison.Ordinal)
                || Path.IsPathRooted(relative);

            if (!outside && relative != ".")
            {
                return PathKeyMark + relative;
            }
        }

        return Path.GetFileName(path);
    }

    /// <summary>
    /// Donne le nom de fichier que portaient les exceptions posées avant que
    /// le chemin ne soit retenu.
    /// </summary>
    /// <param name="fileKey">Clé d'un fichier</param>
    /// <returns>Son nom seul, ou <c>null</c> si la clé est déjà un simple nom</returns>
    public static string? LegacyKeyOf(string fileKey)
    {
        ArgumentNullException.ThrowIfNull(fileKey);
        return fileKey.Length > 0 && fileKey[0] == PathKeyMark ? fileKey[(fileKey.LastIndexOf('/') + 1)..] : null;
    }

    /// <summary>
    /// Donne la forme lisible d'une clé de fichier : son chemin sans la barre
    /// de tête.
    /// </summary>
    /// <param name="fileKey">Clé d'un fichier</param>
    /// <returns>Le chemin ou le nom à afficher</returns>
    public static string DisplayFile(string fileKey)
    {
        ArgumentNullException.ThrowIfNull(fileKey);
        return fileKey.TrimStart(PathKeyMark);
    }

    /// <summary>
    /// Indique si l'exception désigne le fichier d'une clé donnée : par son
    /// chemin, ou, pour une exception ancienne, par son seul nom.
    /// </summary>
    /// <param name="fileKey">Clé du fichier examiné</param>
    /// <returns><c>true</c> si l'exception vaut pour ce fichier</returns>
    public bool AppliesTo(string fileKey)
    {
        ArgumentNullException.ThrowIfNull(fileKey);
        return string.Equals(File, fileKey, StringComparison.Ordinal)
            || string.Equals(File, LegacyKeyOf(fileKey), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rang de l'occurrence sur sa ligne, à partir de 1. Zéro désigne toutes
    /// les occurrences de la valeur sur la ligne : c'est la forme des
    /// exceptions posées avant que le rang ne soit retenu.
    /// </summary>
    public int Occurrence { get; init; }

    /// <summary>
    /// Compose la clé de recherche d'une exception valant pour toute la ligne.
    /// </summary>
    /// <param name="file">Nom du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <returns>La clé correspondante</returns>
    public static string KeyOf(string file, int line, string value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{file}\u0000{line}\u0000{value}");
    }

    /// <summary>
    /// Compose la clé de recherche d'une exception.
    /// </summary>
    /// <param name="file">Nom du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <param name="occurrence">Rang de l'occurrence sur la ligne, zéro pour toute la ligne</param>
    /// <returns>La clé correspondante</returns>
    public static string KeyOf(string file, int line, string value, int occurrence)
    {
        return occurrence <= 0
            ? KeyOf(file, line, value)
            : string.Create(CultureInfo.InvariantCulture, $"{file}\u0000{line}#{occurrence}\u0000{value}");
    }

    /// <summary>Clé de recherche de cette exception.</summary>
    public string Key => KeyOf(File, Line, Value, Occurrence);

    /// <summary>
    /// Forme enregistrée dans les réglages du projet, lisible à la main.
    /// </summary>
    /// <returns>La ligne « fichier|ligne|valeur », ou « fichier|ligne#rang|valeur »</returns>
    public string ToSetting()
    {
        return Occurrence > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{File}|{Line}#{Occurrence}|{Value}")
            : string.Create(CultureInfo.InvariantCulture, $"{File}|{Line}|{Value}");
    }

    /// <summary>
    /// Relit une exception depuis sa forme enregistrée.
    ///
    /// La séparation se fait sur les deux premières barres seulement : une
    /// valeur peut parfaitement en contenir, un chemin ou un nom de fichier non.
    /// </summary>
    /// <param name="setting">Ligne lue dans les réglages</param>
    /// <param name="exception">Exception reconstituée, le cas échéant</param>
    /// <returns><c>true</c> si la ligne était bien formée</returns>
    public static bool TryParse(string setting, out LineExemption exception)
    {
        exception = new LineExemption(string.Empty, 0, string.Empty);
        if (string.IsNullOrWhiteSpace(setting))
        {
            return false;
        }

        int first = setting.IndexOf('|', StringComparison.Ordinal);
        if (first <= 0)
        {
            return false;
        }

        int second = setting.IndexOf('|', first + 1);
        if (second < 0)
        {
            return false;
        }

        string position = setting[(first + 1)..second];
        int hash = position.IndexOf('#', StringComparison.Ordinal);
        int occurrence = 0;

        if (hash >= 0
            && (!int.TryParse(position[(hash + 1)..], CultureInfo.InvariantCulture, out occurrence) || occurrence < 1))
        {
            return false;
        }

        if (!int.TryParse(hash >= 0 ? position[..hash] : position, CultureInfo.InvariantCulture, out int line))
        {
            return false;
        }

        exception = new LineExemption(setting[..first], line, setting[(second + 1)..]) { Occurrence = occurrence };
        return true;
    }

    /// <summary>
    /// Donne le rang, sur sa ligne, de l'occurrence d'une valeur qui commence à
    /// une position donnée : 1 pour la première de la ligne, 2 pour la suivante.
    ///
    /// Les occurrences sont comptées dans le texte, sans recouvrement, et non
    /// parmi les valeurs repérées : le rang ne dépend ainsi ni des détecteurs
    /// ni de l'ordre dans lequel ils rendent leurs résultats, et l'écran, le
    /// moteur et le contrôle final le calculent de la même façon.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="position">Début de l'occurrence</param>
    /// <param name="value">Valeur concernée</param>
    /// <returns>Le rang de l'occurrence, à partir de 1</returns>
    public static int OccurrenceAt(string text, int position, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);

        int end = Math.Min(position, text.Length);
        if (value.Length == 0 || end <= 0)
        {
            return 1;
        }

        return 1 + Occurrences(text, text.LastIndexOf('\n', end - 1) + 1, end, value).Count();
    }

    /// <summary>
    /// Retrouve, sur une ligne, l'occurrence d'une valeur de rang donné.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="lineStart">Première position de la ligne</param>
    /// <param name="lineEnd">Position qui suit le dernier caractère de la ligne</param>
    /// <param name="value">Valeur recherchée</param>
    /// <param name="occurrence">Rang voulu, à partir de 1 ; zéro pour toutes les occurrences</param>
    /// <returns>Les positions de début des occurrences visées</returns>
    public static IEnumerable<int> Locate(string text, int lineStart, int lineEnd, string value, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);

        return LocateRanked(text, lineStart, lineEnd, value, occurrence);
    }

    /// <summary>
    /// Parcourt les occurrences visées par <see cref="Locate"/>, une fois ses
    /// arguments vérifiés.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="lineStart">Première position de la ligne</param>
    /// <param name="lineEnd">Position qui suit le dernier caractère de la ligne</param>
    /// <param name="value">Valeur recherchée</param>
    /// <param name="occurrence">Rang voulu, à partir de 1 ; zéro pour toutes les occurrences</param>
    /// <returns>Les positions de début des occurrences visées</returns>
    private static IEnumerable<int> LocateRanked(string text, int lineStart, int lineEnd, string value, int occurrence)
    {
        int rank = 0;
        foreach (int position in Occurrences(text, Math.Max(0, lineStart), lineEnd, value))
        {
            rank++;
            if (occurrence <= 0 || rank == occurrence)
            {
                yield return position;
            }

            if (occurrence > 0 && rank >= occurrence)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Énumère, sans recouvrement, les occurrences d'une valeur qui commencent
    /// dans un intervalle. La recherche ne déborde pas de l'intervalle — d'une
    /// ligne, en pratique — même quand la valeur n'y figure pas : sans cette
    /// borne, chaque appel parcourrait le reste du fichier.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <param name="start">Début de l'intervalle</param>
    /// <param name="end">Position qui suit l'intervalle : une occurrence doit commencer avant</param>
    /// <param name="value">Valeur recherchée</param>
    /// <returns>Les positions de début, croissantes</returns>
    private static IEnumerable<int> Occurrences(string text, int start, int end, string value)
    {
        if (value.Length == 0)
        {
            yield break;
        }

        // Une occurrence qui commence avant la fin de l'intervalle peut le
        // déborder : c'est le cas d'une valeur sur plusieurs lignes.
        int limit = Math.Min(text.Length, end + value.Length - 1);
        int cursor = start;

        while (cursor < end && limit - cursor >= value.Length)
        {
            int found = text.IndexOf(value, cursor, limit - cursor, StringComparison.Ordinal);
            if (found < 0 || found >= end)
            {
                yield break;
            }

            yield return found;
            cursor = found + value.Length;
        }
    }
}
