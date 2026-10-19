using System.Net;
using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Détecte les éléments d'adressage réseau : adresses IPv4 et IPv6, adresses
/// MAC, chemins UNC, chemins de session Windows ou Unix, et composants
/// identifiants des URL.
///
/// Les valeurs composites sont volontairement décomposées. Masquer
/// <c>https://admin:secret@vpn.exemple.ch:8443/api/v2</c> d'un seul bloc ferait
/// perdre au modèle le schéma, le port et le chemin, qui sont précisément ce
/// sur quoi on l'interroge ; seuls le compte, le mot de passe et l'hôte sont
/// remplacés.
/// </summary>
public sealed partial class NetworkDetector() : RegexRuleDetector("network", BuildRules())
{
    private const int TimeoutMilliseconds = 2000;

    /// <summary>
    /// Adresse IPv4 en notation pointée, isolée de toute suite plus longue
    /// (<c>1.2.3.4.5</c> n'en est pas une). Un point qui n'est pas suivi d'un
    /// chiffre ne prolonge rien : l'adresse qui termine une phrase, dans une note
    /// ou un courrier, est une adresse comme une autre.
    ///
    /// Un numéro de version à quatre nombres a la forme d'une adresse :
    /// <c>Version="1.0.0.0"</c>, <c>oldVersion="0.0.0.0-11.0.0.0"</c>,
    /// <c>Version=1.0.8.0, Culture=neutral</c>, <c>&lt;add key="Version"
    /// value="1.0.0.0" /&gt;</c>, <c>v1.2.3.4</c>. Remplacé, il rendrait absurdes
    /// les redirections d'assemblys d'un web.config : ce qui suit le mot
    /// « version », ou une lettre v, n'est donc pas une adresse.
    /// </summary>
    [GeneratedRegex(
        @"(?<!\d|\d\.|[vV])" +
        @"(?<!(?i:version)(?:[\s""'>(:=]{0,4}|[""']\s+(?i:value)\s*=\s*[""'])(?:\d{1,3}(?:\.\d{1,3}){3}[ \t]*-[ \t]*)?)" +
        @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?!\d|\.\d)",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex IpV4Address();

    /// <summary>
    /// Port écrit derrière une adresse IPv4, hors URL : <c>192.168.1.2:1883</c>.
    /// Seul le port est repéré ici ; l'adresse relève de la règle des adresses,
    /// qui applique sa propre politique aux adresses privées.
    /// </summary>
    [GeneratedRegex(
        @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d):(?<port>\d{1,5})(?!\d)",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex IpV4WithPort();

    /// <summary>Adresse IPv6 sous forme complète ou abrégée.</summary>
    [GeneratedRegex(@"(?<![:\w])(?:[0-9A-Fa-f]{1,4}:){2,7}(?::|[0-9A-Fa-f]{1,4})(?![:\w])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex IpV6Address();

    /// <summary>Adresse MAC, notation à deux-points, tirets ou style Cisco.</summary>
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b|\b(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4}\b", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex MacAddress();

    /// <summary>Chemin UNC : serveur et partage sont repérés séparément.</summary>
    [GeneratedRegex(@"\\\\(?<server>[A-Za-z0-9._\-]+)\\(?<share>[A-Za-z0-9._$\-]+)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex UncPath();

    /// <summary>Nom de session dans un chemin de profil Windows.</summary>
    [GeneratedRegex(@"(?i)[A-Z]:\\Users\\(?<login>[^\\\r\n""'/]+)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex WindowsUserPath();

    /// <summary>Nom de session dans un chemin de profil Unix.</summary>
    [GeneratedRegex(@"(?<![\w.])/home/(?<login>[^/\s\r\n""']+)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex UnixUserPath();

    /// <summary>URL : seuls le compte, le mot de passe et l'hôte sont repérés.</summary>
    [GeneratedRegex(
        @"\b[a-zA-Z][a-zA-Z0-9+.\-]*://(?:(?<user>[^:@/\s]+)(?::(?<password>[^@/\s]*))?@)?(?<host>[A-Za-z0-9._\-]+)(?::(?<port>\d{1,5}))?",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex Url();

    /// <summary>
    /// Valide les correspondances dont la forme ne suffit pas à conclure.
    /// </summary>
    /// <param name="rule">Règle ayant produit la correspondance</param>
    /// <param name="value">Valeur capturée</param>
    /// <returns><c>true</c> si la correspondance doit être retenue</returns>
    protected override bool Accept(RegexRule rule, string value)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(value);

        return rule.RuleId switch
        {
            "network.ipv4" => IpAddressTools.TryParse(value, out _),
            "network.ipv4Port" => SensitiveKeyNames.IsPort(value),
            "network.ipv6" => IsRealIpV6(value),

            // L'hôte d'une URL ou le serveur d'un chemin UNC peut être une
            // adresse : c'est alors au détecteur d'adresses de le traiter, avec
            // son propre réservoir et sa politique des adresses privées — et
            // non un nom de machine qui lui ôterait sa forme d'adresse.
            "network.url" or "network.unc" => !IpAddressTools.TryParse(value, out _),
            _ => true,
        };
    }

    /// <summary>
    /// Distingue une véritable adresse IPv6 d'une adresse MAC, que l'analyseur
    /// d'adresses accepte lui aussi faute de pouvoir les départager.
    /// </summary>
    /// <param name="value">Valeur capturée</param>
    /// <returns><c>true</c> s'il s'agit d'une adresse IPv6</returns>
    private static bool IsRealIpV6(string value)
    {
        if (MacAddress().IsMatch(value))
        {
            return false;
        }

        return IPAddress.TryParse(value, out IPAddress? parsed)
            && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }

    /// <summary>
    /// Construit le jeu de règles du détecteur.
    /// </summary>
    /// <returns>Les règles à appliquer</returns>
    private static IEnumerable<RegexRule> BuildRules()
    {
        return
        [
            new RegexRule("network.ipv4", SpanCategory.IpV4, IpV4Address()),
            new RegexRule("network.ipv4Port", SpanCategory.NetworkPort, IpV4WithPort(), Captures:
            [
                new RegexCapture("port", SpanCategory.NetworkPort, "port"),
            ]),
            new RegexRule("network.ipv6", SpanCategory.IpV6, IpV6Address()),
            new RegexRule("network.mac", SpanCategory.MacAddress, MacAddress()),
            new RegexRule("network.unc", SpanCategory.UncPath, UncPath(), Captures:
            [
                new RegexCapture("server", SpanCategory.Hostname, "server"),
                new RegexCapture("share", SpanCategory.UncPath, "share"),
            ]),
            new RegexRule("network.userPath", SpanCategory.UserPath, WindowsUserPath(), "login"),
            new RegexRule("network.unixUserPath", SpanCategory.UserPath, UnixUserPath(), "login"),
            new RegexRule("network.url", SpanCategory.Url, Url(), Captures:
            [
                new RegexCapture("user", SpanCategory.Credential, "user"),
                new RegexCapture("password", SpanCategory.Secret, "password"),
                new RegexCapture("host", SpanCategory.Fqdn, "host"),

                // Un port d'écoute choisi par l'organisation la désigne presque
                // aussi sûrement qu'un nom de machine ; la liste blanche
                // conserve en revanche les ports de service normalisés, qui
                // disent seulement quel protocole est parlé.
                new RegexCapture("port", SpanCategory.NetworkPort, "port"),
            ]),
        ];
    }
}
