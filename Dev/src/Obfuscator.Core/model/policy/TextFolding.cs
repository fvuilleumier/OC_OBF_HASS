using System.Text;

namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Rapproche deux graphies d'un même terme qui ne diffèrent que par les accents.
///
/// Le besoin est concret en Suisse romande : un export d'équipement réseau écrit
/// « Geneve » là où une note interne écrit « Genève », et les deux désignent la
/// même chose. Sans ce repliement, il faudrait inscrire chaque graphie dans le
/// vocabulaire et un oubli se traduirait par une fuite ; le même terme
/// recevrait en outre deux pseudonymes distincts, et le modèle y verrait deux
/// entités.
///
/// Le repliement s'appuie sur une table explicite plutôt que sur la
/// normalisation Unicode du cadriciel. La raison est concrète : l'application
/// est compilée en mondialisation invariante, mode dans lequel
/// <c>String.Normalize</c> laisse les caractères accentués intacts. Se fier à
/// elle donnerait un repliement silencieusement inopérant — exactement le genre
/// de défaut qui ne se voit qu'au moment où une donnée fuit.
///
/// Le repliement ne s'applique jamais au texte source lui-même : il sert
/// uniquement de clé de comparaison, faute de quoi tous les offsets calculés
/// par les détecteurs se décaleraient.
/// </summary>
public static class TextFolding
{
    /// <summary>
    /// Variantes accentuées de chaque lettre de base, dans l'ordre où elles
    /// servent aussi à composer des motifs indifférents aux accents.
    /// </summary>
    private static readonly Dictionary<char, string> Variants = new()
    {
        ['a'] = "aàáâãäå",
        ['c'] = "cç",
        ['e'] = "eéèêë",
        ['i'] = "iíìîï",
        ['n'] = "nñ",
        ['o'] = "oóòôõö",
        ['u'] = "uúùûü",
        ['y'] = "yÿý",
    };

    /// <summary>Table inverse, construite une fois : variante vers lettre de base.</summary>
    private static readonly Dictionary<char, char> BaseLetters = BuildBaseLetters();

    /// <summary>
    /// Replie une valeur sur sa forme de comparaison : sans accents et en
    /// majuscules.
    /// </summary>
    /// <param name="value">Valeur à replier</param>
    /// <returns>La forme de comparaison correspondante</returns>
    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder builder = new(value.Length);

        foreach (char character in value)
        {
            // Les signes diacritiques combinants d'une graphie décomposée sont
            // écartés : « e » suivi d'un accent grave doit se replier comme le
            // « è » précomposé.
            if (character is >= '̀' and <= 'ͯ')
            {
                continue;
            }

            builder.Append(BaseLetters.TryGetValue(char.ToLowerInvariant(character), out char baseLetter)
                ? baseLetter
                : character);
        }

        return builder.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Replie un seul caractère : sa lettre de base, en majuscule.
    ///
    /// Contrairement à <see cref="Fold"/>, ce repliement conserve la longueur
    /// du texte — un caractère donne toujours un caractère. C'est ce qui permet
    /// de chercher dans un texte replié et de reporter les positions trouvées
    /// sur le texte d'origine.
    /// </summary>
    /// <param name="character">Caractère à replier</param>
    /// <returns>La lettre de base en majuscule, ou le caractère en majuscule</returns>
    public static char FoldCharacter(char character)
    {
        return char.ToUpperInvariant(BaseLetters.TryGetValue(char.ToLowerInvariant(character), out char baseLetter)
            ? baseLetter
            : character);
    }

    /// <summary>
    /// Replie un texte caractère par caractère, en conservant sa longueur.
    /// </summary>
    /// <param name="value">Texte à replier</param>
    /// <returns>Le texte replié, de même longueur</returns>
    public static string FoldPreservingLength(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                span[index] = FoldCharacter(source[index]);
            }
        });
    }

    /// <summary>
    /// Retourne la classe de caractères couvrant toutes les variantes
    /// accentuées d'une lettre, ou <c>null</c> si la lettre n'en a pas.
    /// </summary>
    /// <param name="character">Caractère à traduire</param>
    /// <returns>La liste des variantes, ou <c>null</c></returns>
    public static string? VariantsOf(char character)
    {
        char lower = char.ToLowerInvariant(character);
        return BaseLetters.TryGetValue(lower, out char baseLetter) ? Variants[baseLetter] : null;
    }

    /// <summary>
    /// Construit la table inverse associant chaque variante à sa lettre de base.
    /// </summary>
    /// <returns>La table inverse</returns>
    private static Dictionary<char, char> BuildBaseLetters()
    {
        Dictionary<char, char> table = [];

        foreach ((char baseLetter, string variants) in Variants)
        {
            foreach (char variant in variants)
            {
                table[variant] = baseLetter;
            }
        }

        return table;
    }
}
