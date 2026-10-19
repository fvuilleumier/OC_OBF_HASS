using System.Text;
using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Reporting;

/// <summary>
/// Élément suspect laissé en clair par les détecteurs.
///
/// Le signalement porte deux formes de la valeur, et la distinction est une
/// règle de sécurité, pas un détail : <see cref="Value"/> est la chaîne réelle,
/// réservée à l'affichage interactif et à la promotion en terme masqué ;
/// <see cref="MaskedSample"/> est la seule qui ait le droit d'atteindre un
/// rapport écrit sur disque ou un journal.
/// </summary>
/// <param name="Kind">Nature du soupçon : bloc encadré, base64 déchiffrable, entropie élevée</param>
/// <param name="Start">Position du début dans le texte source, en unités UTF-16</param>
/// <param name="Length">Longueur dans le texte source</param>
/// <param name="Line">Numéro de ligne, à partir de 1</param>
/// <param name="Value">Chaîne réelle repérée, pour l'écran et pour l'ajout aux règles</param>
/// <param name="MaskedSample">Extrait masqué, seule forme publiable</param>
/// <param name="Score">Score d'entropie normalisée, dans l'intervalle [0, 1]</param>
/// <param name="Advice">Conseil adressé à l'utilisateur</param>
public sealed record ResidualFinding(
    string Kind,
    int Start,
    int Length,
    int Line,
    string Value,
    string MaskedSample,
    double Score,
    string Advice)
{
    /// <summary>
    /// Nature proposée si l'utilisateur décide de masquer la valeur. Une chaîne
    /// aléatoire est par défaut un secret ; une valeur déjà connue du coffre
    /// reprend la nature sous laquelle elle y figure.
    /// </summary>
    public SpanCategory SuggestedCategory { get; init; } = SpanCategory.Secret;

    /// <summary>
    /// Vrai si l'utilisateur a gardé cette occurrence-ci en clair : elle n'est
    /// plus à trancher, mais l'écran la montre encore, pour qu'on puisse
    /// revenir sur la décision.
    /// </summary>
    public bool IsExempted { get; init; }
}

/// <summary>
/// Signale ce qui n'a été détecté par aucune règle mais reste probablement
/// sensible.
///
/// Le principe est de signaler, jamais de masquer d'office : une chaîne à haute
/// entropie peut tout aussi bien être une empreinte de paquet, un identifiant
/// de compilation ou une clé publique, et les masquer toutes rendrait
/// inutilisables des fichiers parfaitement anodins.
///
/// L'analyse porte sur le texte source, en écartant les zones déjà remplacées.
/// C'est la seule formulation correcte de la question posée : « qu'est-ce qui,
/// dans ce fichier, aurait dû être masqué et ne l'a pas été ». Analyser la
/// sortie signalerait au contraire les substitutions de l'outil lui-même.
/// </summary>
public static partial class ResidualScanner
{
    private const int TimeoutMilliseconds = 3000;

    /// <summary>
    /// Seuil d'entropie normalisée. La forme normalisée est indispensable :
    /// l'entropie de Shannon d'une chaîne de longueur L plafonne à
    /// log2(min(L, taille de l'alphabet observé)), si bien qu'un seuil absolu
    /// de 4,5 bits par caractère est mathématiquement inatteignable en deçà de
    /// vingt-trois caractères. Rapporter l'entropie à son maximum théorique
    /// donne une mesure comparable quelle que soit la longueur, et sans seuil
    /// distinct pour l'hexadécimal et le base64.
    /// </summary>
    private const double EntropyThreshold = 0.80;

    private const int MinimumBase64Length = 40;

    /// <summary>Bloc encadré au format PEM.</summary>
    [GeneratedRegex(@"-----BEGIN (?<label>[A-Z0-9 ]+)-----.*?-----END \k<label>-----", RegexOptions.Singleline, TimeoutMilliseconds)]
    private static partial Regex PemBlock();

