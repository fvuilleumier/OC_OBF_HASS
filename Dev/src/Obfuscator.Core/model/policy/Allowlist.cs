using System.Diagnostics.CodeAnalysis;
using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Valeurs qui ne doivent jamais être masquées.
///
/// Cette liste n'est pas un confort mais la moitié de la valeur de l'outil.
/// Un fichier dont on aurait masqué les masques de sous-réseau, les noms de
/// produits, les comptes techniques et les résolveurs publics est illisible
/// pour un modèle : il ne sait plus s'il regarde du Cisco ou du Fortinet, ni
/// si <c>admin</c> désigne une personne ou un rôle. Ce sont précisément ces
/// valeurs qui portent le contexte utile, et aucune d'elles n'identifie
/// l'organisation.
///
/// La liste est consultée après la détection, catégorie par catégorie : une
/// adresse IP blanchie ne blanchit pas une chaîne identique rencontrée
/// ailleurs dans un autre rôle.
/// </summary>
public sealed class Allowlist
{
    [SuppressMessage(
        "Major Code Smell",
        "S1313:Avoid using hardcoded IP addresses",
        Justification = "Ces adresses sont la liste blanche elle-même : ce sont des résolveurs publics à conserver en clair, pas une configuration de l'application.")]
    private static readonly HashSet<string> WellKnownResolvers = new(StringComparer.Ordinal)
    {
        "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1", "9.9.9.9", "149.112.112.112",
        "208.67.222.222", "208.67.220.220", "4.2.2.2",
    };

