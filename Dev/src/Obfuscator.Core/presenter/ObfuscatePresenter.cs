using Obfuscator.Core.Model.Documents;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Compte rendu de l'obfuscation d'un fichier.
/// </summary>
/// <param name="SourcePath">Fichier d'origine</param>
/// <param name="OutputPath">Fichier obfusqué produit</param>
/// <param name="LegendPath">Légende produite, le cas échéant</param>
/// <param name="Replacements">Nombre de valeurs remplacées</param>
/// <param name="Residuals">
/// Fuites : valeurs que les règles actuelles masquent, retrouvées malgré tout
/// dans la sortie. Elles signalent un défaut et interdisent l'envoi.
/// </param>
/// <param name="Suspicious">Éléments suspects laissés en clair, à examiner</param>
/// <param name="Kind">Format deviné du fichier</param>
/// <param name="EncodingName">Encodage détecté, restitué à l'écriture</param>
/// <param name="Applied">Spans remplacés, pour le détail technique</param>
/// <param name="Evictions">Arbitrages entre règles concurrentes</param>
/// <param name="KnownInClear">
/// Valeurs connues du coffre — masquées lors d'une exécution précédente — que
/// les règles actuelles ne couvrent plus et qui restent donc en clair. Ce n'est
/// pas un défaut de l'outil, mais cela mérite d'être su.
/// </param>
public sealed record ObfuscatedFile(
    string SourcePath,
    string OutputPath,
    string? LegendPath,
    int Replacements,
    IReadOnlyList<ResidualLiteral> Residuals,
    IReadOnlyList<ResidualFinding> Suspicious,
    FileKind Kind,
    string EncodingName,
    IReadOnlyList<DetectedSpan> Applied,
    IReadOnlyList<SpanEviction> Evictions,
    IReadOnlyList<ResidualLiteral> KnownInClear)
{
    /// <summary>
    /// Fuites dans le nom du fichier ou de ses dossiers : une valeur masquée
    /// dans le contenu, restée en clair dans le chemin. Elles interdisent
    /// l'envoi comme celles du contenu.
    /// </summary>
    public IReadOnlyList<ResidualLiteral> NameLeaks { get; init; } = [];
}

/// <summary>
/// Compte rendu d'un lot.
/// </summary>
/// <param name="Files">Comptes rendus par fichier</param>
/// <param name="TotalReplacements">Nombre total de valeurs remplacées</param>
/// <param name="IsClean">Vrai si aucune fuite ne subsiste dans les sorties</param>
public sealed record ObfuscationBatchReport(
    IReadOnlyList<ObfuscatedFile> Files,
    int TotalReplacements,
    bool IsClean)
{
    /// <summary>Nombre d'occurrences de valeurs connues laissées en clair par les règles actuelles.</summary>
    public int KnownInClearCount => Files.Sum(file => file.KnownInClear.Sum(value => value.Occurrences));

    /// <summary>
    /// Vrai si les sorties ont été écrites. Une fuite interdit toute écriture :
    /// un fichier qui fuit ne doit pas pouvoir être envoyé par mégarde.
    /// </summary>
    public bool Written { get; init; } = true;
}

/// <summary>
/// Fichier d'un lot une fois obfusqué en mémoire, contrôle compris, et pas
/// encore écrit.
/// </summary>
/// <param name="Item">Fichier du lot</param>
/// <param name="Document">Document source, avec son encodage</param>
/// <param name="Content">Contenu obfusqué</param>
/// <param name="Name">Chemin obfusqué</param>
/// <param name="Suspicious">Éléments suspects laissés en clair, à examiner</param>
/// <param name="Leaks">Fuites dans le contenu</param>
/// <param name="KnownInClear">Valeurs connues laissées en clair par les règles actuelles</param>
/// <param name="NameLeaks">Fuites dans le chemin</param>
/// <param name="Kind">Format deviné</param>
public sealed record PreparedFile(
    BatchItem Item,
    SourceDocument Document,
    ObfuscationResult Content,
    RewrittenPath Name,
    IReadOnlyList<ResidualFinding> Suspicious,
    IReadOnlyList<ResidualLiteral> Leaks,
    IReadOnlyList<ResidualLiteral> KnownInClear,
    IReadOnlyList<ResidualLiteral> NameLeaks,
    FileKind Kind)
{
    /// <summary>Vrai si ni le contenu ni le chemin ne fuient.</summary>
    public bool IsClean => Leaks.Count == 0 && NameLeaks.Count == 0;

    /// <summary>Octets à écrire : le contenu obfusqué, dans l'encodage d'origine.</summary>
    /// <returns>Les octets</returns>
    public byte[] OutputBytes() => Document.ToBytes(Content.Text);
}

