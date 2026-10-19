using System.Globalization;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Terme propre à l'organisation, avec la nature qu'on lui attribue.
/// </summary>
/// <param name="Text">Libellé exact, tel qu'il s'écrit</param>
/// <param name="Category">Nature du terme, qui détermine sa famille de pseudonyme</param>
public sealed record VocabularyTerm(string Text, SpanCategory Category);

/// <summary>
/// Liste des termes propres à l'organisation : raisons sociales, noms de
/// clients, de personnes, de sites, de projets et de domaines internes.
///
/// Ce fichier est un composant de premier ordre du produit, et non un réglage
/// annexe. Un nom d'hôte nu ou un patronyme sont lexicalement indiscernables
/// d'un mot ordinaire : aucune expression régulière ne peut les trouver sans
/// noyer l'utilisateur de faux positifs. La liste est donc la principale source
/// de détection réelle sur ces catégories, et elle s'enrichit à chaque revue.
///
/// Le format du fichier est délibérément trivial — un terme par ligne,
/// éventuellement suivi de <c>=&gt; catégorie</c> — pour rester modifiable à la
/// main, y compris par quelqu'un qui n'ouvrira jamais l'application.
/// </summary>
public sealed class Vocabulary
{
    /// <summary>
    /// Longueur minimale d'un terme. En deçà, le risque de capturer une
    /// abréviation technique ou une syllabe l'emporte sur le gain.
    /// </summary>
    public const int MinimumTermLength = 3;

    private readonly Dictionary<string, VocabularyTerm> _terms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Termes enregistrés.</summary>
    public IReadOnlyCollection<VocabularyTerm> Terms => _terms.Values;

    /// <summary>
    /// Ajoute ou remplace un terme.
    /// </summary>
    /// <param name="text">Libellé du terme</param>
    /// <param name="category">Nature à lui attribuer</param>
    /// <returns><c>true</c> si le terme a été retenu</returns>
    public bool Add(string text, SpanCategory category = SpanCategory.Organization)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length < MinimumTermLength)
        {
            return false;
        }

        _terms[trimmed] = new VocabularyTerm(trimmed, category);
        return true;
    }

    /// <summary>
    /// Retire un terme de la liste.
    /// </summary>
    /// <param name="text">Libellé du terme à retirer</param>
    /// <returns><c>true</c> si le terme y figurait</returns>
    public bool Remove(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && _terms.Remove(text.Trim());
    }

    /// <summary>
    /// Indique si un terme figure déjà dans la liste.
    /// </summary>
    /// <param name="text">Libellé à chercher</param>
    /// <returns><c>true</c> si le terme est connu</returns>
    public bool Contains(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && _terms.ContainsKey(text.Trim());
    }

    /// <summary>
    /// Produit une copie indépendante du vocabulaire.
    /// </summary>
    /// <returns>Un vocabulaire identique, qu'aucun ajout ultérieur à l'original n'atteint</returns>
    public Vocabulary Clone()
    {
        Vocabulary copy = new();
        foreach (KeyValuePair<string, VocabularyTerm> pair in _terms)
        {
            copy._terms[pair.Key] = pair.Value;
        }

        return copy;
    }

    /// <summary>
    /// Lit un fichier de vocabulaire. Les lignes vides et celles commençant par
    /// un croisillon sont ignorées, ce qui permet d'y laisser des commentaires.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <returns>Le vocabulaire chargé, vide si le fichier n'existe pas</returns>
    public static Vocabulary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Vocabulary vocabulary = new();
        if (!File.Exists(path))
        {
            return vocabulary;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int separator = trimmed.IndexOf("=>", StringComparison.Ordinal);
            if (separator < 0)
            {
                vocabulary.Add(trimmed);
                continue;
            }

            string text = trimmed[..separator].Trim();
            string categoryName = trimmed[(separator + 2)..].Trim();
            SpanCategory category = Enum.TryParse(categoryName, ignoreCase: true, out SpanCategory parsed)
                ? parsed
                : SpanCategory.Organization;

            vocabulary.Add(text, category);
        }

        return vocabulary;
    }

    /// <summary>
    /// Écrit le vocabulaire sur disque, trié par libellé pour que le fichier
    /// reste comparable d'une version à l'autre.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        List<string> lines =
        [
            "# Termes propres à l'organisation, un par ligne.",
            "# Forme : « terme » ou « terme => catégorie ».",
            "# Catégories usuelles : Person, Client, Site, Organization, Hostname, DomainName.",
            string.Empty,
        ];

        foreach (VocabularyTerm term in _terms.Values.OrderBy(term => term.Text, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{term.Text} => {term.Category}"));
        }

        File.WriteAllLines(path, lines);
    }
}
