using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Détecte les identifiants de personnes et d'entreprises : adresses de
/// courrier, numéros AVS, IBAN, numéros IDE, cartes de paiement.
///
/// Toutes ces règles sauf la première sont adossées à une somme de contrôle
/// vérifiée après coup, ce qui ramène leur taux de faux positifs à presque
/// rien : une suite de chiffres quelconque ne satisfait pas par accident la clé
/// d'un IBAN ou d'un numéro AVS.
/// </summary>
public sealed partial class IdentityDetector() : RegexRuleDetector("identity", BuildRules())
{
    private const int TimeoutMilliseconds = 2000;

    /// <summary>
    /// Adresse de courrier électronique.
    ///
    /// La borne arrière écarte les autorités d'URL : dans
    /// <c>ssh://admin@jump.exemple.ch</c>, la portion avant l'arobase est un
    /// compte, pas une adresse de courrier. Sans cette exclusion, le span
    /// « adresse » engloberait le compte et l'hôte d'un seul tenant, et l'URL
    /// perdrait sa décomposition — le modèle ne verrait plus ni compte ni
    /// machine, seulement une adresse de courrier qui n'existe pas.
    /// </summary>
    [GeneratedRegex(@"(?<!://[^\s/@]*)\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex EmailAddress();

    /// <summary>Numéro AVS/AHV suisse, sous sa forme ponctuée usuelle.</summary>
    [GeneratedRegex(@"\b756\.\d{4}\.\d{4}\.\d{2}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex SocialSecurityNumber();

    /// <summary>IBAN, éventuellement écrit par groupes de quatre caractères.</summary>
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Iban();

    /// <summary>Numéro IDE/UID d'entreprise suisse.</summary>
    [GeneratedRegex(@"\bCHE-\d{3}\.\d{3}\.\d{3}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex BusinessIdentifier();

    /// <summary>Numéro de carte de paiement, avec ou sans séparateurs.</summary>
    [GeneratedRegex(@"\b\d{4}[ \-]?\d{4}[ \-]?\d{4}[ \-]?\d{1,7}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex PaymentCard();

    /// <summary>Identifiant global, sous sa forme canonique à tirets.</summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex GlobalIdentifier();

    /// <summary>Numéro de téléphone suisse ou international au format E.164.</summary>
    [GeneratedRegex(@"(?<![\d.\-])(?:\+41|0041)[ ]?\(?0?\)?[ ]?\d{2}[ ]?\d{3}[ ]?\d{2}[ ]?\d{2}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex SwissPhoneNumber();

    /// <summary>
    /// Valide les correspondances par leur somme de contrôle lorsqu'elles en
    /// possèdent une.
    /// </summary>
    /// <param name="rule">Règle ayant produit la correspondance</param>
    /// <param name="value">Valeur capturée</param>
    /// <returns><c>true</c> si la correspondance est un identifiant valide</returns>
    protected override bool Accept(RegexRule rule, string value)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(value);

        return rule.RuleId switch
        {
            "identity.avs" => Checksums.IsValidEan13(KeepDigits(value)),
            "identity.iban" => Checksums.IsValidIban(value),
            "identity.ide" => Checksums.IsValidSwissBusinessIdentifier(KeepDigits(value)),
            "identity.paymentCard" => Checksums.IsValidLuhn(KeepDigits(value)),
            _ => true,
        };
    }

    /// <summary>
    /// Écarte la règle des cartes de paiement pour un nom de fichier : une
    /// longue suite de chiffres y est un horodatage, et la clé de Luhn,
    /// satisfaite une fois sur dix par hasard, n'y prouve rien.
    /// </summary>
    /// <param name="rule">Règle examinée</param>
    /// <returns><c>true</c> si la règle s'applique aussi à un nom</returns>
    protected override bool AppliesToName(RegexRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.RuleId != "identity.paymentCard";
    }

    /// <summary>
    /// Ne conserve que les chiffres d'une valeur, les formats normalisés
    /// admettant points, espaces et tirets comme séparateurs de lecture.
    /// </summary>
    /// <param name="value">Valeur capturée</param>
    /// <returns>La suite de chiffres correspondante</returns>
    private static string KeepDigits(string value)
    {
        return new string([.. value.Where(char.IsAsciiDigit)]);
    }

    /// <summary>
    /// Construit le jeu de règles du détecteur.
    /// </summary>
    /// <returns>Les règles à appliquer</returns>
    private static IEnumerable<RegexRule> BuildRules()
    {
        return
        [
            new RegexRule("identity.email", SpanCategory.Email, EmailAddress()),
            new RegexRule("identity.avs", SpanCategory.SocialSecurityNumber, SocialSecurityNumber()),
            new RegexRule("identity.iban", SpanCategory.Iban, Iban()),
            new RegexRule("identity.ide", SpanCategory.BusinessIdentifier, BusinessIdentifier()),
            new RegexRule("identity.paymentCard", SpanCategory.Other, PaymentCard()),
            new RegexRule("identity.phone", SpanCategory.PhoneNumber, SwissPhoneNumber(), Confidence: 0.8),

            // Tout ce qui a la forme d'un GUID est masqué, accolades comprises
            // ou non : un GUID de coffre, de locataire ou d'application désigne
            // une installation précise. La catégorie peut être décochée.
            new RegexRule("identity.guid", SpanCategory.GlobalIdentifier, GlobalIdentifier(), Confidence: 0.6),
        ];
    }
}