    /// <summary>
    /// Suite maximale de caractères de l'alphabet base64 étendu, d'au moins
    /// vingt-quatre caractères. Cette longueur minimale est le filtre le plus
    /// discriminant de toute l'analyse ; l'entropie n'arrive qu'en second.
    /// </summary>
    [GeneratedRegex(@"[A-Za-z0-9+/=_\-]{24,}", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex HighEntropyCandidate();

    /// <summary>Ligne dont la clé annonce une empreinte plutôt qu'un secret.</summary>
    [GeneratedRegex(@"(?i)(integrity|checksum|thumbprint|fingerprint|sha\d*|md5|hash|etag|commit)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex ChecksumContext();

    /// <summary>
    /// Analyse un texte source en ignorant les portions déjà remplacées.
    /// </summary>
    /// <param name="text">Texte source, avant obfuscation</param>
    /// <param name="applied">Spans effectivement remplacés, en coordonnées du texte source</param>
    /// <returns>Les éléments suspects restants, du plus au moins probable</returns>
    public static IReadOnlyList<ResidualFinding> Scan(string text, IReadOnlyList<DetectedSpan> applied)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(applied);

        int[] lineStarts = BuildLineIndex(text);
        List<ResidualFinding> findings = [];

        // L'ordre compte : un bloc PEM examiné après coup par le détecteur de
        // base64 serait découpé ligne par ligne et produirait une dizaine de
        // signalements pour un seul secret.
        foreach (Match match in PemBlock().Matches(text))
        {
            if (IsAlreadyHandled(match.Index, match.Length, applied))
            {
                continue;
            }

            findings.Add(new ResidualFinding(
                "Bloc encadré",
                match.Index,
                match.Length,
                LineOf(lineStarts, match.Index),
                match.Groups["label"].Value,
                SensitiveText.Mask(match.Groups["label"].Value),
                1.0,
                "Bloc de clé ou de certificat laissé en clair : à masquer avant tout envoi."));
        }

        foreach (Match match in HighEntropyCandidate().Matches(text))
        {
            if (IsAlreadyHandled(match.Index, match.Length, applied) || OverlapsPem(text, match.Index))
            {
                continue;
            }

            int line = LineOf(lineStarts, match.Index);
            string lineText = LineText(text, lineStarts, line);

            if (TryDecodeReadableBase64(match.Value, out string decoded))
            {
                findings.Add(new ResidualFinding(
                    "Base64 lisible",
                    match.Index,
                    match.Length,
                    line,
                    match.Value,
                    SensitiveText.Mask(decoded),
                    1.0,
                    "Le décodage produit du texte lisible : très probablement un identifiant ou un mot de passe."));
                continue;
            }

            if (ChecksumContext().IsMatch(lineText))
            {
                continue;
            }

            double score = NormalizedEntropy(match.Value);
            if (score >= EntropyThreshold && HasMixedCharacterClasses(match.Value))
            {
                findings.Add(new ResidualFinding(
                    "Entropie élevée",
                    match.Index,
                    match.Length,
                    line,
                    match.Value,
                    SensitiveText.Mask(match.Value),
                    score,
                    "Chaîne aléatoire non reconnue : vérifier s'il s'agit d'un secret et, si oui, l'ajouter aux règles."));
            }
        }

        return [.. findings.OrderBy(finding => finding.Start)];
    }

    /// <summary>
    /// Calcule l'entropie de Shannon rapportée à son maximum théorique.
    /// </summary>
    /// <param name="value">Chaîne à mesurer</param>
    /// <returns>Une valeur dans l'intervalle [0, 1]</returns>
    public static double NormalizedEntropy(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length < 2)
        {
            return 0;
        }

        Dictionary<char, int> counts = [];
        foreach (char character in value)
        {
            counts[character] = counts.TryGetValue(character, out int previous) ? previous + 1 : 1;
        }

        double entropy = 0;
        foreach (int count in counts.Values)
        {
            double probability = (double)count / value.Length;
            entropy -= probability * Math.Log2(probability);
        }

        double maximum = Math.Log2(Math.Min(value.Length, counts.Count));
        return maximum <= 0 ? 0 : entropy / maximum;
    }

    /// <summary>
    /// Indique si une chaîne mêle au moins deux classes de caractères, ce qui
    /// écarte les suites purement numériques ou purement alphabétiques.
    /// </summary>
    /// <param name="value">Chaîne à examiner</param>
    /// <returns><c>true</c> si au moins deux classes sont présentes</returns>
    private static bool HasMixedCharacterClasses(string value)
    {
        bool lower = value.Any(char.IsAsciiLetterLower);
        bool upper = value.Any(char.IsAsciiLetterUpper);
        bool digit = value.Any(char.IsAsciiDigit);
        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) >= 2;
    }

    /// <summary>
    /// Tente de décoder une chaîne en base64 et vérifie que le résultat est du
    /// texte lisible. Un <c>Basic dXNlcjpwYXNz</c> révèle alors un couple
    /// identifiant / mot de passe, cas de loin le plus fréquent.
    /// </summary>
    /// <param name="value">Chaîne candidate</param>
    /// <param name="decoded">Texte décodé, le cas échéant</param>
    /// <returns><c>true</c> si le décodage produit du texte imprimable</returns>
    private static bool TryDecodeReadableBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length < MinimumBase64Length || value.Length % 4 != 0)
        {
            return false;
        }

