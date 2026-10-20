namespace Obfuscator.Core.Model.Reporting;

/// <summary>
/// Masquage des valeurs sensibles destinées à un rapport ou à un journal.
///
/// L'écran de revue interactif affiche les valeurs entières — c'est sa raison
/// d'être, l'utilisateur doit voir ce qu'il décide de masquer ou non. Tout ce
/// qui est écrit sur disque ou dans un journal passe en revanche par ce
/// masquage : un rapport d'exécution qui contiendrait les secrets en clair
/// annulerait l'intérêt de l'outil.
/// </summary>
public static class SensitiveText
{
    private const int MinimumLengthForPartialMask = 6;

    /// <summary>
    /// Masque une valeur en n'en conservant qu'une amorce reconnaissable.
    /// </summary>
    /// <param name="value">Valeur à masquer</param>
    /// <returns>Une forme abrégée non exploitable, par exemple <c>P@s…d</c></returns>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "∅";
        }

        // En dessous d'une certaine longueur, exposer trois caractères
        // reviendrait à exposer la valeur : on masque intégralement.
        if (value.Length < MinimumLengthForPartialMask)
        {
            return new string('•', value.Length);
        }

        return $"{value[..3]}…{value[^1]}";
    }
}
