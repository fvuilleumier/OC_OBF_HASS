using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Detectors;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Engine;

/// <summary>
/// Pseudonyme restitué à sa valeur réelle.
/// </summary>
/// <param name="Start">Position dans le texte traité</param>
/// <param name="Pseudonym">Pseudonyme rencontré</param>
/// <param name="Category">Nature de la valeur restituée</param>
public sealed record TokenSubstitution(int Start, string Pseudonym, SpanCategory Category)
{
    /// <summary>
    /// Vrai si la valeur a été déduite plutôt que lue au coffre : une adresse
    /// écrite par le modèle dans un sous-réseau pseudonymisé connu.
    /// </summary>
    public bool Inferred { get; init; }

    /// <summary>
    /// Vrai si le pseudonyme désigne à coup sûr son coffre : c'est un jeton
    /// signé du nonce du projet, qui ne peut venir que de ce coffre-là. Une
    /// adresse ou un courriel pseudonymisés suivent au contraire la même
    /// numérotation dans tous les coffres — le premier courriel est partout
    /// <c>user0001@example.invalid</c> — et ne désignent donc pas, à eux seuls,
    /// le coffre d'origine.
    /// </summary>
    public bool ProvesOrigin { get; init; }
}

/// <summary>
/// Jeton reconnu comme tel mais absent du coffre.
/// </summary>
/// <param name="Start">Position dans le texte traité</param>
/// <param name="RawText">Forme exacte rencontrée</param>
/// <param name="Canonical">Forme canonique du jeton</param>
public sealed record UnknownToken(int Start, string RawText, string Canonical);

/// <summary>
/// Résultat d'une désobfuscation.
/// </summary>
/// <param name="Text">Texte restitué</param>
/// <param name="Substitutions">Pseudonymes remplacés par leur valeur réelle</param>
/// <param name="UnknownTokens">Jetons rencontrés mais inconnus du coffre</param>
public sealed record DeobfuscationResult(
    string Text,
    IReadOnlyList<TokenSubstitution> Substitutions,
    IReadOnlyList<UnknownToken> UnknownTokens)
{
    /// <summary>
    /// Place de chaque valeur restituée dans le texte produit, en regard du
    /// pseudonyme qu'elle remplace dans le texte traité, par position
    /// croissante. C'est ce qui permet de signaler un jeton inconnu à sa ligne
    /// dans le fichier écrit, et non dans celui de l'IA.
    /// </summary>
    public IReadOnlyList<TextSubstitution> Placements { get; init; } = [];

    /// <summary>
    /// Nombres en position de coordonnée qui ne sont le pseudonyme d'aucune
    /// coordonnée du coffre : une coordonnée que le modèle a inventée, ou
    /// calculée à partir d'une coordonnée fictive. Elle est laissée telle
    /// quelle, et doit être signalée.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> UnmatchedCoordinates { get; init; } = [];

    /// <summary>
    /// Ramène une position du texte traité à la position correspondante du
    /// texte produit. Hors des valeurs restituées, les deux textes ne
    /// diffèrent que d'un décalage.
    /// </summary>
    /// <param name="inputPosition">Position dans le texte traité</param>
    /// <returns>La position dans le texte produit</returns>
    public int OutputPositionOf(int inputPosition)
    {
        int shift = 0;
        foreach (TextSubstitution placement in Placements)
        {
            if (inputPosition < placement.SourceStart)
            {
                break;
            }

            if (inputPosition < placement.SourceStart + placement.SourceLength)
            {
                return placement.OutputStart;
            }

            shift = placement.OutputStart + placement.OutputLength - (placement.SourceStart + placement.SourceLength);
        }

        return inputPosition + shift;
    }
}

/// <summary>
/// Restitue les valeurs réelles dans un texte obfusqué — le fichier d'origine,
/// mais surtout la réponse produite par un modèle de langage à partir de ce
/// fichier.
///
/// Deux points de conception méritent d'être explicites.
/// D'abord, la restitution ne s'appuie que sur le contenu du coffre : on ne
/// remplace jamais « toute valeur ressemblant à un pseudonyme », faute de quoi
/// une adresse de plage de test écrite spontanément par le modèle deviendrait
/// une vraie adresse interne. La seule déduction admise est celle d'une adresse
/// écrite dans un sous-réseau que le coffre connaît déjà : elle désigne un hôte
/// de ce réseau-là, et elle est signalée comme déduite.
/// Ensuite, un jeton inconnu est un avertissement et non une erreur : un modèle
/// à qui l'on demande de générer trois serveurs supplémentaires inventera
/// légitimement des jetons qui n'existent pas encore, et ceux-là doivent
/// traverser la restitution intacts.
/// </summary>
public sealed partial class DeobfuscationEngine
{
    /// <summary>Masque d'un sous-réseau /24.</summary>
    private const uint NetworkMask = 0xFFFFFF00;

