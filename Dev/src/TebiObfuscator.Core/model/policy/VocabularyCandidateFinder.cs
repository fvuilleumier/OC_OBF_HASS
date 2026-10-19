using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Terme proposé à l'utilisateur pour enrichir le vocabulaire.
/// </summary>
/// <param name="Text">Libellé repéré</param>
/// <param name="Occurrences">Nombre d'apparitions dans le lot analysé</param>
/// <param name="SuggestedCategory">Nature probable, à confirmer</param>
/// <param name="SampleLine">Ligne d'exemple, pour juger sur pièce</param>
public sealed record VocabularyCandidate(
    string Text,
    int Occurrences,
    SpanCategory SuggestedCategory,
    string SampleLine);

/// <summary>
/// Propose des termes à ajouter au vocabulaire de l'organisation.
///
/// Cette heuristique ne masque jamais rien d'elle-même, et c'est un choix de
/// conception : elle repère des mots qui « ressemblent » à des noms propres ou
/// à des identifiants internes, ce qui est trop incertain pour agir sans
/// confirmation. Son rôle est d'alimenter l'écran de revue, où un clic promeut
/// un terme dans le vocabulaire — c'est ainsi que la liste, principale source
/// de détection sur les noms propres, se construit dans la durée au lieu de
/// devoir être écrite d'avance.
/// </summary>
public static partial class VocabularyCandidateFinder
{
    private const int MinimumOccurrences = 2;
    private const int TimeoutMilliseconds = 2000;

    /// <summary>
    /// Mots capitalisés trop courants pour être des noms propres. La liste
    /// reste volontairement courte : le filtre décisif est le nombre
    /// d'occurrences et la confirmation par l'utilisateur.
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Interface", "Serveur", "Server", "Service", "Adresse", "Address",
        "Réseau", "Network", "Utilisateur", "User", "Groupe", "Group", "Fichier", "File",
        "Note", "Notes", "Attention", "Remarque", "Exemple", "Example", "Description",
        "Version", "Type", "Name", "Nom", "Date", "Heure", "Statut", "Status", "Erreur",
        "Error", "Warning", "Information", "Default", "Enabled", "Disabled", "None", "True",
        "False", "Null", "Total", "Test", "Production", "Documentation", "Sommaire",
    };

    /// <summary>Mot commençant par une majuscule, éventuellement composé.</summary>
    [GeneratedRegex(@"(?<![\w\-])\p{Lu}[\p{L}]{3,}(?:[ \-]\p{Lu}[\p{L}]{2,})?(?![\w\-])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex CapitalizedWord();

    /// <summary>Identifiant interne de la forme « préfixe-suffixe », typique d'un nom de machine.</summary>
    [GeneratedRegex(@"(?<![\w\-.])[a-z]{2,5}-[a-z0-9]{2,}(?:-[a-z0-9]+)*(?![\w\-])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex InternalIdentifier();

    /// <summary>
    /// Recherche des termes candidats dans un lot de textes.
    /// </summary>
    /// <param name="texts">Contenus des fichiers analysés</param>
    /// <param name="vocabulary">Vocabulaire déjà constitué, dont les termes sont exclus</param>
    /// <param name="allowlist">Liste blanche, dont les termes sont exclus</param>
    /// <returns>Les candidats, du plus fréquent au moins fréquent</returns>
    public static IReadOnlyList<VocabularyCandidate> Find(
        IEnumerable<string> texts,
        Vocabulary vocabulary,
        Allowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(allowlist);

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> samples = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SpanCategory> categories = new(StringComparer.OrdinalIgnoreCase);

        foreach (string text in texts)
        {
            Collect(text, CapitalizedWord(), SpanCategory.Person, counts, samples, categories);
            Collect(text, InternalIdentifier(), SpanCategory.Hostname, counts, samples, categories);
        }

        List<VocabularyCandidate> candidates = [];
        foreach ((string term, int count) in counts)
        {
            if (count < MinimumOccurrences
                || CommonWords.Contains(term)
                || vocabulary.Contains(term)
                || allowlist.Contains(categories[term], term))
            {
                continue;
            }

            candidates.Add(new VocabularyCandidate(term, count, categories[term], samples[term]));
        }

        return [.. candidates
            .OrderByDescending(candidate => candidate.Occurrences)
            .ThenBy(candidate => candidate.Text, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Compte les occurrences d'un motif et retient une ligne d'exemple.
    /// </summary>
    /// <param name="text">Texte à parcourir</param>
    /// <param name="pattern">Motif à appliquer</param>
    /// <param name="category">Nature probable des termes trouvés</param>
    /// <param name="counts">Compteur d'occurrences à compléter</param>
    /// <param name="samples">Lignes d'exemple à compléter</param>
    /// <param name="categories">Natures probables à compléter</param>
    private static void Collect(
        string text,
        Regex pattern,
        SpanCategory category,
        Dictionary<string, int> counts,
        Dictionary<string, string> samples,
        Dictionary<string, SpanCategory> categories)
    {
        foreach (Match match in pattern.Matches(text))
        {
            // Un mot en tête de ligne est presque toujours une directive de
            // configuration — « snmp-server », « name-server » — et non une
            // valeur. Le proposer comme nom propre noierait les vrais candidats.
            if (StartsTheLine(text, match.Index))
            {
                continue;
            }

            string term = match.Value;
            counts[term] = counts.TryGetValue(term, out int previous) ? previous + 1 : 1;

            if (!samples.ContainsKey(term))
            {
                samples[term] = ExtractLine(text, match.Index);
                categories[term] = category;
            }
        }
    }

    /// <summary>
    /// Indique si une position est le premier élément non blanc de sa ligne.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="position">Position à tester</param>
    /// <returns><c>true</c> si rien d'autre qu'un espacement ne précède sur la ligne</returns>
    private static bool StartsTheLine(string text, int position)
    {
        for (int cursor = position - 1; cursor >= 0; cursor--)
        {
            char character = text[cursor];
            if (character == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Extrait la ligne contenant une position donnée, pour l'afficher en
    /// contexte dans l'écran de revue.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="position">Position d'intérêt</param>
    /// <returns>La ligne correspondante, sans ses espaces de bordure</returns>
    private static string ExtractLine(string text, int position)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, Math.Min(position, text.Length - 1))) + 1;
        int end = text.IndexOf('\n', position);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim();
    }
}
