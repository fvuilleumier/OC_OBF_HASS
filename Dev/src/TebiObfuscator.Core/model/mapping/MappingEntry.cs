using System.Text.Json.Serialization;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Association entre une valeur réelle et le pseudonyme qui la remplace.
///
/// Le mapping est bidirectionnel mais asymétrique : la clé d'attribution est la
/// forme canonique de la valeur (pour que <c>TEBICOM.CH</c> et
/// <c>tebicom.ch</c> reçoivent le même pseudonyme), alors que la restitution
/// part du pseudonyme et rend la forme d'origine. Les autres graphies observées
/// sont conservées dans <see cref="Variants"/> : elles ne servent pas à la
/// restitution — qui rend toujours la forme canonique, faute de pouvoir deviner
/// laquelle des graphies était attendue — mais à l'audit.
/// </summary>
/// <remarks>
/// La collection de variantes est exposée en lecture seule : le sérialiseur la
/// remplit en place grâce au mode <c>Populate</c>, ce qui évite d'ouvrir un
/// accesseur en écriture sur une collection publique.
/// </remarks>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class MappingEntry
{
    /// <summary>Pseudonyme substitué dans les fichiers de sortie.</summary>
    public string Pseudonym { get; set; } = string.Empty;

    /// <summary>Nature de la valeur, qui détermine la famille du pseudonyme.</summary>
    public SpanCategory Category { get; set; }

    /// <summary>Forme canonique de la valeur réelle : clé d'attribution.</summary>
    public string Canonical { get; set; } = string.Empty;

    /// <summary>Première graphie rencontrée de la valeur réelle.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>Numéro d'ordre attribué dans la famille de la catégorie.</summary>
    public int Index { get; set; }

    /// <summary>Autres graphies rencontrées de la même valeur canonique.</summary>
    public IList<string> Variants { get; } = [];

    /// <summary>Date de première attribution, pour l'audit du coffre.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Enregistre une graphie observée si elle diffère de celles déjà connues.
    /// </summary>
    /// <param name="literal">Graphie rencontrée dans un fichier source</param>
    public void RecordVariant(string literal)
    {
        if (string.IsNullOrEmpty(literal) || literal == Original || Variants.Contains(literal))
        {
            return;
        }

        Variants.Add(literal);
    }

    /// <summary>
    /// Produit une copie indépendante de l'entrée, variantes comprises.
    /// </summary>
    /// <returns>Une entrée identique, qu'aucune modification ultérieure de l'original n'atteint</returns>
    public MappingEntry Clone()
    {
        MappingEntry copy = new()
        {
            Pseudonym = Pseudonym,
            Category = Category,
            Canonical = Canonical,
            Original = Original,
            Index = Index,
            CreatedAt = CreatedAt,
        };

        foreach (string variant in Variants)
        {
            copy.Variants.Add(variant);
        }

        return copy;
    }

    /// <summary>
    /// Retourne toutes les graphies connues de la valeur réelle. C'est cet
    /// ensemble que le contrôle de vérification recherche dans le fichier de
    /// sortie : aucune d'elles ne doit y subsister.
    /// </summary>
    /// <returns>La graphie d'origine suivie de ses variantes</returns>
    public IEnumerable<string> AllLiterals()
    {
        yield return Original;
        foreach (string variant in Variants)
        {
            yield return variant;
        }
    }
}
