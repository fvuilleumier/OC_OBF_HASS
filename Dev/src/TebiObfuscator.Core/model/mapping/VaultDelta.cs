namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// État d'un coffre à un instant donné : ses pseudonymes, et le nombre de
/// graphies connues de chacun. C'est le point de comparaison qui permet de
/// dire, après un traitement, ce qu'il a ajouté au coffre.
/// </summary>
public sealed class VaultBaseline
{
    private readonly Dictionary<string, int> _variantCounts;

    /// <summary>
    /// Construit le relevé.
    /// </summary>
    /// <param name="variantCounts">Nombre de graphies de chaque entrée, indexé par pseudonyme</param>
    internal VaultBaseline(Dictionary<string, int> variantCounts)
    {
        _variantCounts = variantCounts;
    }

    /// <summary>
    /// Indique si un pseudonyme existait au moment du relevé.
    /// </summary>
    /// <param name="pseudonym">Pseudonyme examiné</param>
    /// <param name="variants">Nombre de graphies que l'entrée comptait alors</param>
    /// <returns><c>true</c> si l'entrée existait</returns>
    internal bool TryGetVariantCount(string pseudonym, out int variants)
    {
        return _variantCounts.TryGetValue(pseudonym, out variants);
    }
}

/// <summary>
/// Ce qu'un traitement a ajouté à un coffre : des attributions nouvelles, et
/// des graphies nouvelles d'attributions qui existaient déjà.
///
/// Les deux comptent. Une attribution nouvelle sans laquelle une réponse de
/// l'IA ne pourrait pas être restituée ; une graphie nouvelle — « Emma »
/// rencontrée écrite « EMMA » — sans laquelle le contrôle final ne la
/// rechercherait plus sous cette forme.
/// </summary>
/// <param name="NewEntries">Attributions nouvelles, copiées</param>
/// <param name="NewVariants">Graphies nouvelles d'attributions anciennes</param>
public sealed record VaultDelta(
    IReadOnlyList<MappingEntry> NewEntries,
    IReadOnlyList<(string Pseudonym, string Variant)> NewVariants)
{
    /// <summary>Aucun ajout.</summary>
    public static VaultDelta Empty { get; } = new([], []);

    /// <summary>Vrai si le traitement n'a rien ajouté au coffre.</summary>
    public bool IsEmpty => NewEntries.Count == 0 && NewVariants.Count == 0;
}
