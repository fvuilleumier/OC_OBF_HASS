using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Reporting;

/// <summary>
/// Traduit les catégories techniques en libellés compréhensibles.
///
/// Les noms d'énumération — <c>Fqdn</c>, <c>UncPath</c>, <c>GlobalIdentifier</c>
/// — sont justes pour le code et opaques pour qui décide quoi masquer. Comme
/// c'est précisément cette décision que l'outil demande à l'utilisateur, la
/// table vit ici, partagée par les deux interfaces et par la légende, plutôt
/// que recopiée dans chacune.
/// </summary>
public static class CategoryLabels
{
    private static readonly Dictionary<SpanCategory, string> Labels = new()
    {
        [SpanCategory.ProtectedToken] = "Pseudonymes déjà en place",
        [SpanCategory.Secret] = "Secrets et mots de passe",
        [SpanCategory.KeyBlock] = "Clés et certificats",
        [SpanCategory.Credential] = "Comptes de connexion",
        [SpanCategory.ConnectionString] = "Chaînes de connexion",
        [SpanCategory.SocialSecurityNumber] = "Numéros AVS",
        [SpanCategory.Iban] = "Comptes bancaires (IBAN)",
        [SpanCategory.BusinessIdentifier] = "Numéros d'entreprise (IDE)",
        [SpanCategory.Email] = "Adresses de courrier",
        [SpanCategory.Person] = "Personnes",
        [SpanCategory.Client] = "Clients et partenaires",
        [SpanCategory.Site] = "Sites et lieux",
        [SpanCategory.Organization] = "Noms internes (société, projets)",
        [SpanCategory.Url] = "Adresses web internes",
        [SpanCategory.Fqdn] = "Machines (nom qualifié)",
        [SpanCategory.DomainName] = "Domaines internes",
        [SpanCategory.Hostname] = "Noms d'hôtes",
        [SpanCategory.IpV4] = "Adresses IPv4",
        [SpanCategory.IpV6] = "Adresses IPv6",
        [SpanCategory.MacAddress] = "Adresses MAC",
        [SpanCategory.NetworkPort] = "Ports non standard",
        [SpanCategory.GlobalIdentifier] = "Identifiants GUID",
        [SpanCategory.Identifier] = "Identifiants (clés ID)",
        [SpanCategory.UncPath] = "Partages réseau",
        [SpanCategory.UserPath] = "Noms de session Windows",
        [SpanCategory.PhoneNumber] = "Numéros de téléphone",
        [SpanCategory.LicenseKey] = "Licences et numéros de série",
        [SpanCategory.Coordinate] = "Coordonnées géographiques",
        [SpanCategory.Other] = "Autres valeurs sensibles",
    };

    private static readonly Dictionary<SpanCategory, string> Descriptions = new()
    {
        [SpanCategory.ProtectedToken] = "un pseudonyme produit par l'outil, laissé tel quel",
        [SpanCategory.Secret] = "un secret : mot de passe, clé d'API, jeton d'accès",
        [SpanCategory.KeyBlock] = "une clé ou un certificat ; l'enveloppe est authentique, le corps ne l'est pas",
        [SpanCategory.Credential] = "un identifiant de connexion nominatif",
        [SpanCategory.ConnectionString] = "une chaîne de connexion",
        [SpanCategory.SocialSecurityNumber] = "un numéro d'assurance sociale",
        [SpanCategory.Iban] = "un numéro de compte bancaire",
        [SpanCategory.BusinessIdentifier] = "un numéro d'identification d'entreprise",
        [SpanCategory.Email] = "une adresse de courrier électronique",
        [SpanCategory.Person] = "une personne physique",
        [SpanCategory.Client] = "un client ou un partenaire",
        [SpanCategory.Site] = "un site, un bâtiment ou une localité",
        [SpanCategory.Organization] = "une raison sociale, un nom de projet ou un terme interne",
        [SpanCategory.Url] = "une adresse web interne",
        [SpanCategory.Fqdn] = "un nom de machine pleinement qualifié",
        [SpanCategory.DomainName] = "un nom de domaine interne",
        [SpanCategory.Hostname] = "un nom d'hôte",
        [SpanCategory.IpV4] = "une adresse IPv4, ou son début quand c'est le réseau qui est masqué ; une adresse privée reste une adresse privée de la même plage, une adresse publique devient une adresse de la plage d'essai 198.18.0.0/15, et l'appartenance au même sous-réseau comme la partie hôte sont préservées",
        [SpanCategory.IpV6] = "une adresse IPv6",
        [SpanCategory.MacAddress] = "une adresse MAC",
        [SpanCategory.NetworkPort] = "un port d'écoute non standard ; les ports de service normalisés sont conservés",
        [SpanCategory.GlobalIdentifier] = "un identifiant global",
        [SpanCategory.Identifier] = "un identifiant d'enregistrement, la valeur d'une clé nommée ID ou dont le nom contient le mot ID (unique_id, config_entry_id) ; un numéro reste un numéro",
        [SpanCategory.UncPath] = "un partage réseau",
        [SpanCategory.UserPath] = "un nom de session dans un chemin de fichier",
        [SpanCategory.PhoneNumber] = "un numéro de téléphone",
        [SpanCategory.LicenseKey] = "une clé de licence ou un numéro de série",
        [SpanCategory.Coordinate] = "une coordonnée géographique — latitude, longitude ou altitude ; le nombre est fictif, de même forme, et ne garde aucune proximité avec le lieu réel",
        [SpanCategory.Other] = "une valeur sensible non classée",
    };

    /// <summary>
    /// Retourne le libellé lisible d'une catégorie.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <returns>Le libellé destiné à l'utilisateur</returns>
    public static string Label(SpanCategory category)
    {
        return Labels.TryGetValue(category, out string? label) ? label : category.ToString();
    }

    /// <summary>
    /// Retourne la description de ce que représente un pseudonyme de cette
    /// catégorie.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <returns>La description, formulée pour être lue par un modèle comme par une personne</returns>
    public static string Description(SpanCategory category)
    {
        return Descriptions.TryGetValue(category, out string? text) ? text : "une valeur sensible";
    }

    /// <summary>
    /// Catégories qu'il est pertinent de proposer lorsqu'on ajoute un terme à
    /// la main. Celles que le moteur déduit seul — sommes de contrôle, blocs de
    /// clés — n'y figurent pas : un terme saisi au clavier désigne une entité
    /// nommée, ou une adresse qu'on veut changer en une autre.
    /// </summary>
    /// <returns>Les catégories proposées, dans l'ordre d'usage</returns>
    public static IReadOnlyList<SpanCategory> ManualCategories()
    {
        return
        [
            SpanCategory.Organization,
            SpanCategory.Client,
            SpanCategory.Person,
            SpanCategory.Site,
            SpanCategory.Hostname,
            SpanCategory.IpV4,
            SpanCategory.DomainName,
            SpanCategory.Secret,
            SpanCategory.Credential,
            SpanCategory.Other,
        ];
    }
}
