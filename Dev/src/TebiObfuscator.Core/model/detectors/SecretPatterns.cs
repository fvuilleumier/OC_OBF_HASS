using System.Text.RegularExpressions;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Expressions régulières des secrets reconnaissables à leur préfixe.
///
/// Ces règles sont les plus rentables de tout l'outil : un jeton commençant par
/// <c>AKIA</c> ou <c>ghp_</c> est un secret, sans ambiguïté possible, donc leur
/// taux de faux positifs est nul et leur valeur de protection immédiate.
/// Elles sont générées à la compilation et dotées d'un délai maximal.
/// </summary>
internal static partial class SecretPatterns
{
    /// <summary>Délai maximal accordé à une correspondance, en millisecondes.</summary>
    private const int TimeoutMilliseconds = 2000;

    /// <summary>Clé d'accès AWS.</summary>
    [GeneratedRegex(@"\b(?:AKIA|ASIA|AIDA|AROA)[0-9A-Z]{16}\b", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex AwsAccessKey();

    /// <summary>Jeton GitHub (personnel, OAuth, application, rafraîchissement).</summary>
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,255}\b", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex GitHubToken();

    /// <summary>Jeton d'accès personnel GitLab.</summary>
    [GeneratedRegex(@"\bglpat-[A-Za-z0-9_\-]{20,}", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex GitLabToken();

    /// <summary>Jeton Slack.</summary>
    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex SlackToken();

    /// <summary>Clé Stripe, en environnement de test comme de production.</summary>
    [GeneratedRegex(@"\b[sprk]k_(?:live|test)_[A-Za-z0-9]{10,}\b", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex StripeKey();

    /// <summary>Clé d'API Google.</summary>
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex GoogleApiKey();

    /// <summary>Jeton npm.</summary>
    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex NpmToken();

    /// <summary>Jeton web JSON, reconnaissable à son en-tête encodé.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex JsonWebToken();

    /// <summary>Clé publique SSH, porteuse d'identité même si elle n'est pas secrète.</summary>
    [GeneratedRegex(@"\bssh-(?:rsa|dss|ed25519)\s+[A-Za-z0-9+/]{100,}={0,3}", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex SshPublicKey();

    /// <summary>Valeur d'une en-tête d'autorisation HTTP, porteuse du secret.</summary>
    [GeneratedRegex(@"(?i)\b(?:Basic|Bearer)\s+(?<value>[A-Za-z0-9+/=._\-]{16,})", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex AuthorizationHeader();

    /// <summary>Signature d'accès partagé Azure.</summary>
    [GeneratedRegex(@"(?i)\b(?:sig|sas|signature)=(?<value>[A-Za-z0-9%+/=]{20,})", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex SharedAccessSignature();

    /// <summary>Empreinte de mot de passe au format Unix (MD5, SHA-256, SHA-512, bcrypt, scrypt).</summary>
    [GeneratedRegex(@"\$(?:1|5|6|9|2[aby])\$[A-Za-z0-9./$+=]{10,}", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex UnixPasswordHash();

    /// <summary>
    /// Mot de passe Cisco de type 7 : chiffrement réversible, donc secret réel
    /// malgré son apparence de condensat.
    /// </summary>
    [GeneratedRegex(@"(?i)\b(?:password|key)\s+7\s+(?<value>[0-9A-F]{4,})", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex CiscoType7Password();

    /// <summary>Communauté SNMP, équivalente à un mot de passe en lecture ou écriture.</summary>
    [GeneratedRegex(@"(?i)\bsnmp-server\s+community\s+(?<value>\S+)", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex SnmpCommunity();

    /// <summary>Secret partagé IPsec déclaré dans une configuration Cisco.</summary>
    [GeneratedRegex(@"(?i)\bcrypto\s+isakmp\s+key\s+(?<value>\S+)", RegexOptions.None, TimeoutMilliseconds)]
    public static partial Regex IsakmpPreSharedKey();
}