/// <summary>
/// Lot obfusqué en mémoire, prêt à être écrit.
/// </summary>
/// <param name="Files">Fichiers, dans l'ordre du lot</param>
/// <param name="Delta">Ce que le traitement a ajouté au coffre sur lequel il a tourné</param>
public sealed record PreparedBatch(IReadOnlyList<PreparedFile> Files, VaultDelta Delta)
{
    /// <summary>Vrai si aucun fichier ne fuit.</summary>
    public bool IsClean => Files.All(file => file.IsClean);
}

/// <summary>
/// Résultat de l'obfuscation d'un simple texte, par exemple le presse-papiers.
/// </summary>
/// <param name="Text">Texte pseudonymisé</param>
/// <param name="Applied">Valeurs remplacées, pour dire lesquelles l'ont été</param>
/// <param name="Leaks">Fuites retrouvées dans le texte produit</param>
/// <param name="KnownInClear">Valeurs connues laissées en clair par les règles actuelles</param>
/// <param name="Candidates">
/// Termes restés en clair qui ressemblent à des noms propres ou à des
/// identifiants internes : aucune règle ne les couvre, seul l'utilisateur peut
/// dire s'il faut les masquer.
/// </param>
public sealed record TextObfuscation(
    string Text,
    IReadOnlyList<DetectedSpan> Applied,
    IReadOnlyList<ResidualLiteral> Leaks,
    IReadOnlyList<ResidualLiteral> KnownInClear,
    IReadOnlyList<VocabularyCandidate> Candidates)
{
    /// <summary>Nombre de valeurs remplacées.</summary>
    public int Replacements => Applied.Count;

    /// <summary>Vrai si aucune fuite ne subsiste.</summary>
    public bool IsClean => Leaks.Count == 0;
}

/// <summary>
/// Orchestre l'obfuscation d'un lot de fichiers.
///
/// Trois choses sont faites systématiquement et non sur option : le contrôle de
/// vérification, qui rattrape les erreurs d'offset en cherchant dans la sortie
/// les valeurs réelles du coffre ; le relevé des résidus, qui signale ce
/// qu'aucune règle n'a reconnu ; et l'enregistrement du coffre, sans lequel la
/// restitution serait impossible. Rendre l'un de ces trois facultatifs
/// reviendrait à proposer un mode où l'outil ment sur ce qu'il garantit.
///
/// Le contrôle distingue trois cas parmi les valeurs connues retrouvées en
/// clair. Une valeur que l'utilisateur a choisi de laisser en clair — à un
/// endroit précis, ou en l'écartant, ou par une catégorie désactivée — n'est
/// rien de plus que ce choix. Une valeur que les règles actuelles masquent est
/// une fuite : un détecteur l'a manquée quelque part, l'envoi doit être bloqué.
/// Une valeur masquée autrefois mais que les règles actuelles ne couvrent plus
/// — un terme retiré du vocabulaire — est signalée sans bloquer : c'est peut-être
/// voulu, peut-être un oubli, et seul l'utilisateur peut trancher.
/// </summary>
/// <param name="project">Projet à utiliser</param>
public sealed class ObfuscatePresenter(ObfuscationProject project)
{
    /// <summary>
    /// Obfusque un ensemble de fichiers vers un dossier de sortie.
    /// </summary>
    /// <param name="filePaths">Fichiers à traiter</param>
    /// <param name="sourceRoot">Racine des chemins d'entrée, pour reproduire l'arborescence</param>
    /// <param name="outputDirectory">Dossier de sortie</param>
    /// <returns>Le compte rendu du lot</returns>
    public ObfuscationBatchReport Run(IEnumerable<string> filePaths, string sourceRoot, string outputDirectory)
    {
        return Run(filePaths, sourceRoot, outputDirectory, null);
    }

