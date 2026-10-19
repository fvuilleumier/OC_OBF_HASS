using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Détecte les secrets applicatifs reconnaissables à leur forme : un
/// identifiant commençant par <c>AKIA</c> ou <c>ghp_</c> est un secret, point
/// final, et ces règles ne produisent pratiquement aucun faux positif. S'y
/// ajoutent les grammaires de ligne des équipements réseau (mot de passe
/// Cisco de type 7, communauté SNMP, clé IPsec).
///
/// Les secrets reconnus au nom de leur clé — <c>password: …</c>,
/// <c>&lt;add key="wsPassword" value="…" /&gt;</c> — relèvent de
/// <see cref="SensitiveKeyDetector"/>.
/// </summary>
public sealed class SecretDetector() : RegexRuleDetector("secret", BuildRules())
{
    /// <summary>
    /// Écarte les valeurs qui sont des marques de substitution plutôt que des
    /// secrets réels.
    /// </summary>
    /// <param name="rule">Règle ayant produit la correspondance</param>
    /// <param name="value">Valeur capturée</param>
    /// <returns><c>true</c> si la valeur doit être obfusquée</returns>
    protected override bool Accept(RegexRule rule, string value)
    {
        return !PlaceholderValues.IsPlaceholder(value);
    }

    /// <summary>
    /// Construit le jeu de règles du détecteur.
    /// </summary>
    /// <returns>Les règles à appliquer, de la plus spécifique à la plus générale</returns>
    private static IEnumerable<RegexRule> BuildRules()
    {
        return
        [
            new RegexRule("secret.aws.accessKey", SpanCategory.Secret, SecretPatterns.AwsAccessKey()),
            new RegexRule("secret.github.token", SpanCategory.Secret, SecretPatterns.GitHubToken()),
            new RegexRule("secret.gitlab.token", SpanCategory.Secret, SecretPatterns.GitLabToken()),
            new RegexRule("secret.slack.token", SpanCategory.Secret, SecretPatterns.SlackToken()),
            new RegexRule("secret.stripe.key", SpanCategory.Secret, SecretPatterns.StripeKey()),
            new RegexRule("secret.google.apiKey", SpanCategory.Secret, SecretPatterns.GoogleApiKey()),
            new RegexRule("secret.npm.token", SpanCategory.Secret, SecretPatterns.NpmToken()),
            new RegexRule("secret.jwt", SpanCategory.Secret, SecretPatterns.JsonWebToken()),
            new RegexRule("secret.ssh.publicKey", SpanCategory.KeyBlock, SecretPatterns.SshPublicKey()),
            new RegexRule("secret.http.authorization", SpanCategory.Secret, SecretPatterns.AuthorizationHeader(), "value"),
            new RegexRule("secret.azure.sas", SpanCategory.Secret, SecretPatterns.SharedAccessSignature(), "value"),
            new RegexRule("secret.unix.hash", SpanCategory.Secret, SecretPatterns.UnixPasswordHash()),
            new RegexRule("secret.cisco.type7", SpanCategory.Secret, SecretPatterns.CiscoType7Password(), "value"),
            new RegexRule("secret.snmp.community", SpanCategory.Secret, SecretPatterns.SnmpCommunity(), "value"),
            new RegexRule("secret.ipsec.psk", SpanCategory.Secret, SecretPatterns.IsakmpPreSharedKey(), "value"),
        ];
    }
}
