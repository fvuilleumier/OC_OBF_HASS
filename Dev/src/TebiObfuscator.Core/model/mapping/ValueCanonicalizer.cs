using System.Text;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Réduit une valeur observée à la forme qui sert de clé dans le coffre.
///
/// L'enjeu est qu'une même entité écrite de plusieurs façons reçoive un seul
/// pseudonyme : <c>SRV-DC01.tebicom.ch</c> et <c>srv-dc01.TEBICOM.CH</c>
/// désignent la même machine et doivent se ressembler pour le modèle. À
/// l'inverse, un secret ne doit jamais être normalisé : la casse y est
/// significative, et deux mots de passe qui ne diffèrent que par elle sont deux
/// secrets distincts.
///
/// La normalisation Unicode n'est appliquée qu'à la clé de comparaison, jamais
/// au texte source : normaliser le texte lui-même déplacerait tous les offsets
/// calculés par les détecteurs.
/// </summary>
public static class ValueCanonicalizer
{
    /// <summary>
    /// Catégories dont la casse ne distingue pas deux entités différentes.
    /// </summary>
    private static readonly HashSet<SpanCategory> CaseInsensitiveCategories =
    [
        SpanCategory.Email,
        SpanCategory.Fqdn,
        SpanCategory.DomainName,
        SpanCategory.Hostname,
        SpanCategory.IpV6,
        SpanCategory.MacAddress,
        SpanCategory.GlobalIdentifier,
        SpanCategory.UncPath,
        SpanCategory.UserPath,
        SpanCategory.Organization,
        SpanCategory.Person,
        SpanCategory.Client,
        SpanCategory.Site,
        SpanCategory.Iban,
        SpanCategory.BusinessIdentifier,
    ];

    /// <summary>
    /// Calcule la forme canonique d'une valeur pour une catégorie donnée.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <param name="raw">Valeur telle qu'elle apparaît dans le fichier source</param>
    /// <returns>La clé d'attribution correspondante</returns>
    public static string Canonicalize(SpanCategory category, string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string canonical = raw.Trim();

        // Une adresse écrite avec des zéros de tête — 010.001.002.003 — est la
        // même que 10.1.2.3 : elle doit recevoir le même pseudonyme.
        if (category == SpanCategory.IpV4 && IpAddressTools.TryParseAddressOrPrefix(canonical, out uint address, out int octets))
        {
            return IpAddressTools.FormatPrefix(address, octets);
        }

        // Les libellés en langage naturel peuvent comporter des espaces
        // multiples ou des sauts de ligne selon la mise en forme du fichier.
        if (category is SpanCategory.Person or SpanCategory.Client or SpanCategory.Site or SpanCategory.Organization)
        {
            canonical = CollapseWhitespace(canonical);
        }

        // Pour les catégories où la casse ne distingue rien, les accents non
        // plus : « Genève » et « Geneve » désignent le même lieu et doivent
        // recevoir le même pseudonyme, sans quoi le modèle y verrait deux
        // entités distinctes.
        return CaseInsensitiveCategories.Contains(category) ? TextFolding.Fold(canonical) : canonical;
    }

    /// <summary>
    /// Remplace toute suite de caractères d'espacement par une espace unique.
    /// </summary>
    /// <param name="value">Valeur à compacter</param>
    /// <returns>La valeur sans espacement redondant</returns>
    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasWhitespace = false;

        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString();
    }
}
