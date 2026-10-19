using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Attribue à chaque valeur détectée le pseudonyme qui la remplacera, en
/// s'appuyant sur le coffre pour garantir la stabilité des attributions et sur
/// les stratégies pour en déterminer la forme.
///
/// La fabrique traite aussi un cas limite discret mais gênant : si le texte
/// source contient déjà, littéralement, une chaîne ayant la forme d'un jeton,
/// réutiliser ce jeton ferait qu'à la désobfuscation la chaîne d'origine serait
/// remplacée par une valeur qui n'a rien à voir. Les jetons rencontrés dans les
/// sources sont donc écartés du stock disponible.
/// </summary>
public sealed class PseudonymFactory
{
    private const int MaxAllocationAttempts = 1000;

    private readonly MappingStore _store;
    private readonly TokenFormat _format;
    private readonly List<IPseudonymStrategy> _strategies;
    private readonly HashSet<string> _reservedLiterals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Construit la fabrique pour un projet.
    /// </summary>
    /// <param name="store">Coffre du projet, rechargé ou neuf</param>
    /// <param name="format">Format des jetons du projet</param>
    /// <param name="strategies">Stratégies spécialisées, consultées avant la stratégie neutre</param>
    public PseudonymFactory(MappingStore store, TokenFormat format, IEnumerable<IPseudonymStrategy>? strategies = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(format);

        _store = store;
        _format = format;

        // L'ordre compte : la stratégie neutre accepte tout, elle doit donc
        // rester en dernier recours.
        _strategies = [.. strategies ?? [], new NeutralTokenStrategy()];
    }

    /// <summary>
    /// Déclare un texte source auprès de la fabrique afin d'en écarter les
    /// chaînes ayant déjà la forme d'un jeton, et de faire connaître aux
    /// stratégies les valeurs réelles qu'il contient. À appeler pour chaque
    /// fichier avant de traiter le lot : une adresse privée de substitution
    /// tirée pour le premier fichier ne doit pas tomber sur un réseau réel du
    /// dernier.
    /// </summary>
    /// <param name="sourceText">Contenu du fichier source</param>
    public void RegisterSource(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        foreach (TokenOccurrence occurrence in _format.FindOccurrences(sourceText))
        {
            _reservedLiterals.Add(occurrence.Identity.Canonical);
            _reservedLiterals.Add(occurrence.RawText);
        }

        foreach (IPseudonymStrategy strategy in _strategies)
        {
            strategy.Observe(sourceText);
        }
    }

    /// <summary>
    /// Retourne le pseudonyme associé à un span, en le créant au besoin.
    /// </summary>
    /// <param name="span">Span retenu par le moteur</param>
    /// <returns>Le pseudonyme à substituer</returns>
    /// <exception cref="InvalidOperationException">Si aucun pseudonyme libre n'a pu être trouvé</exception>
    public string GetOrCreate(DetectedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        string canonical = ValueCanonicalizer.Canonicalize(span.Category, span.RawValue);

        // Valeur déjà connue : on réutilise son pseudonyme et l'on note la
        // graphie observée, qui peut différer de la première rencontrée.
        if (_store.TryGetByValue(span.Category, canonical, out MappingEntry existing))
        {
            existing.RecordVariant(span.RawValue);
            return existing.Pseudonym;
        }

        IPseudonymStrategy strategy = SelectStrategy(span.Category);

        for (int attempt = 0; attempt < MaxAllocationAttempts; attempt++)
        {
            int index = _store.ReserveIndex(span.Category);
            string pseudonym = strategy.Create(new PseudonymRequest(span.Category, canonical, span.RawValue, index, _format));

            if (_store.IsPseudonymTaken(pseudonym) || _reservedLiterals.Contains(pseudonym))
            {
                continue;
            }

            return _store.Add(span.Category, canonical, span.RawValue, index, pseudonym).Pseudonym;
        }

        throw new InvalidOperationException(
            $"Aucun pseudonyme disponible pour la catégorie {span.Category} après {MaxAllocationAttempts} tentatives.");
    }

    /// <summary>
    /// Choisit la première stratégie revendiquant la catégorie.
    /// </summary>
    /// <param name="category">Catégorie à traiter</param>
    /// <returns>La stratégie retenue</returns>
    private IPseudonymStrategy SelectStrategy(SpanCategory category)
    {
        // La stratégie neutre clôt toujours la liste et accepte tout : le
        // repli sur le dernier élément ne sert qu'à satisfaire le compilateur.
        return _strategies.Find(strategy => strategy.CanHandle(category)) ?? _strategies[^1];
    }
}