    /// <summary>
    /// Obfusque un ensemble de fichiers vers un dossier de sortie, en rendant
    /// compte de l'avancement.
    ///
    /// Le lot est d'abord obfusqué et contrôlé en mémoire. S'il fuit, rien
    /// n'est écrit — ni les sorties, ni le coffre : un fichier qui fuit ne doit
    /// pas pouvoir partir par mégarde. Sinon, le coffre est enregistré avant
    /// les sorties : une interruption ne laisse jamais de sortie dont les
    /// pseudonymes manqueraient au coffre.
    /// </summary>
    /// <param name="filePaths">Fichiers à traiter</param>
    /// <param name="sourceRoot">Racine des chemins d'entrée, pour reproduire l'arborescence</param>
    /// <param name="outputDirectory">Dossier de sortie</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le compte rendu du lot</returns>
    public ObfuscationBatchReport Run(
        IEnumerable<string> filePaths,
        string sourceRoot,
        string outputDirectory,
        IProgress<BatchProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        PreparedBatch prepared = Prepare([.. filePaths.Select(path => BatchItem.Of(path, sourceRoot))], progress);
        if (!prepared.IsClean)
        {
            return Report(prepared, outputDirectory, written: false);
        }

        progress?.Report(new BatchProgress(prepared.Files.Count, prepared.Files.Count, string.Empty, "enregistrement du coffre", 1.0));
        project.Save();
        WriteOutputs(prepared, outputDirectory);

        return Report(prepared, outputDirectory, written: true);
    }

    /// <summary>
    /// Obfusque un lot en mémoire, contrôle final compris, sans rien écrire ni
    /// enregistrer. Sur un instantané du projet, le coffre réel n'est pas
    /// touché : ce que le lot y ajouterait se lit dans
    /// <see cref="PreparedBatch.Delta"/>.
    /// </summary>
    /// <param name="items">Fichiers du lot, dans l'ordre de traitement</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le lot obfusqué</returns>
    public PreparedBatch Prepare(IReadOnlyList<BatchItem> items, IProgress<BatchProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(items);

        VaultBaseline baseline = project.Store.CaptureBaseline();
        ObfuscationEngine engine = project.CreateObfuscationEngine();
        int count = items.Count;

        List<RewrittenFile> rewritten = BatchRewriter.Run(
            engine,
            items,
            simulate: false,
            (index, step) => progress?.Report(new BatchProgress(index, count, Path.GetFileName(items[index].Path), step, (double)index / Math.Max(1, count))),
            CancellationToken.None);

        // Le contrôle est mené une fois tout le lot traité : une valeur masquée
        // dans un fichier et restée en clair dans un autre — dans son contenu
        // ou dans son nom — est une fuite.
        HashSet<(SpanCategory, string)> masked = [];
        foreach (RewrittenFile file in rewritten)
        {
            masked.UnionWith(MaskedValues(file.Content.Applied));
            masked.UnionWith(MaskedValues(file.Name.Applied));
        }

        List<PreparedFile> files = new(count);
        foreach (RewrittenFile file in rewritten)
        {
            string text = file.Document.Text;
            EntityMask entities = project.Policy.Entities.Find(text);
            IReadOnlyList<ResidualFinding> suspicious = project.Settings.EmitResidualReport
                ? [.. ResidualScanner.Scan(text, file.Content.Applied).Where(finding => !entities.Covers(finding.Start, finding.Length))]
                : [];

            (List<ResidualLiteral> leaks, List<ResidualLiteral> known) = Classify(file.Content, text, file.Item.Key, masked);
            List<ResidualLiteral> nameLeaks = ClassifyName(file.Name, masked);

            files.Add(new PreparedFile(
                file.Item,
                file.Document,
                file.Content,
                file.Name,
                suspicious,
                leaks,
                known,
                nameLeaks,
                FormatSniffer.Detect(file.Item.Key, text)));
        }

        return new PreparedBatch(files, project.Store.DeltaSince(baseline));
    }

