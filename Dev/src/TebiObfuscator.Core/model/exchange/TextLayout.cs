using System.Text;

namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Texte découpé en lignes, chacune avec sa fin de ligne.
///
/// La fusion compare les lignes sans leur fin : une IA qui réécrit tout un
/// fichier en fins de ligne Unix n'a rien changé au contenu, et le contrôle git
/// du dépôt en clair ne doit pas montrer chaque ligne comme modifiée. Les fins
/// d'origine sont gardées à part, pour que les lignes reprises de la source
/// gardent exactement les leurs.
/// </summary>
public sealed class TextLayout
{
    /// <summary>
    /// Construit le découpage.
    /// </summary>
    /// <param name="lines">Lignes, sans leur fin</param>
    /// <param name="terminators">Fin de chaque ligne ; vide pour une dernière ligne qui n'en a pas</param>
    private TextLayout(List<string> lines, List<string> terminators)
    {
        Lines = lines;
        Terminators = terminators;
        DominantTerminator = terminators
            .Where(terminator => terminator.Length > 0)
            .GroupBy(terminator => terminator, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? "\n";
    }

    /// <summary>Lignes du texte, sans leur fin.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Fin de chaque ligne : <c>\r\n</c>, <c>\n</c>, <c>\r</c>, ou vide pour une dernière ligne qui n'en a pas.</summary>
    public IReadOnlyList<string> Terminators { get; }

    /// <summary>Fin de ligne la plus fréquente du texte, <c>\n</c> à défaut.</summary>
    public string DominantTerminator { get; }

    /// <summary>Vrai si le texte se termine par une fin de ligne.</summary>
    public bool EndsWithTerminator => Terminators.Count > 0 && Terminators[^1].Length > 0;

    /// <summary>
    /// Découpe un texte en lignes.
    /// </summary>
    /// <param name="text">Texte à découper</param>
    /// <returns>Le découpage ; un texte vide n'a aucune ligne</returns>
    public static TextLayout Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> lines = [];
        List<string> terminators = [];
        int start = 0;
        int position = 0;

        while (position < text.Length)
        {
            char character = text[position];
            if (character is not ('\n' or '\r'))
            {
                position++;
                continue;
            }

            int length = character == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;
            lines.Add(text[start..position]);
            terminators.Add(text.Substring(position, length));
            position += length;
            start = position;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
            terminators.Add(string.Empty);
        }

        return new TextLayout(lines, terminators);
    }

    /// <summary>
    /// Recompose un texte à partir de lignes et de leurs fins.
    /// </summary>
    /// <param name="lines">Lignes et fins, dans l'ordre</param>
    /// <returns>Le texte</returns>
    public static string Compose(IEnumerable<(string Line, string Terminator)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        StringBuilder builder = new();
        foreach ((string line, string terminator) in lines)
        {
            builder.Append(line).Append(terminator);
        }

        return builder.ToString();
    }
}
