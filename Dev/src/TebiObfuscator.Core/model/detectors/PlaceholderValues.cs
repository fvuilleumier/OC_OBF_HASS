using System.Text.RegularExpressions;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Reconnaît les valeurs qui ne sont pas des secrets mais des marques de
/// substitution : référence à une variable d'environnement, gabarit de
/// déploiement, champ vidé à la main.
///
/// Le filtrage reste volontairement structurel. On écarte <c>${DB_PASSWORD}</c>
/// ou <c>&lt;mot de passe&gt;</c>, jamais un mot du dictionnaire comme
/// <c>changeme</c> : ce dernier peut parfaitement être le mot de passe réel,
/// et un faux négatif à l'obfuscation est une fuite, alors qu'un faux positif
/// n'est qu'un jeton de plus dans le fichier.
/// </summary>
internal static partial class PlaceholderValues
{
    private const int TimeoutMilliseconds = 1000;

    /// <summary>Référence à une variable ou gabarit de déploiement.</summary>
    [GeneratedRegex(
        @"^(?:\$\{[^}]*\}|\$\([^)]*\)|%[A-Za-z0-9_]*%|<[^>]*>|\{\{[^}]*\}\}|__[A-Za-z0-9_]*__|\$[A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.None,
        TimeoutMilliseconds)]
    private static partial Regex TemplateReference();

    /// <summary>Champ masqué à la main : suite d'un seul caractère de ponctuation.</summary>
    [GeneratedRegex(@"^([*x#\-.•])\1{2,}$", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex ManualMask();

    /// <summary>
    /// Renvoi YAML vers une valeur tenue ailleurs : <c>!secret wifi_password</c>
    /// de Home Assistant et d'ESPHome, <c>!env_var DB_PASSWORD</c>. Le nom qui
    /// suit la marque désigne le secret sans le contenir.
    /// </summary>
    [GeneratedRegex(@"^!(?:secret|env_var|include|input|vault)\b.*$", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex YamlReference();

    /// <summary>
    /// Indique si une valeur est une marque de substitution plutôt qu'un secret.
    /// </summary>
    /// <param name="value">Valeur capturée par un détecteur</param>
    /// <returns><c>true</c> si la valeur ne doit pas être obfusquée</returns>
    public static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        return TemplateReference().IsMatch(trimmed) || ManualMask().IsMatch(trimmed) || YamlReference().IsMatch(trimmed);
    }
}
