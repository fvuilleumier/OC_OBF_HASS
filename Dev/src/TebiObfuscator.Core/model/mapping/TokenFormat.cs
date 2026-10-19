using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Jeton neutre identifié : ce que l'on retrouve dans un texte, une fois
/// nettoyé des variations qu'un modèle de langage peut y introduire.
/// </summary>
/// <param name="Prefix">Préfixe de l'outil, en majuscules</param>
/// <param name="Nonce">Nonce du projet ayant émis le jeton, en majuscules</param>
/// <param name="Family">Code de famille, en majuscules</param>
/// <param name="Index">Numéro d'ordre dans la famille</param>
public readonly record struct TokenIdentity(string Prefix, string Nonce, string Family, int Index)
{
    /// <summary>
    /// Forme canonique du jeton, utilisée comme clé de recherche dans le coffre.
    /// </summary>
    public string Canonical => string.Create(
        CultureInfo.InvariantCulture,
        $"{Prefix}{Nonce}_{Family}_{Index:D4}");
}

/// <summary>
/// Fabrique et reconnaît les jetons neutres de la forme <c>TBXA7_HOST_0001</c>.
///
/// Le principe retenu est celui de robustesse : générateur strict, reconnaisseur
/// laxiste. Trois contraintes dictent cette forme.
/// D'abord la numérotation à largeur fixe : sans elle, <c>HOST_001</c> serait un
/// préfixe de <c>HOST_0011</c> et la désobfuscation abîmerait le second.
/// Ensuite le nonce de projet : il rend improbable qu'un jeton d'un autre projet
/// soit restitué par erreur, et permet de signaler un mapping qui ne correspond
/// pas au fichier.
/// Enfin la reconnaissance tolérante : un modèle qui réécrit du Markdown peut
/// produire <c>TBXA7\_HOST\_0001</c>, et un modèle qui génère du code peut
/// remplacer les soulignés par des tirets.
/// </summary>
public sealed class TokenFormat
{
    /// <summary>Préfixe utilisé par défaut par l'outil.</summary>
    public const string DefaultPrefix = "TBX";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private const string NonceAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly Regex _recognizer;
    private readonly Regex _lenientRecognizer;

    /// <summary>
    /// Construit un format de jeton pour un projet donné.
    /// </summary>
    /// <param name="prefix">Préfixe de l'outil, trois lettres par convention</param>
    /// <param name="nonce">Nonce du projet, deux caractères alphanumériques</param>
    public TokenFormat(string prefix, string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        Prefix = prefix.ToUpperInvariant();
        Nonce = nonce.ToUpperInvariant();

        // Le préfixe étant configurable, l'expression ne peut pas être générée à
        // la compilation ; elle est donc construite ici, une fois, avec un
        // délai maximal — les fichiers analysés ne sont pas maîtrisés.
        // Les séparateurs sont optionnels et tolèrent un antislash
        // d'échappement Markdown ; le nonce reconnu est quelconque, afin de
        // geler aussi les jetons issus d'un autre projet.
        string pattern =
            $@"(?<prefix>{Regex.Escape(Prefix)})(?<nonce>[A-Za-z0-9]{{2}})\\?[_\-]?" +
            @"(?<family>[A-Za-z]{2,12})\\?[_\-]?(?<index>\d{4,6})";

        _recognizer = new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            MatchTimeout);

        // À la restitution, on tolère en plus un numéro dont un modèle a retiré
        // les zéros de tête — « TBXA7_HOST_1 » pour « TBXA7_HOST_0001 », forme
        // qu'il produit volontiers en réécrivant du code. Cette forme courte
        // n'est reconnue qu'avec ses deux séparateurs, et seulement ici : pour
        // geler les jetons d'un texte source, la forme stricte reste de mise,
        // afin qu'un nom réel commençant par le préfixe ne soit pas pris pour
        // un jeton et laissé en clair.
        string shortForm =
            $@"(?<prefix>{Regex.Escape(Prefix)})(?<nonce>[A-Za-z0-9]{{2}})\\?[_\-]" +
            @"(?<family>[A-Za-z]{2,12})\\?[_\-](?<index>\d{1,3})(?!\d)";

        _lenientRecognizer = new Regex(
            $"{pattern}|{shortForm}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            MatchTimeout);
    }

    /// <summary>Préfixe de l'outil, en majuscules.</summary>
    public string Prefix { get; }

    /// <summary>Nonce du projet courant, en majuscules.</summary>
    public string Nonce { get; }

