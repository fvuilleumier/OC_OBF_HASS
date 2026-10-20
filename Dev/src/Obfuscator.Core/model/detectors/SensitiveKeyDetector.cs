using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Documents;
using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Detectors;

/// <summary>
/// Masque la valeur de toute clé dont le nom désigne un secret, un compte, un
/// port d'écoute ou un identifiant, quelle que soit la syntaxe qui les relie.
///
/// Le détecteur ne juge pas la valeur mais la clé : un mot de passe qui
/// ressemble à un mot ordinaire, qu'aucune analyse d'entropie ne signalerait,
/// est masqué parce qu'il se trouve sous <c>wsPassword</c>. Le nom de la clé
/// est confié à <see cref="SensitiveKeyNames"/>, qui reconnaît les noms
/// composés ; ce détecteur-ci se charge de trouver les paires :
/// <list type="bullet">
/// <item><c>clé = valeur</c>, <c>clé: valeur</c>, <c>"clé": "valeur"</c>, <c>clé => valeur</c> —
/// fichiers de réglages, variables d'environnement, JSON, YAML, attributs XML
/// portant eux-mêmes le nom sensible, notes Markdown (<c>**Mot de passe** : …</c>) ;</item>
/// <item><c>&lt;add key="wsPassword" value="…" /&gt;</c> — la forme des réglages .NET, où
/// le nom est la valeur d'un attribut et le secret celle d'un autre, comme dans
/// <c>&lt;property name="…" value="…" /&gt;</c> ;</item>
/// <item><c>&lt;setting name="…"&gt;&lt;value&gt;…&lt;/value&gt;</c> — les réglages
/// d'application .NET ;</item>
/// <item><c>&lt;wsPassword&gt;…&lt;/wsPassword&gt;</c> — un élément XML au nom sensible.</item>
/// </list>
/// </summary>
public sealed partial class SensitiveKeyDetector : IDetector
{
    /// <summary>Règle des coordonnées repérées sous une clé qui les nomme.</summary>
    public const string CoordinateRuleId = "geo.coordinateKeyValue";

    private const int TimeoutMilliseconds = 2000;

    /// <inheritdoc />
    public string RuleId => "secret.key";

    /// <summary>Un nom de fichier n'a pas de paire clé/valeur.</summary>
    public bool AppliesToNames => false;

