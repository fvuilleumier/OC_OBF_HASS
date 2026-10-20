namespace Obfuscator.Core.Model.Engine;

/// <summary>
/// Nature de la donnée repérée dans un fichier source. La catégorie détermine
/// trois choses : la priorité en cas de chevauchement entre deux détecteurs,
/// la famille de jeton utilisée dans le pseudonyme, et la stratégie de
/// remplacement (jeton neutre ou valeur format-préservante).
/// </summary>
public enum SpanCategory
{
    /// <summary>Jeton déjà produit par l'outil : zone gelée, jamais ré-obfusquée.</summary>
    ProtectedToken,

    /// <summary>Mot de passe, clé d'API, jeton d'accès, secret partagé.</summary>
    Secret,

    /// <summary>Bloc PEM : clé privée, certificat, clé publique.</summary>
    KeyBlock,

    /// <summary>Identifiant de connexion (nom de compte associé à un secret).</summary>
    Credential,

    /// <summary>Chaîne de connexion à une base ou à un courtier de messages.</summary>
    ConnectionString,

    /// <summary>Numéro AVS/AHV suisse.</summary>
    SocialSecurityNumber,

    /// <summary>Numéro IBAN.</summary>
    Iban,

    /// <summary>Numéro IDE/UID d'entreprise suisse.</summary>
    BusinessIdentifier,

    /// <summary>Adresse de courrier électronique.</summary>
    Email,

    /// <summary>Nom de personne physique.</summary>
    Person,

    /// <summary>Nom de client ou de partenaire.</summary>
    Client,

    /// <summary>Nom de site, de bâtiment ou de localité.</summary>
    Site,

    /// <summary>Raison sociale, nom de projet ou autre terme propre à l'organisation.</summary>
    Organization,

    /// <summary>URL complète, traitée par décomposition en composants.</summary>
    Url,

    /// <summary>Nom de domaine pleinement qualifié.</summary>
    Fqdn,

    /// <summary>Nom de domaine seul.</summary>
    DomainName,

    /// <summary>Nom d'hôte nu, sans domaine.</summary>
    Hostname,

    /// <summary>Adresse IPv4.</summary>
    IpV4,

    /// <summary>Adresse IPv6.</summary>
    IpV6,

    /// <summary>Adresse MAC.</summary>
    MacAddress,

    /// <summary>Port réseau non standard, porté par une URL.</summary>
    NetworkPort,

    /// <summary>Identifiant global unique (GUID).</summary>
    GlobalIdentifier,

    /// <summary>Identifiant d'enregistrement : la valeur d'une clé nommée ID ou dont le nom contient le mot ID.</summary>
    Identifier,

    /// <summary>Chemin UNC vers un partage réseau.</summary>
    UncPath,

    /// <summary>Composant identifiant d'un chemin local (nom de session utilisateur).</summary>
    UserPath,

    /// <summary>Numéro de téléphone.</summary>
    PhoneNumber,

    /// <summary>Clé de licence ou numéro de série.</summary>
    LicenseKey,

    /// <summary>Coordonnée géographique : latitude, longitude ou altitude d'un lieu.</summary>
    Coordinate,

    /// <summary>Valeur sensible dont la nature exacte n'est pas déterminée.</summary>
    Other,
}

/// <summary>
/// Table des propriétés associées à chaque <see cref="SpanCategory"/> :
/// priorité d'arbitrage et code de famille utilisé dans les jetons.
/// Regroupées ici pour qu'ajouter une catégorie soit une modification en un
/// seul endroit plutôt qu'un <c>switch</c> dispersé dans le moteur.
/// </summary>
public static class SpanCategories
{
    /// <summary>
    /// Priorité accordée aux jetons déjà présents dans le texte. Volontairement
    /// hors échelle : une zone gelée ne doit jamais perdre un arbitrage.
    /// </summary>
    public const int ProtectedTokenPriority = int.MaxValue;

