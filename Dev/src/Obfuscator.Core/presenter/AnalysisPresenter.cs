using System.Diagnostics;
using Obfuscator.Core.Model.Documents;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Avancement d'un traitement par lot.
/// </summary>
/// <param name="FileIndex">Rang du fichier en cours, à partir de 0</param>
/// <param name="FileCount">Nombre de fichiers du lot</param>
/// <param name="FileName">Nom du fichier en cours, vide entre deux fichiers</param>
/// <param name="Step">Étape en cours, en clair</param>
/// <param name="Fraction">Part du travail accomplie, entre 0 et 1</param>
public sealed record BatchProgress(int FileIndex, int FileCount, string FileName, string Step, double Fraction);

/// <summary>
/// Portion de texte qui sera remplacée, décrite pour l'affichage.
/// </summary>
/// <param name="Start">Position du début dans le texte source</param>
/// <param name="Length">Longueur dans le texte source</param>
/// <param name="Category">Nature de la valeur</param>
/// <param name="Value">Valeur réelle</param>
/// <param name="Pseudonym">Pseudonyme qui la remplacera</param>
/// <param name="RuleId">Règle l'ayant repérée</param>
public sealed record PreviewSpan(
    int Start,
    int Length,
    SpanCategory Category,
    string Value,
    string Pseudonym,
    string RuleId);

/// <summary>
/// Aperçu d'un fichier : son texte d'origine, le texte pseudonymisé, ce qui y
/// sera remplacé et ce que l'outil signale sans le remplacer.
/// </summary>
/// <param name="Path">Chemin du fichier</param>
/// <param name="Text">Texte source, tel qu'il sera traité</param>
/// <param name="MaskedText">Texte pseudonymisé, tel qu'il sera écrit</param>
/// <param name="Kind">Format deviné</param>
/// <param name="EncodingName">Encodage détecté</param>
/// <param name="Spans">Portions qui seront remplacées</param>
/// <param name="Residuals">Éléments suspects laissés en clair</param>
public sealed record FilePreview(
    string Path,
    string Text,
    string MaskedText,
    FileKind Kind,
    string EncodingName,
    IReadOnlyList<PreviewSpan> Spans,
    IReadOnlyList<ResidualFinding> Residuals)
{
    /// <summary>
    /// Clé du fichier pour les exceptions ponctuelles : son chemin dans la
    /// source, précédé d'une barre (voir <see cref="LineExemption.KeyFor"/>).
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Chemin sous lequel le fichier partira dans la cible, nom pseudonymisé
    /// compris, avec des barres obliques.
    /// </summary>
    public string TargetPath { get; init; } = string.Empty;
}

/// <summary>
/// Résultat d'une analyse complète d'un lot.
/// </summary>
/// <param name="Scan">Valeurs distinctes repérées et termes proposés</param>
/// <param name="Previews">Aperçu de chaque fichier, dans l'ordre du lot</param>
/// <param name="Exemptions">Occurrences laissées en clair au moment de l'analyse</param>
/// <param name="RulesFingerprint">Empreinte des règles sur lesquelles l'analyse repose</param>
/// <param name="Duration">Durée de l'analyse</param>
public sealed record AnalysisResult(
    ScanReport Scan,
    IReadOnlyList<FilePreview> Previews,
    IReadOnlyList<LineExemption> Exemptions,
    string RulesFingerprint,
    TimeSpan Duration)
{
    /// <summary>
    /// Ce que l'analyse a ajouté au coffre de son instantané : les pseudonymes
    /// que l'aperçu montre pour des valeurs encore jamais obfusquées, et les
    /// graphies nouvelles de valeurs connues. Le coffre les adopte quand on
    /// copie le texte pseudonymisé de l'aperçu, pour que la réponse de l'IA
    /// puisse être restaurée.
    /// </summary>
    public VaultDelta Delta { get; init; } = VaultDelta.Empty;

    /// <summary>Attributions nouvelles de l'analyse (voir <see cref="Delta"/>).</summary>
    public IReadOnlyList<MappingEntry> NewEntries => Delta.NewEntries;

    /// <summary>
    /// Fichiers réellement analysés par cette analyse. Tout le lot pour une
    /// analyse complète ; pour une analyse limitée aux fichiers ouverts
    /// (<see cref="AnalysisPresenter.AnalyseSubset"/>), les autres aperçus sont
    /// repris de l'analyse précédente, et reposent sur les règles d'alors.
    /// </summary>
    public IReadOnlyList<string> AnalysedPaths { get; init; } = [];

    /// <summary>Vrai si une partie des aperçus a été reprise d'une analyse précédente.</summary>
    public bool IsPartial => AnalysedPaths.Count < Previews.Count;
}

