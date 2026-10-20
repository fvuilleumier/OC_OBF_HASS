using System.Buffers;
using Obfuscator.Core.Model.Detectors;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Mapping;

/// <summary>
/// Occurrence, dans un texte, d'une valeur réelle connue du coffre.
/// </summary>
/// <param name="Start">Position du début</param>
/// <param name="Length">Longueur de l'occurrence</param>
/// <param name="Entry">Entrée du coffre à laquelle elle correspond</param>
public sealed record KnownValueOccurrence(int Start, int Length, MappingEntry Entry);

/// <summary>
/// Retrouve dans un texte les valeurs réelles connues du coffre, avec une règle
/// de comparaison adaptée à chaque nature de valeur.
///
/// Une recherche naïve — la valeur comme sous-chaîne, sans tenir compte de sa
/// nature — se trompe dans les deux sens. Elle voit des fuites qui n'en sont
/// pas : un port masqué, <c>8443</c>, retrouvé dans <c>/api/items/8443</c>, ou
/// un prénom court retrouvé au milieu d'un autre mot. Et elle en manque : un
/// nom écrit « Naël » dans le coffre n'est pas retrouvé écrit « nael » dans un
/// identifiant, alors que c'est bien lui. Trois règles sont donc appliquées :
///
/// - les noms (personnes, sociétés, sites, machines, domaines) sont comparés
///   sans tenir compte ni des accents ni de la casse, et doivent former un mot
///   entier — ce qui les autorise à l'intérieur d'un identifiant composé
///   (<c>chambre_nael_lampe</c>) mais pas au milieu d'un autre mot ;
/// - les secrets sont comparés à l'identique, sans bornes : un mot de passe ne
///   fuit que s'il réapparaît exactement, où qu'il soit — sauf un secret court,
///   compte ou mot de passe de quelques lettres, qui doit former un élément
///   entier : <c>test</c> n'est pas retrouvé dans <c>use_test_url</c>, ni
///   <c>hass</c> dans <c>hassio</c>, où il ne serait qu'une coïncidence ;
/// - les valeurs techniques (adresses, identifiants normalisés) sont comparées
///   sans tenir compte de la casse, et doivent former un mot entier : une
///   adresse IP n'est pas retrouvée dans une adresse plus longue ;
/// - un numéro de port n'est retrouvé qu'en position de port, derrière un
///   deux-points ;
/// - une coordonnée n'est retrouvée qu'en position de coordonnée, sous une
///   clé qui la nomme : <c>430</c> n'est une altitude que derrière
///   <c>elevation</c> ;
/// - un début d'adresse — <c>192.168.1</c>, <c>10.10</c> — n'est retrouvé
///   qu'en tête d'adresse, jamais précédé d'un point : <c>10.10</c> à la fin
///   de <c>172.16.10.10</c> n'est pas lui.
///
/// La recherche se fait en une passe par règle, quel que soit le nombre de
/// valeurs du coffre : c'est ce qui permet de la mener à chaque analyse et à
/// chaque obfuscation sans ralentir l'une ou l'autre.
/// </summary>
public sealed class KnownValueScanner
{
    /// <summary>
    /// Longueur minimale d'une valeur recherchée. En deçà, une occurrence n'a
    /// pas de valeur de preuve et noierait le rapport.
    /// </summary>
    private const int MinimumLiteralLength = 3;

    /// <summary>
    /// Longueur minimale d'un identifiant numérique recherché. Un numéro court
    /// — « 42 », « 2024 » — se retrouve partout, comme quantité ou comme date,
    /// sans rien prouver ; un numéro long, lui, désigne l'enregistrement.
    /// </summary>
    private const int MinimumNumericIdentifierLength = 6;

    /// <summary>
    /// Longueur en deçà de laquelle un secret n'est retrouvé que s'il forme un
    /// élément entier. Un compte comme « test » ou « hass » se rencontre au
    /// milieu de bien des identifiants sans rien révéler ; au-delà, une
    /// coïncidence devient invraisemblable.
    /// </summary>
    private const int ShortSecretLength = 8;

    private readonly Matcher? _names;
    private readonly Matcher? _technical;
    private readonly Matcher? _secrets;
    private readonly Matcher? _shortSecrets;
    private readonly Matcher? _prefixes;
    private readonly Dictionary<string, List<MappingEntry>> _ports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MappingEntry> _coordinates = new(StringComparer.Ordinal);

