using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>Nature d'un élément qui attend une décision avant une publication.</summary>
public enum PendingKind
{
    /// <summary>
    /// Valeur signalée par le relevé des résidus — base64, entropie élevée,
    /// bloc encadré — ni masquée, ni gardée en clair, ni exceptée.
    /// </summary>
    Suspect,

    /// <summary>Terme proposé pour le vocabulaire, ni retenu, ni gardé en clair, ni écarté.</summary>
    VocabularyCandidate,
}

/// <summary>
/// Élément qui attend une décision de l'utilisateur avant une publication.
/// Il ne porte jamais la valeur réelle : seulement son extrait masqué, et les
/// fichiers où elle figure, sous leur chemin dans la cible.
/// </summary>
/// <param name="Kind">Nature de l'élément</param>
/// <param name="Value">Extrait masqué de la valeur, seule forme publiable</param>
/// <param name="Files">Fichiers où elle figure, sous leur chemin pseudonymisé dans la cible</param>
/// <param name="Occurrences">Nombre d'occurrences dans le lot</param>
/// <param name="Rule">Nature du soupçon pour une valeur signalée ; <c>null</c> pour un terme proposé</param>
public sealed record PendingDecision(
    PendingKind Kind,
    string Value,
    IReadOnlyList<string> Files,
    int Occurrences,
    string? Rule);

/// <summary>
/// Éléments à trancher avant une publication.
/// </summary>
public sealed partial class ExchangePresenter
{
    private const int TermTimeoutMilliseconds = 2000;

    /// <summary>
    /// Relève ce qui attend encore une décision dans une publication préparée :
    /// les valeurs signalées non tranchées, et les termes proposés pour le
    /// vocabulaire qu'aucune décision n'a encore retenus ni écartés.
    ///
    /// Les deux définitions sont celles de l'écran — l'aperçu et l'onglet des
    /// suggestions —, calculées sur l'instantané qui a préparé le plan. Toute
    /// décision y est enregistrée dans les règles du projet (vocabulaire, liste
    /// blanche, exceptions) : un élément tranché ne reparaît donc jamais.
    /// </summary>
    /// <param name="plan">Plan préparé par ce présentateur</param>
    /// <returns>Les éléments à trancher : les valeurs signalées d'abord, dans l'ordre du lot, puis les termes proposés</returns>
    public IReadOnlyList<PendingDecision> PendingDecisions(PublicationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Batch is null)
        {
            return [];
        }

        List<PendingDecision> pending = [.. PendingSuspects(plan.Batch, plan.TargetIsLinked)];
        pending.AddRange(PendingCandidates(plan.Batch, plan.TargetIsLinked));
        return pending;
    }

    /// <summary>
    /// Indique si une valeur signalée attend encore une décision : elle n'est
    /// gardée en clair ni partout, ni à cet endroit. Une valeur masquée n'est
    /// plus signalée du tout : le relevé des résidus ne voit que le clair.
    /// </summary>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="finding">Valeur signalée</param>
    /// <returns><c>true</c> si la valeur est à trancher</returns>
    private bool IsUndecided(PreparedFile file, ResidualFinding finding)
    {
        ObfuscationPolicy policy = _snapshot.Policy;
        return !policy.IsKeptEverywhere(finding.Value)
            && !policy.IsExceptedAt(file.Item.Key, finding.Line, file.Document.Text, finding.Start, finding.Value);
    }

    /// <summary>
    /// Regroupe les valeurs signalées non tranchées du lot : une entrée par
    /// valeur, avec les fichiers où elle figure.
    /// </summary>
    /// <param name="batch">Lot obfusqué</param>
    /// <param name="linked">Vrai si la cible est le dossier de l'IA</param>
    /// <returns>Les valeurs signalées à trancher</returns>
    private IEnumerable<PendingDecision> PendingSuspects(PreparedBatch batch, bool linked)
    {
        // Regroupées par valeur réelle, mais seul l'extrait masqué en sort.
        Dictionary<string, (ResidualFinding First, List<string> Files, int Count)> groups = new(StringComparer.Ordinal);
        List<string> order = [];

        foreach (PreparedFile file in batch.Files)
        {
            string target = TargetOf(file, linked);
            foreach (ResidualFinding finding in file.Suspicious.Where(finding => IsUndecided(file, finding)))
            {
                if (!groups.TryGetValue(finding.Value, out (ResidualFinding First, List<string> Files, int Count) group))
                {
                    group = (finding, [], 0);
                    order.Add(finding.Value);
                }

                if (!group.Files.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    group.Files.Add(target);
                }

                groups[finding.Value] = group with { Count = group.Count + 1 };
            }
        }

        return order.Select(value =>
        {
            (ResidualFinding first, List<string> files, int count) = groups[value];
            return new PendingDecision(PendingKind.Suspect, first.MaskedSample, files, count, first.Kind);
        });
    }

    /// <summary>
    /// Relève les termes proposés pour le vocabulaire qui n'ont reçu aucune
    /// décision, avec les fichiers où chacun figure.
    /// </summary>
    /// <param name="batch">Lot obfusqué</param>
    /// <param name="linked">Vrai si la cible est le dossier de l'IA</param>
    /// <returns>Les termes à trancher</returns>
    private IEnumerable<PendingDecision> PendingCandidates(PreparedBatch batch, bool linked)
    {
        ObfuscationPolicy policy = _snapshot.Policy;
        List<(string Target, string Text)> texts = [.. batch.Files.Select(file => (TargetOf(file, linked), policy.Entities.Blank(file.Document.Text)))];

        foreach (VocabularyCandidate candidate in VocabularyCandidateFinder.FindUndecided(batch.Files.Select(file => file.Document.Text), policy, _snapshot.Vocabulary))
        {
            // Le terme est cherché comme le moteur de propositions l'a compté :
            // un mot entier, sans égard à la casse.
            Regex term = new(
                $@"(?<![\w\-]){Regex.Escape(candidate.Text)}(?![\w\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(TermTimeoutMilliseconds));

            List<string> files = [.. texts.Where(text => term.IsMatch(text.Text)).Select(text => text.Target).Distinct(StringComparer.OrdinalIgnoreCase)];
            yield return new PendingDecision(PendingKind.VocabularyCandidate, SensitiveText.Mask(candidate.Text), files, candidate.Occurrences, null);
        }
    }
}