    /// <summary>Règle des adresses restituées par leur sous-réseau.</summary>
    private const string SubnetRuleId = "deobf.subnet";

    /// <summary>Règle des jetons signés du nonce du projet.</summary>
    private const string TokenRuleId = "deobf.token";

    /// <summary>Règle des coordonnées restituées en position de coordonnée.</summary>
    private const string CoordinateRuleId = "deobf.coordinate";

    private readonly TokenFormat _format;
    private readonly MappingStore _store;

    /// <summary>
    /// Construit le moteur de restitution pour un projet.
    /// </summary>
    /// <param name="format">Format des jetons du projet</param>
    /// <param name="store">Coffre du projet</param>
    public DeobfuscationEngine(TokenFormat format, MappingStore store)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(store);

        _format = format;
        _store = store;
    }

    /// <summary>
    /// Restitue les valeurs réelles d'un texte.
    /// </summary>
    /// <param name="text">Texte contenant des pseudonymes</param>
    /// <returns>Le texte restitué et le compte rendu des substitutions</returns>
    public DeobfuscationResult Deobfuscate(string text)
    {
        return Deobfuscate(text, static value => value);
    }

    /// <summary>
    /// Restitue les valeurs réelles d'un texte, en les encodant pour le format
    /// qui les accueille — du HTML, par exemple, où une valeur contenant
    /// <c>&lt;</c> ou <c>&amp;</c> casserait le document si elle était insérée
    /// telle quelle.
    /// </summary>
    /// <param name="text">Texte contenant des pseudonymes</param>
    /// <param name="encode">Encodage à appliquer à chaque valeur restituée</param>
    /// <returns>Le texte restitué et le compte rendu des substitutions</returns>
    public DeobfuscationResult Deobfuscate(string text, Func<string, string> encode)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encode);

        List<DetectedSpan> candidates = [];
        Dictionary<(int Start, int Length), string> replacements = [];
        List<UnknownToken> unknown = [];
        List<(int Start, int Length)> unmatched = [];

        CollectTokenOccurrences(text, candidates, replacements, unknown);
        CollectFormatPreservingPseudonyms(text, candidates, replacements);
        CollectSubnetAddresses(text, candidates, replacements);
        CollectCoordinates(text, candidates, replacements, unmatched);

        SpanResolution resolution = SpanResolver.Resolve(candidates);

        // Les spans arrivent dans l'ordre du texte : le décalage accumulé
        // donne la place de chaque valeur restituée dans le texte produit.
        List<TextSubstitution> placements = new(resolution.Accepted.Count);
        int shift = 0;
        string output = TextRewriter.Rewrite(text, resolution.Accepted, span =>
        {
            string restored = encode(replacements[(span.Start, span.Length)]);
            placements.Add(new TextSubstitution(span.Start, span.Length, span.Start + shift, restored.Length));
            shift += restored.Length - span.Length;
            return restored;
        });

        List<TokenSubstitution> substitutions = [];
        foreach (DetectedSpan span in resolution.Accepted)
        {
            substitutions.Add(new TokenSubstitution(span.Start, span.RawValue, span.Category)
            {
                Inferred = span.RuleId == SubnetRuleId,
                ProvesOrigin = span.RuleId == TokenRuleId,
            });
        }

        return new DeobfuscationResult(output, substitutions, unknown)
        {
            Placements = placements,
            UnmatchedCoordinates = unmatched,
        };
    }

    /// <summary>
    /// Restitue les coordonnées, en position de coordonnée seulement : sous
    /// une clé qui nomme une latitude, une longitude ou une altitude. Un
    /// pseudonyme de coordonnée est un nombre court ; cherché partout, il
    /// serait reconnu dans une durée ou un compteur qui n'ont rien à voir.
    /// Un nombre en position de coordonnée qui n'est le pseudonyme d'aucune
    /// est relevé : le modèle l'a inventé.
    /// </summary>
    /// <param name="text">Texte à traiter</param>
    /// <param name="candidates">Liste de spans à compléter</param>
    /// <param name="replacements">Table des remplacements, indexée par position et longueur</param>
    /// <param name="unmatched">Coordonnées sans correspondance, complétées ici</param>
    private void CollectCoordinates(
        string text,
        List<DetectedSpan> candidates,
        Dictionary<(int Start, int Length), string> replacements,
        List<(int Start, int Length)> unmatched)
    {
        // Sans coordonnée au coffre, rien n'a été masqué : les coordonnées du
        // texte sont les vraies, laissées en clair, et n'ont rien à signaler.
        if (!_store.Entries.Any(entry => entry.Category == SpanCategory.Coordinate))
        {
            return;
        }

        foreach (DetectedSpan span in SensitiveKeyDetector.FindCoordinates(text))
        {
            if (_store.TryGetByPseudonym(span.RawValue, out MappingEntry entry) && entry.Category == SpanCategory.Coordinate)
            {
                candidates.Add(span with { Priority = 0, Confidence = 1.0, RuleId = CoordinateRuleId });
                replacements[(span.Start, span.Length)] = entry.Original;
                continue;
            }

            // Un nombre que le coffre connaît sous une autre nature, ou comme
            // coordonnée réelle laissée en clair, n'est pas une invention.
            if (!_store.IsPseudonymTaken(span.RawValue)
                && !_store.TryGetByValue(SpanCategory.Coordinate, span.RawValue.Trim(), out _))
            {
                unmatched.Add((span.Start, span.Length));
            }
        }
    }

    /// <summary>
    /// Recense les jetons neutres, y compris ceux qu'un modèle aurait reformatés.
    /// </summary>
    /// <param name="text">Texte à traiter</param>
    /// <param name="candidates">Liste de spans à compléter</param>
    /// <param name="replacements">Table des remplacements, indexée par position et longueur</param>
    /// <param name="unknown">Liste des jetons inconnus à compléter</param>
    private void CollectTokenOccurrences(
        string text,
        List<DetectedSpan> candidates,
        Dictionary<(int Start, int Length), string> replacements,
        List<UnknownToken> unknown)
    {
        foreach (TokenOccurrence occurrence in _format.FindOccurrences(text))
        {
            if (!_store.TryGetByPseudonym(occurrence.Identity.Canonical, out MappingEntry entry))
            {
                unknown.Add(new UnknownToken(occurrence.Start, occurrence.RawText, occurrence.Identity.Canonical));
                continue;
            }

            candidates.Add(new DetectedSpan(
                occurrence.Start,
                occurrence.Length,
                entry.Category,
                occurrence.RawText,
                Priority: 0,
                Confidence: 1.0,
                RuleId: TokenRuleId));
            replacements[(occurrence.Start, occurrence.Length)] = entry.Original;
        }
    }

    /// <summary>
    /// Recense les pseudonymes qui ne sont pas des jetons neutres — adresses IP
    /// de substitution, ports, domaines en <c>.invalid</c>, identifiants
    /// globaux — en recherchant leur graphie exacte dans le texte.
    ///
    /// Une occurrence doit former un mot entier : ni précédée ni suivie d'une
    /// lettre ou d'un chiffre. Sans cette borne, le pseudonyme <c>198.18.0.1</c>
    /// serait retrouvé au début de <c>198.18.0.12</c>, et le port <c>41001</c>
    /// au milieu de <c>241001</c> : la restitution fabriquerait des valeurs qui
    /// n'ont jamais existé.
    ///
    /// Un début d'adresse — <c>198.18.4</c>, <c>250.0</c> — n'est retrouvé
    /// qu'en tête d'adresse : ni précédé d'un point, ni suivi d'un chiffre.
    /// Suivi d'un point, il commence une adresse dont il restitue le réseau ;
    /// précédé d'un point, il n'en serait que la fin — <c>10.250.0.5</c> ne
    /// contient pas le préfixe <c>250.0</c>.
    /// </summary>
    /// <param name="text">Texte à traiter</param>
    /// <param name="candidates">Liste de spans à compléter</param>
    /// <param name="replacements">Table des remplacements, indexée par position et longueur</param>
    private void CollectFormatPreservingPseudonyms(
        string text,
        List<DetectedSpan> candidates,
        Dictionary<(int Start, int Length), string> replacements)
    {
        foreach (MappingEntry entry in _store.Entries)
        {
            // Les jetons neutres ont déjà été traités par la reconnaissance
            // tolérante, qui rattrape en plus leurs formes reformatées ; les
            // coordonnées ne le sont qu'en position de coordonnée.
            if (entry.Category == SpanCategory.Coordinate || _format.TryParse(entry.Pseudonym, out _))
            {
                continue;
            }

            bool prefix = entry.Category == SpanCategory.IpV4 && IpAddressTools.IsAddressPrefix(entry.Pseudonym);

            int position = 0;
            while (position <= text.Length - entry.Pseudonym.Length)
            {
                int found = text.IndexOf(entry.Pseudonym, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }

                position = found + 1;
                bool bounded = prefix
                    ? IsPrefixBounded(text, found, entry.Pseudonym.Length)
                    : IsBounded(text, found, entry.Pseudonym.Length);

                if (!bounded)
                {
                    continue;
                }

                candidates.Add(new DetectedSpan(
                    found,
                    entry.Pseudonym.Length,
                    entry.Category,
                    text.Substring(found, entry.Pseudonym.Length),
                    Priority: 0,
                    Confidence: 1.0,
                    RuleId: "deobf.pseudonym"));
                replacements[(found, entry.Pseudonym.Length)] = entry.Original;

                position = found + entry.Pseudonym.Length;
            }
        }
    }

    /// <summary>
    /// Restitue les adresses qu'un modèle a écrites dans un sous-réseau
    /// pseudonymisé, sans qu'elles figurent au coffre.
    ///
    /// La substitution conserve la structure des sous-réseaux : un /24 réel
    /// correspond à un /24 de substitution — du réservoir 198.18.0.0/15 pour une
    /// adresse publique, de sa propre plage pour une adresse privée —, et la
    /// partie hôte est gardée. Un modèle qui propose « attribuer 192.168.137.50
    /// à la nouvelle imprimante » désigne donc une adresse précise du réseau
    /// réel correspondant, et c'est elle qu'on doit restituer. Seuls les
    /// sous-réseaux de substitution connus du coffre sont concernés : une
    /// adresse hors de tout sous-réseau connu reste telle quelle, faute de
    /// réseau réel à lui associer. Les réseaux privés de substitution évitant
    /// les réseaux réels du lot et les numéros usuels, une adresse de l'un
    /// d'eux ne peut guère venir que du fichier pseudonymisé ; elle est de
    /// toute façon signalée comme déduite.
    /// </summary>
    /// <param name="text">Texte à traiter</param>
    /// <param name="candidates">Liste de spans à compléter</param>
    /// <param name="replacements">Table des remplacements, indexée par position et longueur</param>
    private void CollectSubnetAddresses(
        string text,
        List<DetectedSpan> candidates,
        Dictionary<(int Start, int Length), string> replacements)
    {
        // Un /24 masqué par son seul début — « 192.168.1 » — renseigne le
        // réseau aussi bien qu'une adresse complète.
        Dictionary<uint, uint> networks = [];
        foreach (MappingEntry entry in _store.Entries)
        {
            if (entry.Category == SpanCategory.IpV4
                && IpAddressTools.TryParseAddressOrPrefix(entry.Pseudonym, out uint pseudonym, out int written)
                && IpAddressTools.TryParseAddressOrPrefix(entry.Canonical, out uint real, out int octets)
                && written == octets
                && octets >= 3)
            {
                networks[pseudonym & NetworkMask] = real & NetworkMask;
            }
        }

        if (networks.Count == 0)
        {
            return;
        }

        foreach (Match match in BoundedAddress().Matches(text))
        {
            if (_store.IsPseudonymTaken(match.Value)
                || !IpAddressTools.TryParse(match.Value, out uint address)
                || !networks.TryGetValue(address & NetworkMask, out uint network))
            {
                continue;
            }

            candidates.Add(new DetectedSpan(
                match.Index,
                match.Length,
                SpanCategory.IpV4,
                match.Value,
                Priority: 0,
                Confidence: 0.9,
                RuleId: SubnetRuleId));
            replacements[(match.Index, match.Length)] = IpAddressTools.Format(network | (address & ~NetworkMask));
        }
    }

    /// <summary>
    /// Indique si une portion de texte forme un mot entier.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <param name="start">Début de la portion</param>
    /// <param name="length">Longueur de la portion</param>
    /// <returns><c>true</c> si la portion n'est ni précédée ni suivie d'une lettre ou d'un chiffre</returns>
    private static bool IsBounded(string text, int start, int length)
    {
        int end = start + length;
        return (start == 0 || !char.IsLetterOrDigit(text[start - 1]))
            && (end >= text.Length || !char.IsLetterOrDigit(text[end]));
    }

    /// <summary>
    /// Indique si une portion de texte se trouve en tête d'adresse : ni
    /// précédée d'une lettre, d'un chiffre ou d'un point, ni suivie d'une
    /// lettre ou d'un chiffre.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <param name="start">Début de la portion</param>
    /// <param name="length">Longueur de la portion</param>
    /// <returns><c>true</c> si la portion peut être le début d'une adresse</returns>
    private static bool IsPrefixBounded(string text, int start, int length)
    {
        return IsBounded(text, start, length) && (start == 0 || text[start - 1] != '.');
    }

    /// <summary>Adresse IPv4 formant un mot entier : ni prolongée, ni fin d'une suite plus longue.</summary>
    [GeneratedRegex(@"(?<![\d.])\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?!\d|\.\d)", RegexOptions.None, 2000)]
    private static partial Regex BoundedAddress();
}
