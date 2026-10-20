using System.Text;

namespace Obfuscator.Core.Model.Engine;

/// <summary>
/// Applique au texte source la liste des spans retenus, en une seule passe
/// croissante. Les offsets ayant été calculés une fois pour toutes sur le texte
/// d'origine, ils ne sont jamais recalculés : le texte de sortie est reconstruit
/// par concaténation, ce qui reste linéaire quel que soit le nombre de
/// remplacements (une insertion au milieu d'un tampon coûterait, elle, une
/// recopie complète par span).
/// </summary>
public static class TextRewriter
{
    /// <summary>
    /// Reconstruit le texte en substituant chaque span par son remplacement.
    /// </summary>
    /// <param name="text">Texte source, inchangé</param>
    /// <param name="spans">Spans à remplacer, deux à deux disjoints et triés par position croissante</param>
    /// <param name="replacementFor">Fonction fournissant le texte de remplacement d'un span</param>
    /// <returns>Le texte obfusqué, identique à la source hors des spans remplacés</returns>
    /// <exception cref="ArgumentException">Si les spans se chevauchent, ne sont pas triés ou sortent du texte</exception>
    public static string Rewrite(string text, IReadOnlyList<DetectedSpan> spans, Func<DetectedSpan, string> replacementFor)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(replacementFor);

        if (spans.Count == 0)
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        int position = 0;

        foreach (DetectedSpan span in spans)
        {
            // Un span mal formé trahit une erreur d'offset chez un détecteur.
            // Mieux vaut échouer bruyamment que produire un fichier corrompu
            // dans lequel une portion de la valeur réelle aurait survécu.
            if (span.Start < position)
            {
                throw new ArgumentException(
                    $"Spans non triés ou en chevauchement à l'offset {span.Start} (règle {span.RuleId}).",
                    nameof(spans));
            }

            if (span.End > text.Length)
            {
                throw new ArgumentException(
                    $"Span hors des limites du texte à l'offset {span.Start} (règle {span.RuleId}).",
                    nameof(spans));
            }

            builder.Append(text, position, span.Start - position);
            builder.Append(replacementFor(span));
            position = span.End;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }
}