/// <summary>
/// Analyse un lot en une seule passe : ce qui sera masqué, où, par quel
/// pseudonyme, et ce qui reste suspect.
///
/// Elle réunit le relevé et l'aperçu, qui lisaient et détectaient chaque
/// fichier deux fois pour produire deux vues d'un même résultat : les valeurs
/// distinctes se déduisent des remplacements de l'aperçu, sans rien relancer.
///
/// Elle est faite pour tourner en arrière-plan. Le projet est recopié à la
/// construction — donc sur le fil de l'écran, là où il est modifié — et
/// l'analyse ne travaille ensuite que sur cette copie : l'écran reste libre de
/// changer les règles pendant qu'elle tourne, et le coffre réel n'est jamais
/// touché. Les pseudonymes attribués dans la copie sont ceux qu'écrira
/// l'obfuscation, les attributions étant déterministes.
/// </summary>
public sealed class AnalysisPresenter
{
    /// <summary>Part de l'avancement consacrée aux fichiers ; le reste va à la recherche de termes.</summary>
    private const double FilesShare = 0.92;

    private readonly ObfuscationProject _snapshot;

    /// <summary>
    /// Prépare l'analyse en prenant un instantané du projet. À appeler sur le
    /// fil qui modifie le projet.
    /// </summary>
    /// <param name="project">Projet à analyser</param>
    public AnalysisPresenter(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _snapshot = project.CreateSnapshot();
        RulesFingerprint = project.RulesFingerprint();
    }

    /// <summary>Empreinte des règles au moment de l'instantané.</summary>
    public string RulesFingerprint { get; }