        Span<byte> buffer = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out int written) || written == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = buffer[..written];
        foreach (byte item in bytes)
        {
            if (item is not (>= 0x20 and < 0x7F) and not (byte)'\n' and not (byte)'\r' and not (byte)'\t')
            {
                return false;
            }
        }

        decoded = Encoding.ASCII.GetString(bytes);
        return true;
    }

    /// <summary>
    /// Indique si une portion de texte a déjà été remplacée.
    /// </summary>
    /// <param name="start">Début de la portion</param>
    /// <param name="length">Longueur de la portion</param>
    /// <param name="applied">Spans remplacés</param>
    /// <returns><c>true</c> si la portion recoupe un remplacement</returns>
    private static bool IsAlreadyHandled(int start, int length, IReadOnlyList<DetectedSpan> applied)
    {
        int end = start + length;
        return applied.Any(span => start < span.End && span.Start < end);
    }

    /// <summary>
    /// Indique si une position tombe dans un bloc encadré, déjà signalé comme
    /// tel.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="position">Position à tester</param>
    /// <returns><c>true</c> si la position est à l'intérieur d'un bloc PEM</returns>
    private static bool OverlapsPem(string text, int position)
    {
        return PemBlock().Matches(text).Any(match => position >= match.Index && position < match.Index + match.Length);
    }

    /// <summary>
    /// Construit l'index des débuts de ligne, pour convertir une position en
    /// numéro de ligne en temps logarithmique plutôt qu'en recomptant à chaque
    /// signalement.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <returns>Les positions de début de chaque ligne</returns>
    private static int[] BuildLineIndex(string text)
    {
        List<int> starts = [0];
        for (int position = 0; position < text.Length; position++)
        {
            if (text[position] == '\n')
            {
                starts.Add(position + 1);
            }
        }

        return [.. starts];
    }

    /// <summary>
    /// Retrouve le numéro de ligne d'une position.
    /// </summary>
    /// <param name="lineStarts">Index des débuts de ligne</param>
    /// <param name="position">Position dans le texte</param>
    /// <returns>Le numéro de ligne, à partir de 1</returns>
    private static int LineOf(int[] lineStarts, int position)
    {
        int index = Array.BinarySearch(lineStarts, position);
        return index >= 0 ? index + 1 : ~index;
    }

    /// <summary>
    /// Extrait le texte d'une ligne.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="lineStarts">Index des débuts de ligne</param>
    /// <param name="line">Numéro de ligne, à partir de 1</param>
    /// <returns>Le contenu de la ligne</returns>
    private static string LineText(string text, int[] lineStarts, int line)
    {
        int start = lineStarts[line - 1];
        int end = line < lineStarts.Length ? lineStarts[line] - 1 : text.Length;
        return text[start..Math.Max(start, end)];
    }
}
