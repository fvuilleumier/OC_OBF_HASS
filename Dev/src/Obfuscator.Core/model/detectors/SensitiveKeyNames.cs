using System.Text;
using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Detectors;

/// <summary>
/// Reconnaît, à son nom, une clé dont la valeur est un secret, un compte, un
/// port d'écoute ou un identifiant.
///
/// Un nom de clé est rarement un mot seul : <c>wsPassword</c>,
/// <c>DB_PASSWORD</c>, <c>mfUsernameHub</c>, <c>spring.datasource.password</c>,
/// <c>MotDePasse</c>. Le nom est donc découpé en mots — casse chameau,
/// soulignés, tirets, points, espaces — et c'est la présence d'un mot sensible
/// parmi eux qui décide, en français, en anglais ou en allemand. Un nom en
/// majuscules sans séparateur (<c>WSPASSWORD</c>) est reconnu aux mots les plus
/// distinctifs, qu'il contient.
///
/// Trois garde-fous évitent de masquer ce qui parle d'un secret sans en être
/// un. Un mot descriptif placé après le terme sensible — <c>passwordPolicy</c>,
/// <c>tokenUrl</c>, <c>loginAttempts</c>, <c>userAgent</c> — désigne une
/// propriété du secret, pas le secret. Un quantificateur placé en tête —
/// <c>maxTokens</c>, <c>minPasswordLength</c> — désigne une mesure. Un compte
/// en complément — <c>name_by_user</c>, le nom qu'un utilisateur a donné à un
/// appareil — n'est pas la valeur de la clé. Pour le reste, le doute profite au
/// masquage : un faux positif n'est qu'un jeton de plus dans le fichier, un
/// faux négatif est une fuite.
///
/// Une clé qui s'appelle simplement <c>id</c> — <c>ID</c>, <c>Id</c>,
/// <c>_id</c> — désigne l'enregistrement lui-même : sa valeur est masquée comme
/// identifiant, dans une famille à part que l'on peut décocher. Un nom composé
/// qui contient le mot <c>id</c> — <c>unique_id</c>, <c>config_entry_id</c>,
/// <c>device-id</c>, <c>tokenId</c> — est lu de même, sauf s'il désigne d'abord
/// un compte (<c>userId</c>) ou un secret (<c>id_token</c>), ou si un
/// descripteur en fait une propriété (<c>id_prefix</c>). Seul le mot entier
/// compte : <c>idee</c>, <c>paid</c> ou <c>uuid</c> ne sont pas des « id ».
///
/// Une clé qui nomme une coordonnée — <c>latitude</c>, <c>longitude</c>,
/// <c>elevation</c>, seules ou composées — porte la position d'un lieu, celle
/// d'un domicile dans une configuration domotique : sa valeur, si c'est un
/// nombre, est masquée comme coordonnée.
/// </summary>
public static partial class SensitiveKeyNames
{
    /// <summary>Nombre de mots au-delà duquel un « nom » est une phrase, pas une clé.</summary>
    private const int MaximumWords = 12;

    private const int TimeoutMilliseconds = 1000;