    /// <summary>
    /// Prépare la recherche pour un ensemble d'entrées du coffre.
    /// </summary>
    /// <param name="entries">Entrées dont les valeurs sont à retrouver</param>
    public KnownValueScanner(IEnumerable<MappingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<(string Literal, MappingEntry Entry)> names = [];
        List<(string Literal, MappingEntry Entry)> technical = [];
        List<(string Literal, MappingEntry Entry)> secrets = [];
        List<(string Literal, MappingEntry Entry)> shortSecrets = [];
        List<(string Literal, MappingEntry Entry)> prefixes = [];

        foreach (MappingEntry entry in entries)
        {
            foreach (string literal in entry.AllLiterals().Distinct(StringComparer.Ordinal))
            {
                if (entry.Category == SpanCategory.NetworkPort)
                {
                    AddPort(literal, entry);
                    continue;
                }

                if (entry.Category == SpanCategory.Coordinate)
                {
                    _coordinates.TryAdd(literal.Trim(), entry);
                    continue;
                }

                if (literal.Length < MinimumLiteralLength || entry.Category == SpanCategory.ProtectedToken)
                {
                    continue;
                }

                if (entry.Category == SpanCategory.Identifier
                    && literal.Length < MinimumNumericIdentifierLength
                    && literal.All(char.IsAsciiDigit))
                {
                    continue;
                }

                if (entry.Category == SpanCategory.IpV4 && IpAddressTools.IsAddressPrefix(literal))
                {
                    prefixes.Add((literal, entry));
                    continue;
                }

                switch (ModeOf(entry.Category))
                {
                    case MatchMode.Name:
                        names.Add((TextFolding.FoldPreservingLength(literal), entry));
                        break;

                    case MatchMode.Secret:
                        (literal.Length < ShortSecretLength ? shortSecrets : secrets).Add((literal, entry));
                        break;

                    default:
                        technical.Add((literal, entry));
                        break;
                }
            }
        }

        _names = Matcher.Create(names, StringComparison.Ordinal, Bounds.Word);
        _technical = Matcher.Create(technical, StringComparison.OrdinalIgnoreCase, Bounds.Word);
        _secrets = Matcher.Create(secrets, StringComparison.Ordinal, Bounds.None);
        _shortSecrets = Matcher.Create(shortSecrets, StringComparison.Ordinal, Bounds.Token);
        _prefixes = Matcher.Create(prefixes, StringComparison.Ordinal, Bounds.AddressStart);
    }

    /// <summary>
    /// Retrouve toutes les occurrences des valeurs connues dans un texte.
    /// </summary>
    /// <param name="text">Texte à examiner</param>
    /// <returns>Les occurrences, par position croissante</returns>
    public IReadOnlyList<KnownValueOccurrence> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<KnownValueOccurrence> found = [];
        _secrets?.Scan(text, found);
        _shortSecrets?.Scan(text, found);
        _technical?.Scan(text, found);
        _prefixes?.Scan(text, found);

        // Les noms sont cherchés dans le texte replié, qui garde la longueur du
        // texte d'origine : les positions trouvées valent donc pour les deux.
        _names?.Scan(TextFolding.FoldPreservingLength(text), found);

        ScanPorts(text, found);
        ScanCoordinates(text, found);