    /// <summary>
    /// Analyse un lot de fichiers. Peut s'exécuter sur n'importe quel fil.
    /// </summary>
    /// <param name="filePaths">Fichiers à analyser, dans l'ordre où ils seront obfusqués</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le résultat complet de l'analyse</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public AnalysisResult Analyse(
        IReadOnlyList<string> filePaths,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        Stopwatch watch = Stopwatch.StartNew();
        (List<FilePreview> previews, VaultDelta delta) = AnalyseFiles(filePaths, progress, cancellationToken);
        return Summarise(previews, filePaths, delta, watch, progress, cancellationToken);
    }

    /// <summary>
    /// Analyse une partie seulement d'un lot — les fichiers ouverts à l'écran —
    /// et reprend pour les autres les aperçus d'une analyse précédente.
    ///
    /// C'est ce qui rend l'analyse automatique supportable sur un gros lot :
    /// après chaque décision, seuls les fichiers qu'on regarde sont relus et
    /// détectés de nouveau. Le relevé des valeurs et les propositions de termes
    /// sont, eux, recalculés sur le lot entier à partir des aperçus réunis : ils
    /// ne coûtent presque rien, et l'onglet des valeurs repérées ne doit pas se
    /// réduire aux seuls fichiers ouverts.
    ///
    /// Un fichier du lot que l'analyse précédente ne connaît pas — il vient
    /// d'arriver — est analysé d'office : sans cela, il n'aurait pas d'aperçu.
    /// </summary>
    /// <param name="filePaths">Lot complet, dans l'ordre où il sera obfusqué</param>
    /// <param name="subset">Fichiers à analyser de nouveau</param>
    /// <param name="previous">Analyse précédente, dont les autres aperçus sont repris</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le résultat réuni, dont <see cref="AnalysisResult.AnalysedPaths"/> dit ce qui a été analysé</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public AnalysisResult AnalyseSubset(
        IReadOnlyList<string> filePaths,
        IReadOnlyCollection<string> subset,
        AnalysisResult previous,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(subset);
        ArgumentNullException.ThrowIfNull(previous);

        Stopwatch watch = Stopwatch.StartNew();
        HashSet<string> wanted = new(subset, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FilePreview> known = previous.Previews
            .DistinctBy(preview => preview.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(preview => preview.Path, StringComparer.OrdinalIgnoreCase);

        // L'ordre du lot est conservé : c'est celui de l'obfuscation, qui
        // décide des numéros attribués aux valeurs nouvelles.
        List<string> analysed = [.. filePaths.Where(path => wanted.Contains(path) || !known.ContainsKey(path))];
        (List<FilePreview> fresh, VaultDelta delta) = AnalyseFiles(analysed, progress, cancellationToken);

        Dictionary<string, FilePreview> freshByPath = fresh.ToDictionary(preview => preview.Path, StringComparer.OrdinalIgnoreCase);
        List<FilePreview> previews = [.. filePaths.Select(path => freshByPath.TryGetValue(path, out FilePreview? preview) ? preview : known[path])];

        return Summarise(previews, analysed, delta, watch, progress, cancellationToken);
    }

    /// <summary>
    /// Réunit les aperçus d'un lot en un résultat : relevé des valeurs
    /// distinctes, termes proposés, exceptions et empreinte des règles.
    /// </summary>
    /// <param name="previews">Aperçu de chaque fichier du lot, dans l'ordre</param>
    /// <param name="analysed">Fichiers réellement analysés</param>
    /// <param name="delta">Attributions nouvelles de l'analyse</param>
    /// <param name="watch">Chronomètre lancé au début de l'analyse</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le résultat complet</returns>
    private AnalysisResult Summarise(
        List<FilePreview> previews,
        IReadOnlyList<string> analysed,
        VaultDelta delta,
        Stopwatch watch,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        int count = previews.Count;
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BatchProgress(count, count, string.Empty, "recherche de termes récurrents", FilesShare));

        // Le relevé se déduit des remplacements de chaque aperçu : un aperçu
        // repris d'une analyse précédente compte comme les autres.
        FindingAggregator findings = new();
        foreach (FilePreview preview in previews)
        {
            findings.Add(
                preview.Spans.Select(span => DetectedSpan.Create(span.Start, span.Length, span.Category, span.Value, span.RuleId)),
                Path.GetFileName(preview.Path));
        }

        // Une proposition écartée cesse d'exister pour l'affichage : elle n'est
        // ni reproposée, ni signalée, ni colorée.
        IReadOnlyList<VocabularyCandidate> candidates = VocabularyCandidateFinder.FindUndecided(
            previews.Select(preview => preview.Text),
            _snapshot.Policy,
            _snapshot.Vocabulary);

        progress?.Report(new BatchProgress(count, count, string.Empty, "terminé", 1.0));

        return new AnalysisResult(
            new ScanReport(findings.ToFindings(), candidates, count),
            previews,
            ParseExemptions(_snapshot.Settings.LineExceptions),
            RulesFingerprint,
            watch.Elapsed)
        {
            Delta = delta,
            AnalysedPaths = [.. analysed],
        };
    }

    /// <summary>
    /// Simule l'obfuscation d'une liste de fichiers et en compose les aperçus.
    /// </summary>
    /// <param name="filePaths">Fichiers à analyser, dans l'ordre où ils seront obfusqués</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>L'aperçu de chaque fichier, et les attributions nouvelles de l'instantané</returns>
    private (List<FilePreview> Previews, VaultDelta Delta) AnalyseFiles(
        IReadOnlyList<string> filePaths,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObfuscationEngine engine = _snapshot.CreateObfuscationEngine();
        VaultBaseline baseline = _snapshot.Store.CaptureBaseline();
        int count = filePaths.Count;

        // La boucle est celle de l'obfuscation, contenus et noms dans le même
        // ordre : les pseudonymes de l'aperçu sont ainsi ceux qu'elle écrira.
        List<BatchItem> items = [.. filePaths.Select(path => BatchItem.Of(path, _snapshot.SourcePath))];
        List<RewrittenFile> simulated = BatchRewriter.Run(
            engine,
            items,
            simulate: true,
            (index, step) => progress?.Report(new BatchProgress(
                index,
                count,
                Path.GetFileName(items[index].Path),
                step,
                Share(index, step == "lecture" ? 0.0 : 0.05, count))),
            cancellationToken);

        // Les valeurs connues sont recherchées une fois tout le lot simulé : le
        // coffre de l'instantané contient alors aussi les valeurs de ce lot, et
        // une valeur masquée dans un fichier mais restée en clair dans un autre
        // est ainsi signalée.
        KnownValueScanner known = new(_snapshot.Store.Entries);
        List<FilePreview> previews = new(count);

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (BatchItem item, SourceDocument document, ObfuscationResult result, RewrittenPath name) = simulated[index];
            progress?.Report(new BatchProgress(index, count, Path.GetFileName(item.Path), "recherche de résidus", Share(index, 0.75, count)));

            previews.Add(new FilePreview(
                item.Path,
                document.Text,
                result.Text,
                FormatSniffer.Detect(item.Key, document.Text),
                document.EncodingName,
                DescribeSpans(result),
                FindResiduals(document.Text, item.Key, result.Applied, known))
            {
                Key = item.Key,
                TargetPath = _snapshot.IsTargetLinked ? name.RelativePath : ObfuscatePresenter.OutputRelativePath(name.RelativePath),
            });
        }

        return (previews, _snapshot.Store.DeltaSince(baseline));
    }

    /// <summary>
    /// Décrit les remplacements d'un fichier, avec le pseudonyme réellement
    /// substitué.
    /// </summary>
    /// <param name="result">Résultat de la réécriture</param>
    /// <returns>Les portions remplacées</returns>
    private static List<PreviewSpan> DescribeSpans(ObfuscationResult result)
    {
        List<PreviewSpan> spans = new(result.Applied.Count);
        foreach (DetectedSpan span in result.Applied)
        {
            spans.Add(new PreviewSpan(
                span.Start,
                span.Length,
                span.Category,
                span.RawValue,
                result.PseudonymsByStart.TryGetValue(span.Start, out string? pseudonym) ? pseudonym : string.Empty,
                span.RuleId));
        }

        return spans;
    }

    /// <summary>
    /// Relève les éléments suspects laissés en clair, hors valeurs de la liste
    /// blanche : les chaînes d'apparence aléatoire, et les valeurs déjà connues
    /// du coffre que les règles actuelles ne masquent pas. Une occurrence que
    /// l'utilisateur a gardée en clair à cet endroit reste relevée, marquée
    /// comme telle : elle n'est plus à trancher, mais on doit pouvoir revenir
    /// sur la décision.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="fileName">Nom du fichier, pour les exceptions ponctuelles</param>
    /// <param name="applied">Portions qui seront remplacées</param>
    /// <param name="known">Recherche des valeurs connues du coffre</param>
    /// <returns>Les éléments à signaler, par position croissante</returns>
    private List<ResidualFinding> FindResiduals(
        string text,
        string fileName,
        IReadOnlyList<DetectedSpan> applied,
        KnownValueScanner known)
    {
        ObfuscationPolicy policy = _snapshot.Policy;
        EntityMask entities = policy.Entities.Find(text);
        List<ResidualFinding> knownInClear = FindKnownInClear(text, fileName, applied, known, entities);

        // Une valeur connue du coffre en dit plus qu'un simple soupçon
        // d'entropie : à portion commune, c'est elle qui est retenue. Un
        // identifiant d'entité gardé en clair n'est pas un soupçon.
        List<ResidualFinding> residuals = [.. ResidualScanner
            .Scan(text, applied)
            .Where(finding => !policy.IsKeptEverywhere(finding.Value)
                && !entities.Covers(finding.Start, finding.Length)
                && !knownInClear.Any(other => other.Start < finding.Start + finding.Length && finding.Start < other.Start + other.Length))
            .Select(finding => policy.IsExceptedAt(fileName, finding.Line, text, finding.Start, finding.Value)
                ? finding with { IsExempted = true }
                : finding)];

        residuals.AddRange(knownInClear);
        residuals.Sort((left, right) => left.Start.CompareTo(right.Start));
        return residuals;
    }

    /// <summary>
    /// Relève les valeurs connues du coffre restées en clair dans un texte.
    ///
    /// C'est le cas d'un terme masqué lors d'une exécution précédente puis
    /// retiré du vocabulaire, ou d'un nom repéré dans un fichier mais écrit
    /// autrement dans un autre. Sans ce relevé, la valeur ne se révélerait qu'au
    /// contrôle final de l'obfuscation, sans qu'on sache où la trouver.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="fileName">Nom du fichier</param>
    /// <param name="applied">Portions qui seront remplacées</param>
    /// <param name="known">Recherche des valeurs connues du coffre</param>
    /// <param name="entities">Identifiants d'entités du texte, gardés en clair</param>
    /// <returns>Les valeurs connues laissées en clair</returns>
    private List<ResidualFinding> FindKnownInClear(
        string text,
        string fileName,
        IReadOnlyList<DetectedSpan> applied,
        KnownValueScanner known,
        EntityMask entities)
    {
        List<ResidualFinding> findings = [];
        TextLines? lines = null;
        int cursor = 0;

        foreach (KnownValueOccurrence occurrence in known.Scan(text))
        {
            // Les portions remplacées sont triées : on avance un curseur plutôt
            // que de toutes les parcourir pour chaque occurrence.
            while (cursor < applied.Count && applied[cursor].End <= occurrence.Start)
            {
                cursor++;
            }

            if (cursor < applied.Count && applied[cursor].Start < occurrence.Start + occurrence.Length)
            {
                continue;
            }

            string value = text.Substring(occurrence.Start, occurrence.Length);
            lines ??= new TextLines(text);
            int line = lines.LineOf(occurrence.Start);
            ObfuscationPolicy policy = _snapshot.Policy;

            // Un identifiant d'entité masqué autrefois en entier (la clé
            // « entity_id ») est désormais gardé en clair : ce n'est pas un
            // oubli des règles, sauf pour un terme demandé explicitement.
            if (policy.IsKeptEverywhere(value)
                || (entities.Covers(occurrence.Start, occurrence.Length) && !_snapshot.IsExplicitTerm(value))
                || !policy.ShouldObfuscate(DetectedSpan.Create(occurrence.Start, occurrence.Length, occurrence.Entry.Category, value, "known.clear")))
            {
                continue;
            }

            findings.Add(new ResidualFinding(
                "Valeur connue du coffre",
                occurrence.Start,
                occurrence.Length,
                line,
                value,
                SensitiveText.Mask(value),
                1.0,
                $"Cette valeur a déjà été masquée ({CategoryLabels.Label(occurrence.Entry.Category)}, pseudonyme {occurrence.Entry.Pseudonym}), mais vos règles actuelles ne la couvrent plus : elle partirait en clair.")
            {
                SuggestedCategory = occurrence.Entry.Category,
                IsExempted = policy.IsExceptedAt(fileName, line, text, occurrence.Start, value),
            });
        }

        return findings;
    }

    /// <summary>
    /// Lit les exceptions ponctuelles telles qu'elles sont enregistrées.
    /// </summary>
    /// <param name="settings">Exceptions sous leur forme persistée</param>
    /// <returns>Les exceptions lisibles</returns>
    private static List<LineExemption> ParseExemptions(IEnumerable<string> settings)
    {
        List<LineExemption> exemptions = [];
        foreach (string setting in settings)
        {
            if (LineExemption.TryParse(setting, out LineExemption exemption))
            {
                exemptions.Add(exemption);
            }
        }

        return exemptions;
    }

    /// <summary>
    /// Traduit un point d'avancement dans un fichier en part du lot entier.
    /// </summary>
    /// <param name="index">Rang du fichier</param>
    /// <param name="withinFile">Avancement dans le fichier, entre 0 et 1</param>
    /// <param name="count">Nombre de fichiers</param>
    /// <returns>La part accomplie du lot</returns>
    private static double Share(int index, double withinFile, int count)
    {
        return count == 0 ? 0 : FilesShare * (index + withinFile) / count;
    }
}