    private static readonly Dictionary<SpanCategory, int> Priorities = new()
    {
        [SpanCategory.ProtectedToken] = ProtectedTokenPriority,
        [SpanCategory.Secret] = 1000,
        [SpanCategory.KeyBlock] = 1000,
        [SpanCategory.Credential] = 950,
        [SpanCategory.ConnectionString] = 900,

        // Les catégories validées par une somme de contrôle sont quasi
        // exemptes de faux positifs : elles passent devant les détecteurs
        // purement lexicaux.
        [SpanCategory.SocialSecurityNumber] = 880,
        [SpanCategory.Iban] = 880,
        [SpanCategory.BusinessIdentifier] = 880,

        [SpanCategory.Email] = 800,
        [SpanCategory.Person] = 780,
        [SpanCategory.Client] = 780,
        [SpanCategory.Site] = 770,
        [SpanCategory.Organization] = 760,
        [SpanCategory.Url] = 700,
        [SpanCategory.Fqdn] = 650,
        [SpanCategory.DomainName] = 640,
        [SpanCategory.Hostname] = 600,
        [SpanCategory.IpV4] = 500,
        [SpanCategory.IpV6] = 500,

        // Une adresse MAC comme « aa:bb:cc:dd:ee:ff » est aussi une adresse
        // IPv6 syntaxiquement valide : la priorité tranche en faveur de la
        // lecture la plus probable dans une configuration réseau.
        [SpanCategory.MacAddress] = 520,

        // Un port est toujours contenu dans l'URL qui le porte : sa priorité
        // n'a d'effet qu'entre sous-captures, jamais face au span englobant.
        [SpanCategory.NetworkPort] = 480,
        [SpanCategory.GlobalIdentifier] = 450,

        // Une coordonnée n'est repérée que sous une clé qui la nomme : elle ne
        // concurrence guère que l'identifiant d'un enregistrement.
        [SpanCategory.Coordinate] = 445,

        // Sous les valeurs qui ont une forme propre — GUID, adresse, courriel —
        // pour qu'un identifiant qui en a une garde le pseudonyme de sa forme.
        [SpanCategory.Identifier] = 440,
        [SpanCategory.UncPath] = 430,
        [SpanCategory.UserPath] = 420,
        [SpanCategory.PhoneNumber] = 400,
        [SpanCategory.LicenseKey] = 390,
        [SpanCategory.Other] = 100,
    };

    private static readonly Dictionary<SpanCategory, string> Families = new()
    {
        [SpanCategory.ProtectedToken] = "TOKEN",
        [SpanCategory.Secret] = "SECRET",
        [SpanCategory.KeyBlock] = "KEY",
        [SpanCategory.Credential] = "CRED",
        [SpanCategory.ConnectionString] = "CONN",
        [SpanCategory.SocialSecurityNumber] = "AVS",
        [SpanCategory.Iban] = "IBAN",
        [SpanCategory.BusinessIdentifier] = "IDE",
        [SpanCategory.Email] = "MAIL",
        [SpanCategory.Person] = "PERSON",
        [SpanCategory.Client] = "CLIENT",
        [SpanCategory.Site] = "SITE",
        [SpanCategory.Organization] = "ORG",
        [SpanCategory.Url] = "URL",
        [SpanCategory.Fqdn] = "FQDN",
        [SpanCategory.DomainName] = "DOM",
        [SpanCategory.Hostname] = "HOST",
        [SpanCategory.IpV4] = "IP",
        [SpanCategory.IpV6] = "IPV6",
        [SpanCategory.MacAddress] = "MAC",
        [SpanCategory.NetworkPort] = "PORT",
        [SpanCategory.GlobalIdentifier] = "GUID",
        [SpanCategory.Identifier] = "ID",
        [SpanCategory.UncPath] = "UNC",
        [SpanCategory.UserPath] = "USER",
        [SpanCategory.PhoneNumber] = "PHONE",
        [SpanCategory.LicenseKey] = "LIC",
        [SpanCategory.Coordinate] = "GEO",
        [SpanCategory.Other] = "VAL",
    };

    /// <summary>
    /// Retourne la priorité d'arbitrage par défaut d'une catégorie.
    /// </summary>
    /// <param name="category">Catégorie dont on veut la priorité</param>
    /// <returns>Priorité, d'autant plus élevée que la catégorie est sensible</returns>
    public static int DefaultPriority(this SpanCategory category)
    {
        return Priorities.TryGetValue(category, out int priority) ? priority : 0;
    }

    /// <summary>
    /// Retourne le code de famille utilisé dans les jetons de cette catégorie.
    /// </summary>
    /// <param name="category">Catégorie dont on veut le code de famille</param>
    /// <returns>Code court en majuscules, par exemple <c>HOST</c> ou <c>SECRET</c></returns>
    public static string FamilyCode(this SpanCategory category)
    {
        return Families.TryGetValue(category, out string? family) ? family : "VAL";
    }
}