        found.Sort((left, right) => left.Start.CompareTo(right.Start));
        return found;
    }

    /// <summary>
    /// Retrouve les coordonnées réelles restées en position de coordonnée :
    /// sous une clé qui nomme une latitude, une longitude ou une altitude.
    /// </summary>
    /// <param name="text">Texte à examiner</param>
    /// <param name="found">Occurrences trouvées, complétées ici</param>
    private void ScanCoordinates(string text, List<KnownValueOccurrence> found)
    {
        if (_coordinates.Count == 0)
        {
            return;
        }

        foreach (DetectedSpan span in SensitiveKeyDetector.FindCoordinates(text))
        {
            if (_coordinates.TryGetValue(span.RawValue, out MappingEntry? entry))
            {
                found.Add(new KnownValueOccurrence(span.Start, span.Length, entry));
            }
        }
    }

    /// <summary>
    /// Détermine la règle de comparaison d'une nature de valeur.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <returns>La règle à appliquer</returns>
    private static MatchMode ModeOf(SpanCategory category)
    {
        return category switch
        {
            SpanCategory.Person or SpanCategory.Client or SpanCategory.Site or SpanCategory.Organization
                or SpanCategory.Hostname or SpanCategory.DomainName or SpanCategory.Fqdn => MatchMode.Name,
            SpanCategory.Secret or SpanCategory.KeyBlock or SpanCategory.Credential
                or SpanCategory.ConnectionString or SpanCategory.LicenseKey or SpanCategory.Other => MatchMode.Secret,
            _ => MatchMode.Technical,
        };
    }

    /// <summary>
    /// Enregistre un numéro de port à rechercher.
    /// </summary>
    /// <param name="literal">Numéro de port</param>
    /// <param name="entry">Entrée correspondante</param>
    private void AddPort(string literal, MappingEntry entry)
    {
        if (literal.Length == 0 || !literal.All(char.IsAsciiDigit))
        {
            return;
        }

        if (!_ports.TryGetValue(literal, out List<MappingEntry>? list))
        {
            list = [];
            _ports[literal] = list;
        }

        list.Add(entry);
    }

    /// <summary>
    /// Retrouve les numéros de port restés en position de port : derrière un
    /// deux-points, et non suivis d'un chiffre.
    /// </summary>
    /// <param name="text">Texte à examiner</param>
    /// <param name="found">Occurrences trouvées, complétées ici</param>
    private void ScanPorts(string text, List<KnownValueOccurrence> found)
    {
        if (_ports.Count == 0)
        {
            return;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        while (colon >= 0)
        {
            int end = colon + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end > colon + 1 && _ports.TryGetValue(text[(colon + 1)..end], out List<MappingEntry>? entries))
            {
                found.Add(new KnownValueOccurrence(colon + 1, end - colon - 1, entries[0]));
            }

            colon = end < text.Length ? text.IndexOf(':', end) : -1;
        }
    }

    /// <summary>Règle de comparaison d'une valeur.</summary>
    private enum MatchMode
    {
        /// <summary>Nom : sans accents ni casse, mot entier.</summary>
        Name,

        /// <summary>Valeur technique : sans casse, mot entier.</summary>
        Technical,

        /// <summary>Secret : à l'identique, sans bornes.</summary>
        Secret,
    }

    /// <summary>Bornes qu'une occurrence doit respecter.</summary>
    private enum Bounds
    {
        /// <summary>Aucune : la valeur compte où qu'elle soit.</summary>
        None,

        /// <summary>Mot entier : ni précédée ni suivie d'une lettre ou d'un chiffre.</summary>
        Word,

        /// <summary>Tête d'adresse : mot entier, et jamais précédée d'un point.</summary>
        AddressStart,

        /// <summary>
        /// Élément entier : ni précédée ni suivie d'une lettre, d'un chiffre,
        /// d'un souligné ou d'un trait d'union — les caractères qui prolongent
        /// un identifiant.
        /// </summary>
        Token,
    }

    /// <summary>
    /// Recherche simultanée d'un ensemble de valeurs, selon une même règle.
    /// </summary>
    private sealed class Matcher
    {
        private readonly SearchValues<string> _values;
        private readonly Dictionary<char, List<(string Literal, MappingEntry Entry)>> _byFirstCharacter;
        private readonly StringComparison _comparison;
        private readonly Bounds _bounds;

        /// <summary>
        /// Construit la recherche.
        /// </summary>
        /// <param name="literals">Valeurs à rechercher</param>
        /// <param name="comparison">Comparaison à appliquer</param>
        /// <param name="bounds">Bornes qu'une occurrence doit respecter</param>
        private Matcher(List<(string Literal, MappingEntry Entry)> literals, StringComparison comparison, Bounds bounds)
        {
            _comparison = comparison;
            _bounds = bounds;
            _values = SearchValues.Create([.. literals.Select(item => item.Literal).Distinct(StringComparer.FromComparison(comparison))], comparison);

            // À une même position, la valeur la plus longue est essayée en
            // premier : « Marc-Antoine » l'emporte sur « Marc ».
            _byFirstCharacter = literals
                .GroupBy(item => KeyOf(item.Literal[0]))
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.Literal.Length).ToList());
        }

        /// <summary>
        /// Construit la recherche, ou rien s'il n'y a aucune valeur.
        /// </summary>
        /// <param name="literals">Valeurs à rechercher</param>
        /// <param name="comparison">Comparaison à appliquer</param>
        /// <param name="bounds">Bornes qu'une occurrence doit respecter</param>
        /// <returns>La recherche, ou <c>null</c></returns>
        public static Matcher? Create(List<(string Literal, MappingEntry Entry)> literals, StringComparison comparison, Bounds bounds)
        {
            return literals.Count == 0 ? null : new Matcher(literals, comparison, bounds);
        }

        /// <summary>
        /// Parcourt un texte et consigne les occurrences, sans chevauchement.
        /// </summary>
        /// <param name="haystack">Texte à parcourir, éventuellement replié</param>
        /// <param name="found">Occurrences trouvées, complétées ici</param>
        public void Scan(string haystack, List<KnownValueOccurrence> found)
        {
            int position = 0;
            while (position < haystack.Length)
            {
                int offset = haystack.AsSpan(position).IndexOfAny(_values);
                if (offset < 0)
                {
                    return;
                }

                int start = position + offset;
                (string Literal, MappingEntry Entry)? hit = LongestAt(haystack, start);

                if (hit is null)
                {
                    position = start + 1;
                    continue;
                }

                found.Add(new KnownValueOccurrence(start, hit.Value.Literal.Length, hit.Value.Entry));
                position = start + hit.Value.Literal.Length;
            }
        }

        /// <summary>
        /// Retient la plus longue valeur présente à une position et respectant
        /// les bornes.
        /// </summary>
        /// <param name="haystack">Texte parcouru</param>
        /// <param name="start">Position examinée</param>
        /// <returns>La valeur retenue, ou <c>null</c></returns>
        private (string Literal, MappingEntry Entry)? LongestAt(string haystack, int start)
        {
            if (!_byFirstCharacter.TryGetValue(KeyOf(haystack[start]), out List<(string Literal, MappingEntry Entry)>? candidates))
            {
                return null;
            }

            foreach ((string literal, MappingEntry entry) in candidates)
            {
                if (haystack.AsSpan(start).StartsWith(literal, _comparison) && IsBounded(haystack, start, literal.Length))
                {
                    return (literal, entry);
                }
            }

            return null;
        }

        /// <summary>
        /// Indique si une portion de texte respecte les bornes de la recherche.
        /// Pour un mot entier, ni précédée ni suivie d'une lettre ou d'un
        /// chiffre — le point, le trait d'union et le souligné comptent comme
        /// des séparateurs ; pour une tête d'adresse, pas non plus précédée
        /// d'un point.
        /// </summary>
        /// <param name="text">Texte examiné</param>
        /// <param name="start">Début de la portion</param>
        /// <param name="length">Longueur de la portion</param>
        /// <returns><c>true</c> si la portion est bornée</returns>
        private bool IsBounded(string text, int start, int length)
        {
            if (_bounds == Bounds.None)
            {
                return true;
            }

            int end = start + length;
            if (_bounds == Bounds.Token)
            {
                return (start == 0 || !ExtendsToken(text[start - 1])) && (end >= text.Length || !ExtendsToken(text[end]));
            }

            bool word = (start == 0 || !char.IsLetterOrDigit(text[start - 1]))
                && (end >= text.Length || !char.IsLetterOrDigit(text[end]));

            return _bounds == Bounds.Word ? word : word && (start == 0 || text[start - 1] != '.');
        }

        /// <summary>
        /// Indique si un caractère prolonge un identifiant.
        /// </summary>
        /// <param name="character">Caractère voisin d'une occurrence</param>
        /// <returns><c>true</c> pour une lettre, un chiffre, un souligné ou un trait d'union</returns>
        private static bool ExtendsToken(char character)
        {
            return char.IsLetterOrDigit(character) || character is '_' or '-';
        }

        /// <summary>
        /// Clé de regroupement d'un premier caractère, conforme à la comparaison.
        /// </summary>
        /// <param name="character">Premier caractère</param>
        /// <returns>La clé</returns>
        private char KeyOf(char character)
        {
            return _comparison == StringComparison.OrdinalIgnoreCase ? char.ToUpperInvariant(character) : character;
        }
    }
}