    /// <summary>
    /// Écrit un lot obfusqué dans un dossier « out » : chaque fichier sous son
    /// chemin pseudonymisé, suffixé « _obfusc », accompagné de sa légende.
    /// Chaque écriture est atomique.
    /// </summary>
    /// <param name="prepared">Lot obfusqué, sans fuite</param>
    /// <param name="outputDirectory">Dossier de sortie</param>
    /// <exception cref="InvalidOperationException">Si le lot fuit</exception>
    public void WriteOutputs(PreparedBatch prepared, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!prepared.IsClean)
        {
            throw new InvalidOperationException("Un lot qui fuit ne s'écrit pas.");
        }

        foreach (PreparedFile file in prepared.Files)
        {
            string outputPath = OutputPathOf(file, outputDirectory);

            // L'encodage et la marque d'ordre des octets d'origine sont
            // restitués : la sortie ne doit différer de l'entrée que sur les
            // valeurs remplacées.
            AtomicFile.WriteAllBytes(outputPath, file.OutputBytes());

            // La légende nomme le fichier que l'IA reçoit, pas l'original.
            if (project.Settings.EmitLegend)
            {
                AtomicFile.WriteAllText(LegendPathOf(outputPath), LegendWriter.Create(project, file.Content.Applied, Path.GetFileName(outputPath)));
            }
        }
    }

    /// <summary>
    /// Donne le chemin d'un fichier obfusqué dans un dossier « out » : son
    /// chemin pseudonymisé, le nom suffixé « _obfusc ».
    /// </summary>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="outputDirectory">Dossier de sortie</param>
    /// <returns>Le chemin absolu de la sortie</returns>
    public static string OutputPathOf(PreparedFile file, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(file);
        return RelativePaths.Resolve(outputDirectory, OutputRelativePath(file.Name.RelativePath));
    }

    /// <summary>
    /// Donne le chemin relatif d'une sortie dans « out » à partir du chemin
    /// pseudonymisé : seul le nom du fichier reçoit le suffixe.
    /// </summary>
    /// <param name="relative">Chemin pseudonymisé, avec des barres obliques</param>
    /// <returns>Le chemin de la sortie, avec des barres obliques</returns>
    public static string OutputRelativePath(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        int slash = relative.LastIndexOf('/');
        return slash < 0
            ? OutputNaming.ObfuscatedName(relative)
            : relative[..(slash + 1)] + OutputNaming.ObfuscatedName(relative[(slash + 1)..]);
    }

    /// <summary>
    /// Compose le compte rendu d'un lot.
    /// </summary>
    /// <param name="prepared">Lot obfusqué</param>
    /// <param name="outputDirectory">Dossier de sortie</param>
    /// <param name="written">Vrai si les sorties ont été écrites</param>
    /// <returns>Le compte rendu</returns>
    private ObfuscationBatchReport Report(PreparedBatch prepared, string outputDirectory, bool written)
    {
        List<ObfuscatedFile> files = [];
        foreach (PreparedFile file in prepared.Files)
        {
            string outputPath = OutputPathOf(file, outputDirectory);
            files.Add(new ObfuscatedFile(
                file.Item.Path,
                outputPath,
                written && project.Settings.EmitLegend ? LegendPathOf(outputPath) : null,
                file.Content.Applied.Count,
                file.Leaks,
                file.Suspicious,
                file.Kind,
                file.Document.EncodingName,
                file.Content.Applied,
                file.Content.Evictions,
                file.KnownInClear)
            {
                NameLeaks = file.NameLeaks,
            });
        }

        return new ObfuscationBatchReport(files, files.Sum(file => file.Replacements), prepared.IsClean)
        {
            Written = written,
        };
    }

    /// <summary>
    /// Pseudonymise un texte isolé, par exemple le contenu du presse-papiers.
    ///
    /// Les pseudonymes attribués sont enregistrés dans le coffre avant que le
    /// texte ne soit rendu : un texte envoyé à un modèle sans que ses
    /// attributions soient conservées produirait une réponse impossible à
    /// restituer.
    /// </summary>
    /// <param name="text">Texte à pseudonymiser</param>
    /// <returns>Le texte pseudonymisé et le résultat du contrôle</returns>
    public TextObfuscation RunOnText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        ObfuscationResult result = project.CreateObfuscationEngine().Obfuscate(text);
        project.Save();

        (List<ResidualLiteral> leaks, List<ResidualLiteral> known) =
            Classify(result, text, string.Empty, [.. MaskedValues(result.Applied)]);

        // Les candidats sont cherchés dans le texte produit : ce qui a été
        // masqué n'y figure plus, seul reste ce qu'aucune règle n'a couvert.
        List<VocabularyCandidate> candidates = [.. VocabularyCandidateFinder
            .Find([project.Policy.Entities.Blank(result.Text)], project.Vocabulary, project.Policy.Allowlist)
            .Where(candidate => !project.Policy.IsIgnored(candidate.Text))];

        return new TextObfuscation(result.Text, result.Applied, leaks, known, candidates);
    }

    /// <summary>
    /// Relève les fuites d'un chemin : une valeur que les règles masquent,
    /// restée en clair dans le nom du fichier ou de l'un de ses dossiers. Un
    /// nom n'a pas de ligne : seules la liste blanche et les catégories
    /// désactivées peuvent y laisser une valeur en clair.
    /// </summary>
    /// <param name="name">Chemin réécrit</param>
    /// <param name="masked">Valeurs masquées dans le lot, sous forme canonique</param>
    /// <returns>Les fuites du chemin</returns>
    private List<ResidualLiteral> ClassifyName(RewrittenPath name, HashSet<(SpanCategory, string)> masked)
    {
        List<ResidualLiteral> leaks = [];
        HashSet<string> foldedTerms = [.. project.Vocabulary.Terms.Select(term => TextFolding.Fold(term.Text))];

        foreach (ObfuscationResult segment in name.Segments)
        {
            EntityMask entities = project.Policy.Entities.Find(segment.Text);
            foreach (ResidualLiteral residual in segment.Residuals)
            {
                List<(int Start, int Length)> leaked = [];
                foreach ((int start, int length) in residual.Locations)
                {
                    string value = segment.Text.Substring(start, length);
                    bool explicitTerm = foldedTerms.Contains(TextFolding.Fold(value))
                        || project.Settings.AlwaysObfuscate.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

                    if (IsDeliberatelyClear(residual.Category, value, string.Empty, string.Empty, 0, 0)
                        || (!explicitTerm && entities.Covers(start, length)))
                    {
                        continue;
                    }

                    if (explicitTerm || masked.Contains((residual.Category, ValueCanonicalizer.Canonicalize(residual.Category, value))))
                    {
                        leaked.Add((start, length));
                    }
                }

                if (leaked.Count > 0)
                {
                    leaks.Add(residual with { FirstIndex = leaked[0].Start, Occurrences = leaked.Count, Locations = leaked });
                }
            }
        }

        return leaks;
    }

    /// <summary>
    /// Sépare, parmi les valeurs connues retrouvées dans une sortie, les fuites
    /// des valeurs laissées en clair par les règles actuelles, et écarte les
    /// choix explicites de l'utilisateur.
    ///
    /// Une exception ponctuelle désigne une occurrence du texte source : sa
    /// ligne et son rang sur la ligne. Chaque occurrence retrouvée dans la
    /// sortie est donc ramenée à sa place dans le source avant d'être comparée
    /// aux exceptions — dans la sortie, les autres occurrences de la ligne ont
    /// été remplacées, et le rang n'y serait plus le même.
    /// </summary>
    /// <param name="result">Résultat de l'obfuscation, valeurs retrouvées comprises</param>
    /// <param name="source">Texte source</param>
    /// <param name="fileName">Nom du fichier, pour les exceptions ponctuelles</param>
    /// <param name="masked">Valeurs masquées dans le lot, sous forme canonique</param>
    /// <returns>Les fuites et les valeurs connues laissées en clair</returns>
    private (List<ResidualLiteral> Leaks, List<ResidualLiteral> KnownInClear) Classify(
        ObfuscationResult result,
        string source,
        string fileName,
        HashSet<(SpanCategory, string)> masked)
    {
        List<ResidualLiteral> leaks = [];
        List<ResidualLiteral> known = [];
        if (result.Residuals.Count == 0)
        {
            return (leaks, known);
        }

        string output = result.Text;
        TextLines sourceLines = new(source);
        HashSet<string> foldedTerms = [.. project.Vocabulary.Terms.Select(term => TextFolding.Fold(term.Text))];
        EntityMask entities = project.Policy.Entities.Find(source);

        // Les numéros de ligne rapportés sont ceux du fichier d'origine : c'est
        // là qu'on va chercher la valeur.
        int SourceLineOf((int Start, int Length) location) => sourceLines.LineOf(result.SourcePositionOf(location.Start));

        foreach (ResidualLiteral residual in result.Residuals)
        {
            List<(int Start, int Length)> leaked = [];
            List<(int Start, int Length)> kept = [];

            foreach ((int start, int length) in residual.Locations)
            {
                string value = output.Substring(start, length);
                int sourceStart = result.SourcePositionOf(start);
                bool explicitTerm = foldedTerms.Contains(TextFolding.Fold(value))
                    || project.Settings.AlwaysObfuscate.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

                // Un identifiant d'entité gardé en clair n'est ni une fuite ni
                // un oubli des règles, sauf pour un terme demandé explicitement.
                if (IsDeliberatelyClear(residual.Category, value, fileName, source, sourceLines.LineOf(sourceStart), sourceStart)
                    || (!explicitTerm && entities.Covers(sourceStart, length)))
                {
                    continue;
                }

                bool covered = explicitTerm || masked.Contains((residual.Category, ValueCanonicalizer.Canonicalize(residual.Category, value)));

                (covered ? leaked : kept).Add((start, length));
            }

            if (leaked.Count > 0)
            {
                leaks.Add(residual with
                {
                    FirstIndex = leaked[0].Start,
                    Occurrences = leaked.Count,
                    Locations = leaked,
                    Lines = [.. leaked.Select(SourceLineOf).Distinct()],
                });
            }

            if (kept.Count > 0)
            {
                known.Add(residual with
                {
                    FirstIndex = kept[0].Start,
                    Occurrences = kept.Count,
                    Locations = kept,
                    Lines = [.. kept.Select(SourceLineOf).Distinct()],
                });
            }
        }

        return (leaks, known);
    }

    /// <summary>
    /// Indique si une valeur retrouvée en clair l'est par un choix explicite de
    /// l'utilisateur : occurrence gardée à cet endroit, valeur de la liste
    /// blanche, catégorie désactivée.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <param name="value">Valeur retrouvée</param>
    /// <param name="fileName">Nom du fichier</param>
    /// <param name="source">Texte source</param>
    /// <param name="line">Numéro de la ligne du source où elle figure</param>
    /// <param name="position">Position de l'occurrence dans le source</param>
    /// <returns><c>true</c> si la présence en clair est voulue</returns>
    private bool IsDeliberatelyClear(SpanCategory category, string value, string fileName, string source, int line, int position)
    {
        ObfuscationPolicy policy = project.Policy;
        return (fileName.Length > 0 && policy.IsExceptedAt(fileName, line, source, position, value))
            || policy.IsKeptEverywhere(value)
            || !policy.ShouldObfuscate(DetectedSpan.Create(0, value.Length, category, value, "verify.known"));
    }

    /// <summary>
    /// Donne la forme canonique des valeurs remplacées.
    /// </summary>
    /// <param name="applied">Spans remplacés</param>
    /// <returns>Les couples nature et valeur canonique</returns>
    private static IEnumerable<(SpanCategory, string)> MaskedValues(IEnumerable<DetectedSpan> applied)
    {
        return applied.Select(span => (span.Category, ValueCanonicalizer.Canonicalize(span.Category, span.RawValue)));
    }

    /// <summary>
    /// Compose le chemin de la légende d'une sortie.
    /// </summary>
    /// <param name="outputPath">Chemin du fichier obfusqué</param>
    /// <returns>Le chemin de sa légende</returns>
    private static string LegendPathOf(string outputPath) => OutputNaming.LegendPath(outputPath);
}
