using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Mapping.Strategies;

/// <summary>
/// Remplace une adresse IPv6 par une adresse de la plage de documentation
/// 2001:db8::/32 (RFC 3849). La structure de préfixe n'est pas préservée : les
/// configurations traitées sont très majoritairement en IPv4, et une
/// correspondance sous-réseau par sous-réseau en IPv6 n'apporterait pas assez
/// pour justifier sa complexité.
/// </summary>
public sealed class IpV6PseudonymStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.IpV6;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        return string.Create(CultureInfo.InvariantCulture, $"2001:db8::{request.Index:x4}");
    }
}

/// <summary>
/// Remplace une adresse MAC par une adresse dont le bit « administrée
/// localement » est à 1, ce qui garantit qu'elle n'appartient à aucun
/// constructeur réel. La notation d'origine — deux-points, tirets ou groupes de
/// quatre à la mode Cisco — est conservée, faute de quoi la configuration
/// obfusquée ne ressemblerait plus à ce que produit l'équipement.
/// </summary>
public sealed class MacPseudonymStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.MacAddress;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        int index = request.Index;
        string hex = string.Create(CultureInfo.InvariantCulture, $"020000{index:X6}");

        if (request.Original.Contains('.', StringComparison.Ordinal))
        {
            return $"{hex[..4]}.{hex[4..8]}.{hex[8..]}".ToLowerInvariant();
        }

        char separator = request.Original.Contains('-', StringComparison.Ordinal) ? '-' : ':';
        string[] octets = new string[6];
        for (int position = 0; position < 6; position++)
        {
            octets[position] = hex.Substring(position * 2, 2);
        }

        return string.Join(separator, octets);
    }
}

/// <summary>
/// Remplace un port d'écoute par un autre port valide, pris dans une plage
/// haute peu employée en configuration.
///
/// Le choix de la plage n'est pas cosmétique. Un pseudonyme de port est un
/// nombre court : s'il tombait sur une valeur usuelle comme 8080 ou 3000, la
/// restitution risquerait de remplacer une occurrence sans rapport rencontrée
/// ailleurs dans le texte. Une plage que personne n'écrit à la main réduit ce
/// risque à presque rien, tout en restant un port syntaxiquement valide.
/// </summary>
public sealed class PortPseudonymStrategy : IPseudonymStrategy
{
    /// <summary>Premier port du réservoir de substitution.</summary>
    private const int PoolStart = 41000;

    /// <summary>Nombre de ports disponibles avant d'atteindre la limite des seize bits.</summary>
    private const int PoolSize = 20000;

    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.NetworkPort;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        // Réservoir épuisé : on bascule sur un jeton neutre plutôt que de
        // réutiliser un port, ce qui rendrait la restitution ambiguë.
        if (request.Index > PoolSize)
        {
            return request.Format.Compose(SpanCategory.NetworkPort.FamilyCode(), request.Index);
        }

        return (PoolStart + request.Index).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Remplace une adresse de courrier par une adresse du domaine réservé
/// <c>example.invalid</c> (RFC 2606). Le domaine <c>example.com</c> serait un
/// mauvais choix : un modèle l'écrit spontanément en permanence, et l'on ne
/// saurait plus distinguer un pseudonyme d'une invention du modèle.
/// </summary>
public sealed class EmailPseudonymStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.Email;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        return string.Create(CultureInfo.InvariantCulture, $"user{request.Index:D4}@example.invalid");
    }
}

/// <summary>
/// Remplace un domaine ou un nom pleinement qualifié par un nom du domaine
/// réservé <c>example.invalid</c>, en conservant la distinction entre les deux
/// afin que le modèle sache s'il regarde une machine ou une zone.
/// </summary>
public sealed class DomainPseudonymStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category)
    {
        return category is SpanCategory.DomainName or SpanCategory.Fqdn;
    }

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string label = request.Category == SpanCategory.DomainName ? "dom" : "host";
        return string.Create(CultureInfo.InvariantCulture, $"{label}{request.Index:D4}.example.invalid");
    }
}

/// <summary>
/// Remplace un identifiant global par un autre identifiant global valide, de
/// version 4. La valeur est dérivée du nonce du projet et du numéro d'ordre, et
/// non de l'identifiant réel : un pseudonyme ne doit jamais être une fonction
/// de la donnée qu'il masque, sous peine d'en devenir un condensat exploitable.
/// </summary>
public sealed class GuidPseudonymStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.GlobalIdentifier;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string seed = string.Create(CultureInfo.InvariantCulture, $"{request.Format.Nonce}|guid|{request.Index}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        byte[] bytes = digest[..16];

        // Bits de version (4) et de variante (RFC 4122) forcés, pour que la
        // valeur soit un identifiant valide et non une suite d'octets.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes).ToString();
    }
}

/// <summary>
/// Remplace la valeur d'une clé « id ».
///
/// Un identifiant fait de chiffres reçoit un autre nombre : dans un document
/// JSON ou YAML, <c>"id": 4711</c> doit rester un nombre, faute de quoi le
/// document deviendrait invalide. Le nombre est pris dans une plage à neuf
/// chiffres qui commence à 900 000 001, que personne n'écrit à la main : comme
/// pour les ports, c'est ce qui évite qu'une valeur usuelle soit restituée à
/// tort ailleurs dans le texte. Tout autre identifiant reçoit un jeton neutre,
/// qui dit au modèle qu'il regarde un substitut.
/// </summary>
public sealed class IdentifierPseudonymStrategy : IPseudonymStrategy
{
    /// <summary>Base des identifiants numériques de substitution.</summary>
    private const long PoolStart = 900_000_000;

    /// <summary>Nombre d'identifiants numériques disponibles avant d'atteindre dix chiffres.</summary>
    private const int PoolSize = 99_999_999;

    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.Identifier;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string original = request.Original.Trim();
        bool numeric = original.Length > 0 && original.All(char.IsAsciiDigit);

        return numeric && request.Index <= PoolSize
            ? (PoolStart + request.Index).ToString(CultureInfo.InvariantCulture)
            : request.Format.Compose(SpanCategory.Identifier.FamilyCode(), request.Index);
    }
}