    /// <summary>Mots qui désignent à eux seuls un secret.</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "password", "passwords", "passwd", "pwd", "pass", "passphrase", "passcode", "passkey",
        "secret", "secrets", "token", "tokens", "credential", "credentials", "psk", "community",
        "apikey", "apikeys", "accesskey", "secretkey", "privatekey", "authtoken", "sastoken",
        "connectionstring", "connectionstrings",
        "mdp", "motdepasse", "passwort", "kennwort", "geheimnis",
    };

    /// <summary>Suites de mots qui désignent ensemble un secret.</summary>
    private static readonly string[][] SecretPhrases =
    [
        ["api", "key"], ["api", "keys"], ["access", "key"], ["private", "key"], ["shared", "key"],
        ["app", "key"], ["master", "key"], ["encryption", "key"], ["signing", "key"],
        ["subscription", "key"], ["account", "key"], ["connection", "string"], ["connection", "strings"],
        ["mot", "de", "passe"], ["mot", "passe"], ["cle", "api"], ["cle", "privee"], ["cle", "secrete"],
    ];

    /// <summary>Mots qui désignent à eux seuls un compte.</summary>
    private static readonly HashSet<string> CredentialWords = new(StringComparer.Ordinal)
    {
        "user", "users", "username", "usernames", "userid", "uid", "login", "logins", "logon",
        "account", "accounts", "utilisateur", "utilisateurs", "identifiant", "identifiants",
        "compte", "comptes", "benutzer", "benutzername", "anmeldename",
    };

    /// <summary>Suites de mots qui désignent ensemble un compte.</summary>
    private static readonly string[][] CredentialPhrases =
    [
        ["bind", "dn"],
    ];

    /// <summary>
    /// Mots qui désignent un port d'écoute : <c>mfPort</c>, <c>frontend.port</c>,
    /// <c>tcp-listen:8485</c>. La valeur n'est retenue que si c'est un numéro
    /// de port.
    /// </summary>
    private static readonly HashSet<string> PortWords = new(StringComparer.Ordinal)
    {
        "port", "ports", "listen",
    };

    /// <summary>
    /// Mots qui désignent une coordonnée géographique : <c>latitude</c>,
    /// <c>home_longitude</c>, <c>elevation</c>. La valeur n'est retenue que si
    /// c'est un nombre. « long » et « alt » n'en font pas partie : le premier
    /// est un type de données, le second le texte de remplacement d'une image.
    /// </summary>
    private static readonly HashSet<string> CoordinateWords = new(StringComparer.Ordinal)
    {
        "latitude", "longitude", "lat", "lon", "lng", "elevation", "altitude",
    };

    /// <summary>
    /// Mot qui désigne un identifiant d'enregistrement, seul ou dans un nom
    /// composé : <c>id</c>, <c>unique_id</c>, <c>config-entry-id</c>, <c>deviceId</c>.
    /// </summary>
    private static readonly HashSet<string> IdentifierWords = new(StringComparer.Ordinal)
    {
        "id",
    };

    /// <summary>Aucun descripteur propre : seuls les descripteurs généraux s'appliquent.</summary>
    private static readonly HashSet<string> NoDescriptors = new(StringComparer.Ordinal);

    /// <summary>
    /// Qualificatifs qui font d'une clé ou d'un jeton une donnée publique :
    /// <c>publicKeyToken</c> identifie un assembly signé, il n'a rien de secret.
    /// </summary>
    private static readonly HashSet<string> PublicQualifiers = new(StringComparer.Ordinal)
    {
        "public", "publique", "publiques", "publics",
    };

    /// <summary>
    /// Mots de secret assez distinctifs pour être reconnus à l'intérieur d'un
    /// nom sans séparateur : <c>WSPASSWORD</c>, <c>dbpasswd</c>.
    /// </summary>
    private static readonly string[] DistinctiveSecrets =
    [
        "password", "passwd", "passphrase", "motdepasse", "passwort", "kennwort", "credential",
        "connectionstring", "apikey", "privatekey", "secretkey", "accesskey",
    ];

    /// <summary>Mots de compte assez distinctifs pour être reconnus à l'intérieur d'un nom.</summary>
    private static readonly string[] DistinctiveCredentials =
    [
        "username", "utilisateur", "identifiant", "benutzername", "loginname",
    ];

    /// <summary>
    /// Mots qui, placés après le terme sensible, en décrivent une propriété :
    /// son adresse, sa règle, sa durée, son format, son affichage.
    /// </summary>
    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal)
    {
        "url", "uri", "urls", "endpoint", "endpoints", "path", "paths", "file", "files", "dir", "directory", "folder",
        "policy", "policies", "rule", "rules", "regex", "pattern", "length", "len", "size", "min", "max",
        "minimum", "maximum", "count", "limit", "limits", "quota", "version",
        "timeout", "ttl", "lifetime", "expiry", "expiration", "expires", "expire", "expired", "validity",
        "duration", "age", "interval", "period", "delay",
        "enabled", "enable", "disabled", "disable", "required", "require", "use", "allow", "allowed",
        "reset", "change", "changed", "history", "strength", "complexity",
        "type", "types", "mode", "format", "algorithm", "algo", "provider", "issuer", "audience",
        "scope", "scopes", "claim", "claims", "grant", "header", "headers", "field", "fields", "column",
        "param", "parameter", "prefix", "suffix", "label", "hint", "prompt", "placeholder", "description",
        "title", "message", "template", "attempts", "attempt", "retries", "retry", "lockout",
        "validation", "validator", "rate", "ratio", "through", "thru", "fail", "failed", "failure",
        "cache", "store", "storage", "source", "location", "manager", "ref", "reference", "list", "table",
        "filter", "class", "level", "icon", "color", "style", "visible", "visibility", "show", "hide",
        "hidden", "input", "button", "link", "page", "screen", "form", "dialog",
        "longueur", "politique", "regle", "regles", "duree", "delai", "nombre", "taille", "champ",
        "libelle", "chemin", "fichier", "dossier",
    };

    /// <summary>
    /// Descripteurs propres aux secrets : le nom ou l'identifiant d'un secret
    /// le désigne sans le contenir — <c>secretName</c>, <c>tokenId</c> —, alors
    /// que le nom d'un utilisateur est l'utilisateur lui-même.
    /// </summary>
    private static readonly HashSet<string> SecretDescriptors = new(StringComparer.Ordinal)
    {
        "name", "names", "id", "ids",
    };

    /// <summary>
    /// Descripteurs propres aux comptes : ce qui se rapporte à l'utilisateur
    /// sans l'identifier — son navigateur, ses réglages, son rôle.
    /// </summary>
    private static readonly HashSet<string> CredentialDescriptors = new(StringComparer.Ordinal)
    {
        "agent", "interface", "profile", "home", "data", "settings", "preferences", "config",
        "configuration", "role", "roles", "group", "groups", "experience", "session", "sessions",
        "avatar", "image", "photo", "picture", "locale", "language", "lang", "timezone", "theme",
        "select", "selector", "status", "state", "flow", "guide", "manual", "journey", "activity",
        "feedback", "query", "search", "consent", "terms",
    };

    /// <summary>Quantificateurs qui, en tête du nom, en font une mesure.</summary>
    private static readonly HashSet<string> Quantifiers = new(StringComparer.Ordinal)
    {
        "max", "min", "maximum", "minimum", "num", "number", "nb", "nombre", "count", "total",
    };

    /// <summary>
    /// Prépositions qui font du compte un complément : <c>name_by_user</c> est
    /// le nom donné par l'utilisateur, <c>limit_per_user</c> une limite. Le
    /// génitif — <c>name_of_user</c>, « nom d'utilisateur » — n'en fait pas
    /// partie : il désigne bien l'utilisateur.
    /// </summary>
    private static readonly HashSet<string> AgentivePrepositions = new(StringComparer.Ordinal)
    {
        "by", "per", "for", "from", "to", "via", "par", "pour",
    };

    /// <summary>Valeurs de drapeau, jamais des secrets.</summary>
    private static readonly HashSet<string> FlagValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "none", "nil", "~",
    };

    /// <summary>Adresse de courrier, pour lui garder la forme d'une adresse une fois pseudonymisée.</summary>
    [GeneratedRegex(@"^[^@\s""'<>]+@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex EmailAddress();

    /// <summary>Indicateur de bloc ou alias YAML : la valeur est ailleurs.</summary>
    [GeneratedRegex(@"^(?:[|>][+\-]?\d?|\*[\w\-]+)$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex YamlIndirection();

    /// <summary>Numéro de port, sans signe ni zéro de tête.</summary>
    [GeneratedRegex(@"^[1-9]\d{0,4}$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex PortNumber();

    /// <summary>Identifiant global, accolades comprises ou non.</summary>
    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex GuidValue();

    /// <summary>Nombre signé ou décimal : une mesure, pas un identifiant.</summary>
    [GeneratedRegex(@"^(?:[+\-]\d+(?:\.\d+)?|\d+\.\d+)$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex SignedOrDecimal();

    /// <summary>Coordonnée : un nombre, signé ou non, entier ou décimal.</summary>
    [GeneratedRegex(@"^[+\-]?\d{1,5}(?:\.\d{1,15})?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex CoordinateNumber();

    /// <summary>
    /// Détermine si un nom de clé désigne un secret, un compte, un port ou un
    /// identifiant.
    /// </summary>
    /// <param name="keyName">Nom de la clé, tel qu'il figure dans le fichier</param>
    /// <returns><see cref="SpanCategory.Secret"/>, <see cref="SpanCategory.Credential"/>, <see cref="SpanCategory.NetworkPort"/>, <see cref="SpanCategory.Identifier"/>, <see cref="SpanCategory.Coordinate"/>, ou <c>null</c> si la clé n'est pas sensible</returns>
    public static SpanCategory? Classify(string keyName)
    {
        ArgumentNullException.ThrowIfNull(keyName);

        List<string> words = Words(keyName);
        if (words.Count is 0 or > MaximumWords)
        {
            return null;
        }

        // Une clé qui s'appelle « id » désigne l'enregistrement : sa valeur
        // l'identifie, quelle qu'elle soit.
        if (words is ["id"])
        {
            return SpanCategory.Identifier;
        }

        (int Start, int End) secret = LastMatch(words, SecretWords, SecretPhrases, DistinctiveSecrets);
        (int Start, int End) credential = LastMatch(words, CredentialWords, CredentialPhrases, DistinctiveCredentials);

        // Une préposition ne fait du compte un complément que dans un nom de
        // clé d'un seul tenant (« name_by_user ») : dans une phrase —
        // « password for user=bob » —, c'est le dernier mot qui porte la valeur.
        bool identifier = !keyName.Contains(' ', StringComparison.Ordinal);
        bool secretHolds = secret.End >= 0
            && Holds(words, secret, SecretDescriptors, agentive: false)
            && !words.Take(secret.Start).Any(PublicQualifiers.Contains);
        bool credentialHolds = credential.End >= 0 && Holds(words, credential, CredentialDescriptors, agentive: identifier);

        // Le terme le plus proche de la fin est celui que le nom désigne :
        // « userPassword » est un mot de passe. À égalité, le secret l'emporte.
        if (secretHolds && (!credentialHolds || secret.End >= credential.End))
        {
            return SpanCategory.Secret;
        }

        if (credentialHolds)
        {
            return SpanCategory.Credential;
        }

        // Un port ne passe qu'après : « passwordPort » n'existe pas, mais une
        // clé qui nommerait à la fois un compte et un port désigne le compte.
        (int Start, int End) port = LastMatch(words, PortWords, [], []);
        if (port.End >= 0 && Holds(words, port, SecretDescriptors, agentive: identifier))
        {
            return SpanCategory.NetworkPort;
        }

        // Une coordonnée vient en dernier : c'est la lecture la moins sûre
        // d'un nom de clé, et une clé qui nomme aussi un secret désigne le secret.
        (int Start, int End) coordinate = LastMatch(words, CoordinateWords, [], []);
        if (coordinate.End >= 0 && Holds(words, coordinate, SecretDescriptors, agentive: false))
        {
            return SpanCategory.Coordinate;
        }

        // Un nom composé qui contient le mot « id » — « unique_id »,
        // « config_entry_id », « deviceId » — désigne un identifiant, à moins
        // qu'une lecture plus précise ne l'ait déjà réclamé (« userId » est un
        // compte, « id_token » un secret). Le découpage en mots garantit que
        // seul le mot entier compte : « idee », « paid » ou « uuid » ne sont
        // pas des « id ». Un descripteur placé après — « id_prefix »,
        // « idLength » — en fait une propriété, pas la valeur.
        (int Start, int End) record = LastMatch(words, IdentifierWords, [], []);
        return record.End >= 0 && Holds(words, record, NoDescriptors, agentive: false)
            ? SpanCategory.Identifier
            : null;
    }

    /// <summary>
    /// Détermine la catégorie d'une paire clé/valeur, en écartant les valeurs
    /// qui ne peuvent pas être un secret : référence à une variable, drapeau,
    /// renvoi YAML. Une adresse de courrier désignant un compte reste une
    /// adresse : elle reçoit un pseudonyme qui en garde la forme.
    /// </summary>
    /// <param name="keyName">Nom de la clé</param>
    /// <param name="value">Valeur associée</param>
    /// <returns>La catégorie de la valeur à masquer, ou <c>null</c> s'il n'y a rien à masquer</returns>
    public static SpanCategory? ClassifyPair(string keyName, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        SpanCategory? category = Classify(keyName);
        if (category is null || !CanHoldSecret(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return category switch
        {
            // Une clé de port dont la valeur n'est pas un numéro — un port série
            // « /dev/ttyUSB0 », une URL qui porte son propre port — n'est pas
            // un port réseau à masquer ici.
            SpanCategory.NetworkPort => IsPort(trimmed) ? category : null,
            SpanCategory.Credential when EmailAddress().IsMatch(trimmed) => SpanCategory.Email,
            SpanCategory.Identifier => IdentifierCategory(trimmed),

            // Une coordonnée est un nombre : « latitude: home » renvoie à autre
            // chose, que les autres règles jugeront.
            SpanCategory.Coordinate => IsCoordinate(trimmed) ? category : null,
            _ => category,
        };
    }

    /// <summary>
    /// Indique si une valeur a la forme d'une coordonnée : un nombre, signé ou
    /// non, entier ou décimal.
    /// </summary>
    /// <param name="value">Valeur examinée, sans les espaces qui l'entourent</param>
    /// <returns><c>true</c> pour un nombre</returns>
    public static bool IsCoordinate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CoordinateNumber().IsMatch(value);
    }

    /// <summary>
    /// Détermine la catégorie de la valeur d'une clé « id ».
    /// </summary>
    /// <param name="value">Valeur, sans les espaces qui l'entourent</param>
    /// <returns>La catégorie de la valeur à masquer, ou <c>null</c> si c'est à un autre détecteur d'en décider</returns>
    private static SpanCategory? IdentifierCategory(string value)
    {
        // Une valeur qui a une forme propre la garde. Un GUID — accolades
        // comprises — et une adresse sont laissés à leur détecteur, qui leur
        // donne un pseudonyme de leur forme ; un courriel reçoit un pseudonyme
        // de courriel.
        if (GuidValue().IsMatch(value) || IpAddressTools.TryParse(value, out _))
        {
            return null;
        }

        if (EmailAddress().IsMatch(value))
        {
            return SpanCategory.Email;
        }

        // Un nombre signé ou décimal n'identifie rien, et un jeton le rendrait
        // invalide dans un document JSON ou YAML.
        return SignedOrDecimal().IsMatch(value) ? null : SpanCategory.Identifier;
    }

    /// <summary>
    /// Indique si une valeur est un numéro de port valide.
    /// </summary>
    /// <param name="value">Valeur examinée</param>
    /// <returns><c>true</c> entre 1 et 65535</returns>
    public static bool IsPort(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PortNumber().IsMatch(value) && int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) <= 65535;
    }

    /// <summary>
    /// Indique si un terme sensible désigne bien la valeur de la clé, et non
    /// l'une de ses propriétés ou de ses mesures.
    /// </summary>
    /// <param name="words">Mots du nom de clé</param>
    /// <param name="match">Positions du terme</param>
    /// <param name="specific">Descripteurs propres à la famille du terme</param>
    /// <param name="agentive">Vrai pour écarter un terme placé en complément, après une préposition</param>
    /// <returns><c>true</c> si la valeur est celle que le terme désigne</returns>
    private static bool Holds(List<string> words, (int Start, int End) match, HashSet<string> specific, bool agentive)
    {
        for (int index = match.End + 1; index < words.Count; index++)
        {
            if (Descriptors.Contains(words[index]) || specific.Contains(words[index]))
            {
                return false;
            }
        }

        if (match.Start > 0 && Quantifiers.Contains(words[0]))
        {
            return false;
        }

        // « name_by_user » : l'utilisateur est le complément du nom, pas sa
        // valeur. « created_by_user_id », lui, désigne bien un identifiant.
        return !(agentive
            && match.Start > 0
            && match.End == words.Count - 1
            && AgentivePrepositions.Contains(words[match.Start - 1]));
    }

    /// <summary>
    /// Indique si une valeur peut contenir un secret.
    /// </summary>
    /// <param name="value">Valeur associée à une clé sensible</param>
    /// <returns><c>false</c> pour une marque de substitution, un drapeau ou un renvoi</returns>
    private static bool CanHoldSecret(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length > 0
            && !FlagValues.Contains(trimmed)
            && !YamlIndirection().IsMatch(trimmed)
            && !PlaceholderValues.IsPlaceholder(trimmed);
    }

    /// <summary>
    /// Découpe un nom de clé en mots en minuscules, sans accents.
    /// </summary>
    /// <param name="keyName">Nom de la clé</param>
    /// <returns>Les mots, dans l'ordre</returns>
    private static List<string> Words(string keyName)
    {
        List<string> words = [];
        StringBuilder current = new();

        for (int index = 0; index < keyName.Length; index++)
        {
            char character = keyName[index];
            if (!char.IsLetterOrDigit(character))
            {
                Flush(words, current);
                continue;
            }

            if (current.Length > 0 && StartsWord(keyName, index))
            {
                Flush(words, current);
            }

            current.Append(char.ToLowerInvariant(TextFolding.FoldCharacter(character)));
        }

        Flush(words, current);
        return words;
    }

    /// <summary>
    /// Indique si un caractère commence un nouveau mot à l'intérieur d'une
    /// suite de lettres et de chiffres : minuscule suivie d'une majuscule
    /// (<c>wsPassword</c>), fin d'un sigle (<c>APIKey</c>), passage entre
    /// lettres et chiffres (<c>password2</c>).
    /// </summary>
    /// <param name="name">Nom découpé</param>
    /// <param name="index">Position du caractère</param>
    /// <returns><c>true</c> si un mot commence à cette position</returns>
    private static bool StartsWord(string name, int index)
    {
        char previous = name[index - 1];
        char character = name[index];

        return (char.IsLower(previous) && char.IsUpper(character))
            || (char.IsUpper(previous) && char.IsUpper(character) && index + 1 < name.Length && char.IsLower(name[index + 1]))
            || (char.IsLetter(previous) != char.IsLetter(character));
    }

    /// <summary>
    /// Termine le mot en cours.
    /// </summary>
    /// <param name="words">Mots déjà découpés</param>
    /// <param name="current">Mot en cours</param>
    private static void Flush(List<string> words, StringBuilder current)
    {
        if (current.Length > 0)
        {
            words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>
    /// Trouve le dernier terme sensible d'une suite de mots.
    /// </summary>
    /// <param name="words">Mots du nom de clé</param>
    /// <param name="single">Mots sensibles à eux seuls</param>
    /// <param name="phrases">Suites de mots sensibles ensemble</param>
    /// <param name="distinctive">Mots reconnus à l'intérieur d'un mot plus long</param>
    /// <returns>Les positions du premier et du dernier mot du terme, ou (-1, -1)</returns>
    private static (int Start, int End) LastMatch(
        List<string> words,
        HashSet<string> single,
        string[][] phrases,
        string[] distinctive)
    {
        (int Start, int End) found = (-1, -1);

        for (int index = 0; index < words.Count; index++)
        {
            string word = words[index];
            if (single.Contains(word) || distinctive.Any(term => word.Length > term.Length && word.Contains(term, StringComparison.Ordinal)))
            {
                found = found.End > index ? found : (index, index);
            }

            foreach (string[] phrase in phrases)
            {
                int end = index + phrase.Length - 1;
                if (end < words.Count && end >= found.End && PhraseAt(words, index, phrase))
                {
                    found = (index, end);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Indique si une suite de mots commence à une position donnée.
    /// </summary>
    /// <param name="words">Mots du nom de clé</param>
    /// <param name="start">Position examinée</param>
    /// <param name="phrase">Suite recherchée</param>
    /// <returns><c>true</c> si la suite figure à cette position</returns>
    private static bool PhraseAt(List<string> words, int start, string[] phrase)
    {
        for (int offset = 0; offset < phrase.Length; offset++)
        {
            if (!string.Equals(words[start + offset], phrase[offset], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