    /// <summary>
    /// Paire clé/valeur en texte. La clé peut compter jusqu'à quatre mots
    /// (« mot de passe », « User ID »), être entre guillemets ou en gras
    /// Markdown ; la valeur ne franchit jamais la fin de ligne — une clé YAML
    /// suivie d'un bloc imbriqué n'a pas de valeur sur sa ligne.
    ///
    /// La valeur est lue par anticipation, sans être consommée : la recherche
    /// reprend donc à l'intérieur. Sans cela, la valeur d'une clé anodine
    /// masquerait les paires qu'elle contient — le mot de passe d'une chaîne
    /// de connexion rangée sous <c>"Default": "…;Password=…"</c>.
    /// </summary>
    [GeneratedRegex(
        @"(?<![\w.])(?<key>[\p{L}_][\p{L}\p{N}_.\-]*(?:[ ][\p{L}][\p{L}\p{N}_.\-]*){0,3})[*""'`]{0,2}[ \t]*(?::=|=>|:|=)[ \t]*[*`]{0,2}[ \t]*" +
        @"(?=""(?<value>[^""\r\n]*)""|'(?<value>[^'\r\n]*)'|`(?<value>[^`\r\n]*)`" +
        // Les références sont capturées entières, pour être reconnues comme
        // telles et laissées en place : « ${DB_PASSWORD} », « !secret wifi ».
        @"|(?<value>\$\{[^}\r\n]*\}|\$\([^)\r\n]*\)|\{\{[^}\r\n]*\}\}|<[^>\r\n]*>|![\w\-]+[ \t]+[^\s,;{}\[\]]+)" +
        // Guillemets et accolades sont exclus de la valeur nue : les absorber
        // casserait la syntaxe du document. Elle ne commence pas non plus par
        // un signe d'égalité ou deux-points : « password == x » est une
        // comparaison, pas une affectation.
        @"|(?<bare>[^\s,;{}\[\]""'`=:\r\n][^\s,;{}\[\]""'`\r\n]*))",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex KeyValuePair();

    /// <summary>
    /// Élément XML dont un attribut nomme la valeur et un autre la porte :
    /// <c>&lt;add key="…" value="…" /&gt;</c>, <c>&lt;property name="…" value="…" /&gt;</c>.
    /// </summary>
    [GeneratedRegex(
        @"<[\p{L}_][\w:.\-]*(?<attributes>(?:\s+[\w:.\-]+\s*=\s*(?:""[^""]*""|'[^']*'))+)\s*/?>",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex AttributedElement();

    /// <summary>Attribut XML, avec sa valeur entre guillemets doubles ou simples.</summary>
    [GeneratedRegex(
        @"(?<name>[\w:.\-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex Attribute();

    /// <summary>Réglage d'application .NET : le nom en attribut, la valeur en élément enfant.</summary>
    [GeneratedRegex(
        @"<setting\b[^>]*?\bname\s*=\s*(?:""(?<key>[^""]*)""|'(?<key>[^']*)')[^>]*>\s*<value>(?<value>[^<]*)</value>",
        RegexOptions.IgnoreCase,
        TimeoutMilliseconds)]
    private static partial Regex SettingElement();

    /// <summary>Élément XML de texte seul, dont le nom est la clé : <c>&lt;password&gt;…&lt;/password&gt;</c>.</summary>
    [GeneratedRegex(
        @"<(?<key>[\p{L}_][\w:.\-]*)(?:\s[^<>]*)?>(?<value>[^<>]+)</\k<key>\s*>",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex TextElement();

    /// <summary>Nombre nu, qu'un document JSON ou YAML ne peut pas voir remplacé par un jeton.</summary>
    [GeneratedRegex(@"^[+\-]?\d+(?:\.\d+)?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex BareNumber();

    /// <summary>
    /// Mots de liaison qui, après une clé de plusieurs mots, signalent une
    /// phrase et non une valeur : « … un token de thème : il n'y a rien ».
    /// </summary>
    private static readonly HashSet<string> ProseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "il", "ils", "elle", "elles", "on", "le", "la", "les", "l", "un", "une", "des", "de", "du", "d",
        "ce", "cet", "cette", "ces", "c", "ça", "qui", "que", "qu", "et", "ou", "en", "à", "a", "y",
        "the", "an", "it", "this", "that", "these", "those", "is", "are", "you", "we", "they", "he", "she",
    };

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(context);
    }

    /// <summary>
    /// Parcourt les quatre formes de paires.
    /// </summary>
    /// <param name="context">Contexte d'analyse validé</param>
    /// <returns>Un span par valeur à masquer</returns>
    private static IEnumerable<DetectedSpan> Enumerate(DetectionContext context)
    {
        string text = context.Text;
        bool structured = context.Kind is FileKind.Json or FileKind.Yaml;

        return KeyValuePair().Matches(text).Select(pair => PairSpan(pair, text, structured))
            .Concat(AttributedElement().Matches(text).Select(element => NamedValue(element, text)))
            .Concat(SettingElement().Matches(text).Select(setting => Span(setting.Groups["key"].Value, setting.Groups["value"], text)))
            .Concat(TextElement().Matches(text).Select(element => Span(element.Groups["key"].Value, element.Groups["value"], text)))
            .OfType<DetectedSpan>();
    }

    /// <summary>
    /// Examine une paire clé/valeur en texte.
    /// </summary>
    /// <param name="pair">Paire trouvée</param>
    /// <param name="text">Texte source</param>
    /// <param name="structured">Vrai pour un document JSON ou YAML</param>
    /// <returns>Le span de la valeur, ou <c>null</c></returns>
    private static DetectedSpan? PairSpan(Match pair, string text, bool structured)
    {
        Group bare = pair.Groups["bare"];
        string key = pair.Groups["key"].Value;

        // Dans un document JSON ou YAML, un nombre nu n'est pas une chaîne :
        // le remplacer par un jeton rendrait le document invalide. Un port, un
        // identifiant ou une coordonnée, eux, reçoivent un autre nombre, et le
        // document reste valide.
        if (structured && bare.Success && BareNumber().IsMatch(bare.Value)
            && SensitiveKeyNames.ClassifyPair(key, bare.Value) is not (SpanCategory.NetworkPort or SpanCategory.Identifier or SpanCategory.Coordinate))
        {
            return null;
        }

        // Une clé de plusieurs mots suivie d'un mot de liaison est une phrase.
        if (bare.Success && key.Contains(' ', StringComparison.Ordinal) && ProseWords.Contains(bare.Value.TrimEnd('\'', '’')))
        {
            return null;
        }

        return Span(key, bare.Success ? bare : pair.Groups["value"], text);
    }

    /// <summary>
    /// Examine un élément XML à attributs : s'il porte à la fois un nom — dans
    /// <c>key</c> ou <c>name</c> — et une <c>value</c>, c'est une paire.
    /// </summary>
    /// <param name="element">Élément trouvé</param>
    /// <param name="text">Texte source</param>
    /// <returns>Le span de la valeur, ou <c>null</c></returns>
    private static DetectedSpan? NamedValue(Match element, string text)
    {
        Group attributes = element.Groups["attributes"];
        List<(string Name, Group Value)> parsed = [.. Attribute().Matches(attributes.Value)
            .Select(attribute => (attribute.Groups["name"].Value, attribute.Groups["value"]))];

        string? keyName = parsed
            .Where(attribute => attribute.Name.Equals("key", StringComparison.OrdinalIgnoreCase) || attribute.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value.Value)
            .FirstOrDefault();

        Group? value = parsed
            .Where(attribute => attribute.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value)
            .FirstOrDefault();

        // Les positions de l'attribut sont relatives à la liste d'attributs.
        return keyName is null || value is null
            ? null
            : SpanAt(keyName, attributes.Index + value.Index, value.Value, text);
    }

    /// <summary>
    /// Produit le span d'une valeur si sa clé est sensible.
    /// </summary>
    /// <param name="keyName">Nom de la clé</param>
    /// <param name="value">Groupe de la valeur</param>
    /// <param name="text">Texte source</param>
    /// <returns>Le span, ou <c>null</c></returns>
    private static DetectedSpan? Span(string keyName, Group value, string text)
    {
        return value.Success ? SpanAt(keyName, value.Index, value.Value, text) : null;
    }

    /// <summary>
    /// Produit le span d'une valeur si sa clé est sensible, sans les espaces
    /// qui l'entourent.
    /// </summary>
    /// <param name="keyName">Nom de la clé</param>
    /// <param name="start">Position de la valeur dans le texte</param>
    /// <param name="value">Valeur telle qu'elle figure dans le texte</param>
    /// <param name="text">Texte source</param>
    /// <returns>Le span, ou <c>null</c></returns>
    private static DetectedSpan? SpanAt(string keyName, int start, string value, string text)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || SensitiveKeyNames.ClassifyPair(keyName, trimmed) is not SpanCategory category)
        {
            return null;
        }

        int offset = start + value.IndexOf(trimmed, StringComparison.Ordinal);
        string rule = category switch
        {
            SpanCategory.Secret => "secret.keyValue",
            SpanCategory.Email => "secret.credentialKeyValue.email",
            SpanCategory.NetworkPort => "network.portKeyValue",
            SpanCategory.Identifier => "identity.idKeyValue",
            SpanCategory.Coordinate => CoordinateRuleId,
            _ => "secret.credentialKeyValue",
        };

        return DetectedSpan.Create(offset, trimmed.Length, category, text.Substring(offset, trimmed.Length), rule);
    }

    /// <summary>
    /// Repère les coordonnées d'un texte : les nombres placés sous une clé qui
    /// nomme une coordonnée, quelle que soit la syntaxe qui les relie.
    ///
    /// C'est la position qui fait la coordonnée, pas la forme : un nombre comme
    /// <c>430</c> n'est l'altitude d'un lieu que derrière <c>elevation</c>. La
    /// restitution et le contrôle final ne cherchent donc une coordonnée qu'à
    /// une telle position, comme un port derrière un deux-points.
    /// </summary>
    /// <param name="text">Texte à examiner, pseudonymisé ou non</param>
    /// <returns>Les nombres en position de coordonnée, par position croissante</returns>
    public static IReadOnlyList<DetectedSpan> FindCoordinates(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Le texte examiné est souvent un fichier rendu par l'IA, dont le nom
        // est inconnu : le format est deviné au contenu, et la paire textuelle
        // suffit à toutes les syntaxes.
        List<DetectedSpan> spans = [.. Enumerate(new DetectionContext(text) { Kind = FileKind.PlainText })
            .Where(span => span.Category == SpanCategory.Coordinate)
            .DistinctBy(span => span.Start)];

        spans.Sort((left, right) => left.Start.CompareTo(right.Start));
        return spans;
    }
}