    /// <summary>
    /// Tire au sort un nonce de projet. L'alphabet exclut les caractères
    /// visuellement ambigus (0/O, 1/I) pour qu'un jeton reste lisible et
    /// recopiable à la main sans erreur.
    /// </summary>
    /// <returns>Un nonce de deux caractères</returns>
    public static string CreateNonce()
    {
        char[] nonce = new char[2];
        for (int i = 0; i < nonce.Length; i++)
        {
            nonce[i] = NonceAlphabet[RandomNumberGenerator.GetInt32(NonceAlphabet.Length)];
        }

        return new string(nonce);
    }

    /// <summary>
    /// Produit le jeton canonique d'une famille et d'un numéro d'ordre.
    /// </summary>
    /// <param name="family">Code de famille, par exemple <c>HOST</c></param>
    /// <param name="index">Numéro d'ordre, à partir de 1</param>
    /// <returns>Le jeton sous sa forme stricte, par exemple <c>TBXA7_HOST_0001</c></returns>
    public string Compose(string family, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);

        return new TokenIdentity(Prefix, Nonce, family.ToUpperInvariant(), index).Canonical;
    }

    /// <summary>
    /// Tente de reconnaître un jeton dans un texte court, par exemple une valeur
    /// isolée. La reconnaissance est tolérante aux séparateurs échappés ou
    /// absents et à la casse.
    /// </summary>
    /// <param name="candidate">Texte supposé contenir un jeton</param>
    /// <param name="identity">Jeton reconnu, sous sa forme canonique</param>
    /// <returns><c>true</c> si un jeton a été reconnu</returns>
    public bool TryParse(string candidate, out TokenIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        Match match = _recognizer.Match(candidate);
        if (!match.Success)
        {
            return false;
        }

        identity = ToIdentity(match);
        return true;
    }

    /// <summary>
    /// Recense tous les jetons déjà présents dans un texte et les retourne sous
    /// forme de zones gelées. C'est le mécanisme d'idempotence : repasser un
    /// fichier déjà obfusqué ne doit rien changer, donc ces intervalles doivent
    /// être soustraits aux candidats de tous les autres détecteurs.
    /// </summary>
    /// <param name="text">Texte à examiner</param>
    /// <returns>Un span de catégorie <see cref="SpanCategory.ProtectedToken"/> par jeton trouvé</returns>
    public IReadOnlyList<DetectedSpan> FindProtectedSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<DetectedSpan> spans = [];
        foreach (Match match in _recognizer.Matches(text))
        {
            spans.Add(new DetectedSpan(
                match.Index,
                match.Length,
                SpanCategory.ProtectedToken,
                match.Value,
                SpanCategories.ProtectedTokenPriority,
                1.0,
                RuleId: "token.protected"));
        }

        return spans;
    }

    /// <summary>
    /// Recense tous les jetons d'un texte avec leur position, pour la
    /// désobfuscation.
    /// </summary>
    /// <param name="text">Texte à examiner, typiquement une réponse de modèle</param>
    /// <returns>Les occurrences trouvées, dans l'ordre d'apparition</returns>
    public IReadOnlyList<TokenOccurrence> FindOccurrences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<TokenOccurrence> occurrences = [];
        foreach (Match match in _lenientRecognizer.Matches(text))
        {
            TokenIdentity identity = ToIdentity(match);

            // Un numéro nul ne correspond à aucun jeton émis, les numéros
            // commençant à 1.
            if (identity.Index > 0)
            {
                occurrences.Add(new TokenOccurrence(match.Index, match.Length, identity, match.Value));
            }
        }

        return occurrences;
    }

    /// <summary>
    /// Convertit une correspondance de l'expression régulière en jeton canonique.
    /// </summary>
    /// <param name="match">Correspondance produite par le reconnaisseur</param>
    /// <returns>Le jeton, normalisé en majuscules</returns>
    private static TokenIdentity ToIdentity(Match match)
    {
        return new TokenIdentity(
            match.Groups["prefix"].Value.ToUpperInvariant(),
            match.Groups["nonce"].Value.ToUpperInvariant(),
            match.Groups["family"].Value.ToUpperInvariant(),
            int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Occurrence d'un jeton dans un texte : sa position, son identité canonique et
/// la forme exacte sous laquelle il apparaissait (qui peut différer de la forme
/// stricte si un modèle l'a reformatée).
/// </summary>
/// <param name="Start">Index de début dans le texte</param>
/// <param name="Length">Longueur de l'occurrence</param>
/// <param name="Identity">Jeton reconnu, sous forme canonique</param>
/// <param name="RawText">Texte exact de l'occurrence</param>
public sealed record TokenOccurrence(int Start, int Length, TokenIdentity Identity, string RawText);
