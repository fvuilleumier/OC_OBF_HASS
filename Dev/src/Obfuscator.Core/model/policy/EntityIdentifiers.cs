using System.Text.RegularExpressions;

namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Identifiants d'entités Home Assistant gardés en clair :
/// <c>sensor.salon_temperature</c>, <c>binary_sensor.porte_garage</c>,
/// <c>switch.prise_bureau</c>…
///
/// Ces identifiants sont la grammaire même d'une configuration domotique : les
/// automatisations, les tableaux de bord et les modèles s'y réfèrent partout.
/// Une règle générale — la clé <c>entity_id</c> lue comme un identifiant, une
/// chaîne jugée aléatoire — les remplaçait par des jetons, et l'IA ne pouvait
/// plus raisonner sur ce qui pilote quoi. Ce qui suit le domaine est donc gardé
/// en clair partout, par défaut, sauf les mots qu'on a explicitement demandé de
/// masquer (vocabulaire, liste noire) : <c>sensor.chambre_emma_battery</c>
/// garde sa forme, et seul « emma » y est remplacé.
///
/// Les domaines concernés sont un réglage du projet, modifiable à la main.
/// </summary>
public sealed class EntityIdentifiers
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Domaines gardés d'office : capteurs, capteurs binaires, mises à jour,
    /// nombres, interrupteurs et boutons.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDomains = ["sensor", "binary_sensor", "update", "number", "switch", "button"];

    /// <summary>Aucun domaine : rien n'est gardé à ce titre.</summary>
    public static readonly EntityIdentifiers None = new([]);

    private readonly Regex? _pattern;

    /// <summary>
    /// Construit la règle à partir des domaines à garder.
    /// </summary>
    /// <param name="domains">Domaines, avec ou sans leur point final ; les noms invalides sont ignorés</param>
    public EntityIdentifiers(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // Un domaine Home Assistant s'écrit en minuscules, chiffres et
        // soulignés : toute autre forme serait une faute de frappe, et
        // l'accepter ferait garder en clair n'importe quoi.
        List<string> valid = [.. domains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0 && domain.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_'))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(domain => domain.Length)];

        if (valid.Count == 0)
        {
            return;
        }

        // Le domaine n'est collé à aucune lettre, aucun chiffre, aucun souligné
        // ni trait d'union : « binary_sensor.x » n'est pas lu « sensor.x », ni
        // « my_switch.x » « switch.x ». Un point avant reste permis, pour les
        // modèles (« states.sensor.salon.state »).
        _pattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}_\-])(?:{string.Join('|', valid.Select(Regex.Escape))})\.[\p{{L}}\p{{N}}_]+",
            RegexOptions.CultureInvariant,
            MatchTimeout);
    }

    /// <summary>Vrai si aucun domaine n'est gardé.</summary>
    public bool IsEmpty => _pattern is null;

    /// <summary>
    /// Repère les identifiants d'entités d'un texte.
    /// </summary>
    /// <param name="text">Texte à parcourir</param>
    /// <returns>Leurs positions, à interroger pour chaque portion repérée</returns>
    public EntityMask Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_pattern is null)
        {
            return EntityMask.Empty;
        }

        List<(int Start, int End)> ranges = [];
        foreach (Match match in _pattern.Matches(text))
        {
            ranges.Add((match.Index, match.Index + match.Length));
        }

        return ranges.Count == 0 ? EntityMask.Empty : new EntityMask(text, ranges);
    }

    /// <summary>
    /// Efface les identifiants d'entités d'un texte, à longueur égale, pour
    /// que les heuristiques de suggestion ne les lisent pas.
    /// </summary>
    /// <param name="text">Texte à parcourir</param>
    /// <returns>Le texte, identifiants remplacés par des espaces</returns>
    public string Blank(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _pattern is null ? text : _pattern.Replace(text, match => new string(' ', match.Length));
    }
}

/// <summary>
/// Positions des identifiants d'entités d'un texte.
/// </summary>
public sealed class EntityMask
{
    private readonly string _text;
    private readonly List<(int Start, int End)> _ranges;

    /// <summary>Aucun identifiant.</summary>
    public static readonly EntityMask Empty = new(string.Empty, []);

    /// <summary>
    /// Construit le relevé.
    /// </summary>
    /// <param name="text">Texte parcouru</param>
    /// <param name="ranges">Identifiants, par position croissante et disjoints</param>
    internal EntityMask(string text, List<(int Start, int End)> ranges)
    {
        _text = text;
        _ranges = ranges;
    }

    /// <summary>Vrai si le texte ne contient aucun identifiant.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>
    /// Indique si une portion ne tient qu'à des identifiants d'entités : toutes
    /// ses lettres et tous ses chiffres en font partie. La valeur d'une clé
    /// <c>entity_id: sensor.a, sensor.b</c> est ainsi couverte, mais pas une
    /// phrase qui en cite un.
    /// </summary>
    /// <param name="start">Début de la portion</param>
    /// <param name="length">Longueur de la portion</param>
    /// <returns><c>true</c> si la portion est gardée en clair à ce titre</returns>
    public bool Covers(int start, int length)
    {
        if (_ranges.Count == 0 || length <= 0)
        {
            return false;
        }

        int end = Math.Min(start + length, _text.Length);
        int index = FirstEndingAfter(start);
        bool touched = false;

        for (int position = start; position < end; position++)
        {
            // Les identifiants sont triés : on avance dans la liste avec la
            // position au lieu de la reparcourir.
            while (index < _ranges.Count && _ranges[index].End <= position)
            {
                index++;
            }

            bool inside = index < _ranges.Count && _ranges[index].Start <= position;
            touched |= inside;

            if (!inside && char.IsLetterOrDigit(_text[position]))
            {
                return false;
            }
        }

        return touched;
    }

    /// <summary>
    /// Cherche le premier identifiant qui se termine après une position.
    /// </summary>
    /// <param name="position">Position examinée</param>
    /// <returns>Son rang, ou le nombre d'identifiants s'il n'y en a aucun</returns>
    private int FirstEndingAfter(int position)
    {
        int low = 0;
        int high = _ranges.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_ranges[middle].End <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