    private static readonly HashSet<string> VendorDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "example.com", "example.net", "example.org", "example.invalid",
        "microsoft.com", "login.microsoftonline.com", "graph.microsoft.com", "office.com",
        "office365.com", "windows.com", "windowsupdate.com", "azure.com", "azurewebsites.net",
        "github.com", "gitlab.com", "cisco.com", "fortinet.com", "fortiguard.com",
        "vmware.com", "broadcom.com", "veeam.com", "synology.com", "ubnt.com", "ui.com",
        "ubuntu.com", "debian.org", "redhat.com", "nuget.org", "npmjs.com", "letsencrypt.org",
        "google.com", "gstatic.com", "googleapis.com", "apple.com", "cloudflare.com",
        "pool.ntp.org", "ntp.org", "time.windows.com", "admin.ch", "switch.ch",
    };

    private static readonly HashSet<string> VendorAndProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cisco", "IOS", "NX-OS", "Catalyst", "Meraki", "Fortinet", "FortiGate", "FortiOS",
        "FortiAnalyzer", "FortiManager", "Sophos", "WatchGuard", "pfSense", "OPNsense",
        "Ubiquiti", "UniFi", "Aruba", "HPE", "Juniper", "MikroTik", "Netgear", "Zyxel",
        "VMware", "ESXi", "vSphere", "vCenter", "Proxmox", "Hyper-V", "Citrix", "Nutanix",
        "Veeam", "Acronis", "Synology", "QNAP", "NetApp",
        "Microsoft", "Windows", "Windows Server", "Active Directory", "Entra", "Entra ID",
        "Exchange", "SharePoint", "Teams", "Office", "Microsoft 365", "Azure", "Intune",
        "Linux", "Ubuntu", "Debian", "CentOS", "Rocky", "AlmaLinux", "RHEL", "FreeBSD",
        "Docker", "Kubernetes", "Nginx", "Apache", "IIS", "HAProxy", "Traefik",
        "PostgreSQL", "MySQL", "MariaDB", "Redis", "MongoDB", "SQL Server", "Oracle",
        "Zabbix", "Grafana", "Prometheus", "Nagios", "Graylog", "Elastic", "Kibana",
        "OSPF", "BGP", "EIGRP", "RIP", "VRRP", "HSRP", "STP", "LACP", "LLDP", "VLAN",
        "IPsec", "IKEv2", "OpenVPN", "WireGuard", "RADIUS", "TACACS", "LDAP", "Kerberos",
        "SNMP", "NTP", "DNS", "DHCP", "SMTP", "IMAP", "HTTPS", "SSH", "TLS", "SSL",
    };

    private static readonly HashSet<string> TechnicalAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "administrateur", "root", "system", "localsystem",
        "networkservice", "localservice", "guest", "invité", "anonymous", "nobody",
        "www-data", "daemon", "operator", "service", "svc", "sa", "postgres", "mysql",
        "oracle", "default", "public", "everyone", "authenticated users",
    };

    /// <summary>
    /// Ports dont le numéro dit le protocole et non l'organisation. Les
    /// conserver laisse le modèle comprendre qu'il regarde du HTTPS ou du SSH ;
    /// tout autre port est un choix de l'exploitant, et à ce titre identifiant.
    /// Les ports « alternatifs » courants — 8080, 8443 — n'y figurent pas
    /// volontairement : ils sont assez souvent un choix interne pour mériter
    /// d'être masqués, et il reste possible de les ajouter à la liste blanche
    /// du projet.
    /// </summary>
    private static readonly HashSet<string> WellKnownPorts = new(StringComparer.Ordinal)
    {
        "20", "21", "22", "23", "25", "53", "67", "68", "69", "80", "110", "123", "135",
        "137", "138", "139", "143", "161", "162", "389", "443", "445", "465", "500",
        "514", "515", "587", "636", "993", "995", "1194", "1433", "1521", "1812", "1813",
        "3306", "3389", "5060", "5061", "5432", "5900", "6379", "27017",
    };

    private readonly HashSet<string> _extraTerms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ajoute un terme propre au projet à la liste blanche.
    /// </summary>
    /// <param name="term">Valeur à ne jamais masquer</param>
    public void Add(string term)
    {
        if (!string.IsNullOrWhiteSpace(term))
        {
            _extraTerms.Add(term.Trim());
        }
    }

    /// <summary>
    /// Indique si une valeur figure dans la liste blanche propre au projet —
    /// celle que l'utilisateur tient —, quelle que soit sa nature.
    /// </summary>
    /// <param name="value">Valeur examinée</param>
    /// <returns><c>true</c> si l'utilisateur a demandé de la garder en clair partout</returns>
    public bool IsProjectTerm(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && _extraTerms.Contains(value.Trim());
    }

    /// <summary>
    /// Indique si une valeur doit être conservée telle quelle.
    /// </summary>
    /// <param name="category">Nature de la valeur repérée</param>
    /// <param name="value">Valeur repérée</param>
    /// <returns><c>true</c> si la valeur doit échapper à l'obfuscation</returns>
    public bool Contains(SpanCategory category, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string trimmed = value.Trim();
        if (_extraTerms.Contains(trimmed))
        {
            return true;
        }

        return category switch
        {
            SpanCategory.IpV4 => IsKeptIpV4(trimmed),
            SpanCategory.IpV6 => IsKeptIpV6(trimmed),
            SpanCategory.NetworkPort => WellKnownPorts.Contains(trimmed),
            SpanCategory.DomainName or SpanCategory.Fqdn => IsVendorDomain(trimmed),
            SpanCategory.Email => IsVendorDomain(DomainOf(trimmed)),
            SpanCategory.Credential => TechnicalAccounts.Contains(trimmed),
            SpanCategory.Hostname => TechnicalAccounts.Contains(trimmed) || VendorAndProductNames.Contains(trimmed),
            SpanCategory.Person or SpanCategory.Client or SpanCategory.Site or SpanCategory.Organization =>
                VendorAndProductNames.Contains(trimmed) || TechnicalAccounts.Contains(trimmed),
            _ => false,
        };
    }

    /// <summary>
    /// Détermine si une adresse IPv4 doit être conservée : masque, adresse à
    /// signification protocolaire, plage de documentation ou résolveur public.
    /// </summary>
    /// <param name="value">Adresse repérée</param>
    /// <returns><c>true</c> si l'adresse doit rester lisible</returns>
    private static bool IsKeptIpV4(string value)
    {
        if (WellKnownResolvers.Contains(value))
        {
            return true;
        }

        if (!IpAddressTools.TryParse(value, out uint address))
        {
            return false;
        }

        return IpAddressTools.IsSubnetMaskOrWildcard(address)
            || IpAddressTools.IsSpecialPurpose(address)
            || IpAddressTools.IsDocumentationOrSubstitution(address);
    }

    /// <summary>
    /// Détermine si une adresse IPv6 doit être conservée.
    /// </summary>
    /// <param name="value">Adresse repérée</param>
    /// <returns><c>true</c> si l'adresse doit rester lisible</returns>
    private static bool IsKeptIpV6(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        return normalized is "::1" or "::"
            || normalized.StartsWith("FE80:", StringComparison.Ordinal)
            || normalized.StartsWith("FF0", StringComparison.Ordinal)
            || normalized.StartsWith("2001:DB8:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Détermine si un nom de domaine appartient à un éditeur ou à un service
    /// public, auquel cas il porte du contexte sans rien révéler.
    /// </summary>
    /// <param name="value">Nom de domaine ou nom pleinement qualifié</param>
    /// <returns><c>true</c> si le domaine est public et connu</returns>
    private static bool IsVendorDomain(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (VendorDomains.Contains(value))
        {
            return true;
        }

        // Un sous-domaine d'un domaine d'éditeur l'est aussi : « update.cisco.com »
        // n'apprend rien sur l'organisation.
        return VendorDomains.Any(domain => value.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extrait la partie domaine d'une adresse de courrier.
    /// </summary>
    /// <param name="email">Adresse complète</param>
    /// <returns>Le domaine, ou une chaîne vide si l'adresse est mal formée</returns>
    private static string DomainOf(string email)
    {
        int separator = email.LastIndexOf('@');
        return separator >= 0 && separator < email.Length - 1 ? email[(separator + 1)..] : string.Empty;
    }
}
