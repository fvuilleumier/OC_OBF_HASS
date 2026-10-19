using System.Text.Json;
using System.Text.Json.Serialization;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Model.Reporting;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Valeur distincte repérée dans un lot, telle qu'elle est présentée à la revue.
/// </summary>
/// <param name="Category">Nature de la valeur</param>
/// <param name="Value">Valeur réelle, destinée à l'affichage interactif</param>
/// <param name="MaskedValue">Valeur masquée, destinée aux rapports écrits sur disque</param>
/// <param name="Occurrences">Nombre total d'occurrences dans le lot</param>
/// <param name="Files">Fichiers où la valeur apparaît</param>
/// <param name="RuleId">Règle l'ayant repérée</param>
public sealed record ScanFinding(
    SpanCategory Category,
    string Value,
    string MaskedValue,
    int Occurrences,
    IReadOnlyList<string> Files,
    string RuleId);

/// <summary>
/// Relevé complet d'un lot de fichiers.
/// </summary>
/// <param name="Findings">Valeurs distinctes repérées, de la plus fréquente à la moins fréquente</param>
/// <param name="Candidates">Termes proposés pour enrichir le vocabulaire</param>
/// <param name="FileCount">Nombre de fichiers analysés</param>
public sealed record ScanReport(
    IReadOnlyList<ScanFinding> Findings,
    IReadOnlyList<VocabularyCandidate> Candidates,
    int FileCount);

/// <summary>
/// Relève ce que l'outil masquerait, sans rien écrire.
///
/// Cette étape existe pour une raison simple : régler des règles de détection à
/// l'aveugle est impraticable. Le relevé permet de voir exactement ce qui
/// partirait, de décocher ce qui doit rester, et de promouvoir dans le
/// vocabulaire ce qui a échappé aux règles — le tout sans produire un seul
/// fichier ni consommer un seul pseudonyme.
/// </summary>
/// <param name="project">Projet à analyser</param>
public sealed class ScanPresenter(ObfuscationProject project)
{
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Analyse un ensemble de fichiers.
    /// </summary>
    /// <param name="filePaths">Fichiers à analyser</param>
    /// <returns>Le relevé des valeurs repérées et des termes candidats</returns>
    public ScanReport Scan(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        DetectionPipeline pipeline = new(project.Format, project.CreateDetectors(), project.Policy);
        FindingAggregator findings = new();
        List<string> texts = [];
        int fileCount = 0;

        foreach (string path in filePaths)
        {
            SourceDocument document = SourceDocument.Load(File.ReadAllBytes(path));
            texts.Add(project.Policy.Entities.Blank(document.Text));
            fileCount++;

            string fileName = Path.GetFileName(path);
            findings.Add(pipeline.Run(document.Text, fileName).Replacements, fileName);
        }

        IReadOnlyList<VocabularyCandidate> candidates =
            [.. VocabularyCandidateFinder
                .Find(texts, project.Vocabulary, project.Policy.Allowlist)
                .Where(candidate => !project.Policy.IsIgnored(candidate.Text))];

        return new ScanReport(findings.ToFindings(), candidates, fileCount);
    }

    /// <summary>
    /// Écrit le relevé sur disque, valeurs masquées.
    ///
    /// Le fichier est destiné à être relu, versionné ou transmis : il ne doit
    /// donc contenir aucune valeur réelle, alors même que l'écran de revue, lui,
    /// les affiche en clair — c'est sa raison d'être.
    /// </summary>
    /// <param name="report">Relevé à enregistrer</param>
    public void SaveReport(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            fileCount = report.FileCount,
            findings = report.Findings.Select(finding => new
            {
                category = finding.Category,
                value = finding.MaskedValue,
                occurrences = finding.Occurrences,
                files = finding.Files,
                rule = finding.RuleId,
            }),
            vocabularyCandidates = report.Candidates.Select(candidate => new
            {
                term = candidate.Text,
                occurrences = candidate.Occurrences,
                suggestedCategory = candidate.SuggestedCategory,
            }),
        };

        Directory.CreateDirectory(project.MetadataPath);
        File.WriteAllText(project.FindingsPath, JsonSerializer.Serialize(document, ReportOptions));
    }
}
