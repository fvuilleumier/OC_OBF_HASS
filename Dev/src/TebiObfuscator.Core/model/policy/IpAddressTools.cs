using System.Globalization;
using System.Net;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Outils de classement des adresses IPv4.
///
/// Ce classement conditionne trois décisions du moteur : ce qui doit être
/// masqué, ce qui doit être conservé parce qu'il porte le contexte technique,
/// et ce qui ne doit surtout pas être touché sous peine de rendre la
/// configuration incompréhensible.
/// </summary>
public static class IpAddressTools
{
    /// <summary>Première adresse du réservoir de substitution, 198.18.0.0/15 (RFC 2544).</summary>
    public const uint SubstitutionPoolStart = 0xC6120000;

    /// <summary>Nombre d'adresses du réservoir de substitution.</summary>
    public const uint SubstitutionPoolSize = 0x20000;

    /// <summary>
    /// Convertit une adresse IPv4 textuelle en entier non signé.
    /// </summary>
    /// <param name="text">Adresse sous forme pointée</param>
    /// <param name="address">Valeur numérique correspondante</param>
    /// <returns><c>true</c> si le texte est une adresse IPv4 en notation pointée complète</returns>
    public static bool TryParse(string text, out uint address)
    {
        address = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // L'analyseur du cadriciel accepte des formes abrégées à deux ou trois
        // octets. On exige ici les quatre octets, sans quoi un numéro de
        // version serait pris pour une adresse.
        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!IPAddress.TryParse(text, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = parsed.GetAddressBytes();
        address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    /// <summary>
    /// Lit une adresse IPv4 complète ou son début : de deux à quatre nombres de
    /// 0 à 255 séparés par des points, sans signe ni espace — <c>10.10</c>,
    /// <c>192.168.1</c>, <c>192.168.1.25</c>.
    ///
    /// Un début d'adresse est ce qu'un utilisateur masque quand il veut cacher
    /// son plan d'adressage plutôt qu'une machine. Le reconnaître comme tel,
    /// c'est lui donner un pseudonyme qui a encore la forme d'une adresse, au
    /// lieu d'un nom de machine collé au reste de l'adresse.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <param name="value">Octets lus, alignés à gauche : « 192.168.1 » donne la valeur de 192.168.1.0</param>
    /// <param name="octets">Nombre d'octets écrits, de 2 à 4</param>
    /// <returns><c>true</c> si le texte a la forme d'une adresse ou d'un début d'adresse</returns>
    public static bool TryParseAddressOrPrefix(string text, out uint value, out int octets)
    {
        value = 0;
        octets = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 15)
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        uint read = 0;
        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            int number = int.Parse(part, CultureInfo.InvariantCulture);
            if (number > 255)
            {
                return false;
            }

            read = (read << 8) | (uint)number;
        }

        octets = parts.Length;
        value = read << (8 * (4 - octets));
        return true;
    }

    /// <summary>
    /// Indique si un texte est un début d'adresse IPv4 : deux ou trois octets,
    /// comme <c>10.10</c> ou <c>192.168.1</c>.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <returns><c>true</c> pour un début d'adresse, <c>false</c> pour une adresse complète ou autre chose</returns>
    public static bool IsAddressPrefix(string text)
    {
        return TryParseAddressOrPrefix(text, out _, out int octets) && octets < 4;
    }

