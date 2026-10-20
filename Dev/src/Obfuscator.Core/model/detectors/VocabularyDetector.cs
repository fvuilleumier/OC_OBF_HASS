using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Detectors;

/// <summary>
/// Repère dans le texte les termes du vocabulaire de l'organisation.
///
/// C'est le second détecteur le plus productif après celui qui juge les noms de
/// clés : il est le seul capable de trouver un patronyme ou un nom d'hôte nu,
/// que rien ne distingue lexicalement d'un mot ordinaire.
///
/// L'implémentation compile une expression unique alternant tous les termes,
/// triés du plus long au plus court pour que « Ville de Genève » l'emporte sur
/// « Genève ». Un automate d'Aho-Corasick serait asymptotiquement meilleur,
/// mais l'expression compilée reste largement suffisante pour les quelques
/// centaines de termes qu'un vocabulaire d'entreprise comporte en pratique.
///
/// Un terme qui a la forme d'une adresse IPv4 ou de son début —
/// <c>192.168.1.25</c>, <c>192.168.1</c>, <c>10.10</c> — est une adresse,
/// quelle que soit la nature qu'on lui a donnée : il est repéré comme tel, et
/// reçoit un pseudonyme qui a encore la forme d'une adresse. Il a pour cela
/// ses propres bornes, celles d'une adresse : un début d'adresse n'est retrouvé
/// qu'en tête, et <c>10.10</c> désigne bien le réseau de <c>10.10.5.4</c>, mais
/// rien dans <c>172.16.10.10</c>.
/// </summary>
public sealed class VocabularyDetector : IDetector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, SpanCategory> _categoriesByTerm = new(StringComparer.Ordinal);
    private readonly Regex? _pattern;
    private readonly Regex? _addressPattern;

    /// <summary>
    /// Construit le détecteur à partir d'un vocabulaire.
    /// </summary>
    /// <param name="vocabulary">Vocabulaire de l'organisation</param>
    public VocabularyDetector(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);

        List<string> alternatives = [];
        List<string> addresses = [];
        foreach (VocabularyTerm term in vocabulary.Terms.OrderByDescending(term => term.Text.Length))
        {
            if (IpAddressTools.TryParseAddressOrPrefix(term.Text, out _, out int octets))
            {
                // Une adresse complète ne se prolonge pas : 1.2.3.4 n'est pas
                // repérée dans 1.2.3.4.5. Un début d'adresse, si, par un point.
                addresses.Add(octets == 4 ? $@"{Regex.Escape(term.Text)}(?!\.\d)" : Regex.Escape(term.Text));
                continue;
            }

            // La clé est repliée : une occurrence écrite « Geneve » doit
            // retrouver la catégorie du terme « Genève ».
            _categoriesByTerm[TextFolding.Fold(term.Text)] = term.Category;
            alternatives.Add(ToAccentInsensitivePattern(term.Text));
        }

        // Les bornes n'excluent que les lettres et les chiffres. Un terme est
        // donc repéré à l'intérieur d'un identifiant composé — nom de domaine
        // (point), chemin ou slug (trait d'union), identifiant d'entité ou
        // variable (souligné) : « chambre-emma », « sensor.chambre_emma_battery ».
        // Ces formes sont partout dans les configurations domotiques et
        // applicatives, et les exclure laissait passer en clair précisément les
        // noms qu'on avait demandé de masquer. Un terme collé à d'autres lettres,
        // lui, reste ignoré : « Emma » ne doit pas être repéré dans « Emmanuel ».
        if (alternatives.Count > 0)
        {
            _pattern = new Regex(
                $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', alternatives)})(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }

        // Une adresse n'est jamais précédée d'un point : celui-ci ferait du
        // terme la fin d'une autre adresse.
        if (addresses.Count > 0)
        {
            _addressPattern = new Regex(
                $@"(?<![\p{{L}}\p{{N}}.])(?:{string.Join('|', addresses)})(?![\p{{L}}\p{{N}}])",
                RegexOptions.CultureInvariant,
                MatchTimeout);
        }
    }

    /// <inheritdoc />
    public string RuleId => "vocabulary";

    /// <summary>
    /// Traduit un terme en motif indifférent aux accents.
    ///
    /// Les exports d'équipements réseau perdent régulièrement les accents —
    /// « Genève » y devient « Geneve » — alors que les notes internes les
    /// conservent. Sans cette tolérance, il faudrait inscrire chaque graphie
    /// dans le vocabulaire, et un oubli se traduirait par une fuite.
    /// </summary>
    /// <param name="term">Terme du vocabulaire</param>
    /// <returns>Le motif correspondant, échappé</returns>
    private static string ToAccentInsensitivePattern(string term)
    {
        System.Text.StringBuilder pattern = new(term.Length * 2);

        foreach (char character in term)
        {
            string? variants = TextFolding.VariantsOf(character);
            if (variants is not null)
            {
                pattern.Append('[').Append(variants).Append(']');
                continue;
            }

            pattern.Append(Regex.Escape(character.ToString()));
        }

        return pattern.ToString();
    }

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _pattern is null && _addressPattern is null ? [] : Enumerate(context);
    }

    /// <summary>
    /// Parcourt les occurrences des termes du vocabulaire.
    /// </summary>
    /// <param name="context">Contexte d'analyse validé</param>
    /// <returns>Un span par occurrence</returns>
    private IEnumerable<DetectedSpan> Enumerate(DetectionContext context)
    {
        if (_pattern is not null)
        {
            foreach (Match match in _pattern.Matches(context.Text))
            {
                SpanCategory category = _categoriesByTerm.TryGetValue(TextFolding.Fold(match.Value), out SpanCategory known)
                    ? known
                    : SpanCategory.Organization;

                yield return DetectedSpan.Create(
                    match.Index,
                    match.Length,
                    category,
                    match.Value,
                    $"{RuleId}.{category}");
            }
        }

        if (_addressPattern is not null)
        {
            foreach (Match match in _addressPattern.Matches(context.Text))
            {
                yield return DetectedSpan.Create(
                    match.Index,
                    match.Length,
                    SpanCategory.IpV4,
                    match.Value,
                    $"{RuleId}.{SpanCategory.IpV4}");
            }
        }
    }
}