    /// <summary>
    /// Restitue une adresse numérique sous sa forme pointée.
    /// </summary>
    /// <param name="address">Adresse sous forme numérique</param>
    /// <returns>L'adresse en notation pointée</returns>
    public static string Format(uint address)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}");
    }

    /// <summary>
    /// Restitue le début d'une adresse numérique sous sa forme pointée.
    /// </summary>
    /// <param name="address">Adresse sous forme numérique, alignée à gauche</param>
    /// <param name="octets">Nombre d'octets à écrire, de 1 à 4</param>
    /// <returns>Les premiers octets, en notation pointée : « 198.18.4 » pour trois octets</returns>
    public static string FormatPrefix(uint address, int octets)
    {
        string[] parts = Format(address).Split('.');
        return string.Join('.', parts.Take(Math.Clamp(octets, 1, 4)));
    }

    /// <summary>
    /// Indique si une adresse appartient à une plage privée ou partagée
    /// (RFC 1918 et CGNAT).
    /// </summary>
    /// <param name="address">Adresse à classer</param>
    /// <returns><c>true</c> s'il s'agit d'une adresse non routable sur Internet</returns>
    public static bool IsPrivate(uint address)
    {
        return TryGetPrivateBlock(address, out _, out _);
    }

    /// <summary>
    /// Donne la plage privée ou partagée à laquelle appartient une adresse.
    /// </summary>
    /// <param name="address">Adresse à classer</param>
    /// <param name="network">Première adresse de la plage</param>
    /// <param name="prefixLength">Longueur du préfixe de la plage : 8, 10, 12 ou 16</param>
    /// <returns><c>true</c> si l'adresse est privée ou partagée</returns>
    public static bool TryGetPrivateBlock(uint address, out uint network, out int prefixLength)
    {
        (network, prefixLength) = address switch
        {
            _ when InRange(address, 0x0A000000, 8) => (0x0A000000u, 8),       // 10.0.0.0/8
            _ when InRange(address, 0xAC100000, 12) => (0xAC100000u, 12),     // 172.16.0.0/12
            _ when InRange(address, 0xC0A80000, 16) => (0xC0A80000u, 16),     // 192.168.0.0/16
            _ when InRange(address, 0x64400000, 10) => (0x64400000u, 10),     // 100.64.0.0/10
            _ => (0u, 0),
        };

        return prefixLength > 0;
    }

    /// <summary>
    /// Indique si une adresse a une signification protocolaire propre et doit
    /// donc rester lisible : bouclage, lien local, diffusion, multidiffusion.
    /// </summary>
    /// <param name="address">Adresse à classer</param>
    /// <returns><c>true</c> si l'adresse ne doit jamais être masquée</returns>
    public static bool IsSpecialPurpose(uint address)
    {
        return address is 0x00000000 or 0xFFFFFFFF
            || InRange(address, 0x7F000000, 8)      // 127.0.0.0/8 bouclage
            || InRange(address, 0xA9FE0000, 16)     // 169.254.0.0/16 lien local
            || InRange(address, 0xE0000000, 4)      // 224.0.0.0/4 multidiffusion
            || InRange(address, 0xF0000000, 4);     // 240.0.0.0/4 réservé
    }

    /// <summary>
    /// Indique si une adresse appartient à une plage réservée à la
    /// documentation, y compris le réservoir de substitution de l'outil.
    /// </summary>
    /// <param name="address">Adresse à classer</param>
    /// <returns><c>true</c> s'il s'agit d'une adresse d'exemple ou de substitution</returns>
    public static bool IsDocumentationOrSubstitution(uint address)
    {
        return InRange(address, 0xC0000200, 24)     // 192.0.2.0/24
            || InRange(address, 0xC6336400, 24)     // 198.51.100.0/24
            || InRange(address, 0xCB007100, 24)     // 203.0.113.0/24
            || InRange(address, SubstitutionPoolStart, 15);
    }

    /// <summary>
    /// Indique si une valeur est un masque de sous-réseau ou un masque
    /// générique Cisco.
    ///
    /// C'est la règle la plus indispensable de toute la liste blanche :
    /// remplacer <c>255.255.255.0</c> par une pseudo-adresse détruit le sens de
    /// la configuration entière. La reconnaissance exige un premier octet à 255
    /// ou à 0 — ce qui couvre tous les masques réellement écrits dans des
    /// configurations — plutôt que de se fier à la seule contiguïté binaire,
    /// qui classerait <c>128.0.0.0</c> comme masque alors que c'est
    /// vraisemblablement une adresse.
    /// </summary>
    /// <param name="address">Valeur à classer</param>
    /// <returns><c>true</c> s'il s'agit d'un masque plutôt que d'une adresse d'hôte</returns>
    public static bool IsSubnetMaskOrWildcard(uint address)
    {
        uint firstOctet = (address >> 24) & 0xFF;
        if (firstOctet is not (255 or 0))
        {
            return false;
        }

        // Masque : les bits à 1 forment un préfixe contigu.
        uint inverted = ~address;
        if ((inverted & (inverted + 1)) == 0)
        {
            return true;
        }

        // Masque générique Cisco : les bits à 1 forment un suffixe contigu.
        return (address & (address + 1)) == 0;
    }

    /// <summary>
    /// Teste l'appartenance d'une adresse à un préfixe.
    /// </summary>
    /// <param name="address">Adresse à tester</param>
    /// <param name="network">Adresse de réseau</param>
    /// <param name="prefixLength">Longueur du préfixe, en bits</param>
    /// <returns><c>true</c> si l'adresse appartient au préfixe</returns>
    private static bool InRange(uint address, uint network, int prefixLength)
    {
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return (address & mask) == (network & mask);
    }
}
