using System.Globalization;
using Spectre.Console;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Reporting;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View;

/// <summary>
/// Met en forme, pour le terminal, les comptes rendus produits par les
/// Presenters.
///
/// Toute la mise en forme est rassemblée ici afin que les commandes restent de
/// simples enchaînements « ouvrir le projet, appeler le Presenter, afficher ».
/// Une règle traverse ce fichier : les valeurs réelles ne s'affichent que sur
/// demande explicite, et ne sont jamais écrites sur disque.
/// </summary>
internal static class ConsoleRenderer
{
    /// <summary>
    /// Affiche le relevé des valeurs repérées, groupées par catégorie.
    /// </summary>
    /// <param name="report">Relevé à afficher</param>
    /// <param name="showValues">Vrai pour afficher les valeurs en clair</param>
    public static void WriteFindings(ScanReport report, bool showValues)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Findings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Aucune valeur sensible repérée.[/]");
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Catégorie")
            .AddColumn("Valeur")
            .AddColumn(new TableColumn("Occ.").RightAligned())
            .AddColumn("Fichiers")
            .AddColumn("Règle");

        foreach (ScanFinding finding in report.Findings)
        {
            table.AddRow(
                finding.Category.ToString().EscapeMarkup(),
                (showValues ? finding.Value : finding.MaskedValue).EscapeMarkup(),
                finding.Occurrences.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", finding.Files).EscapeMarkup(),
                finding.RuleId.EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Affiche les termes proposés pour le vocabulaire.
    /// </summary>
    /// <param name="report">Relevé contenant les candidats</param>
    public static void WriteCandidates(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Candidates.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Termes proposés pour le vocabulaire[/] (aucun n'est masqué tant que vous ne l'avez pas retenu)");

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Terme")
            .AddColumn(new TableColumn("Occ.").RightAligned())
            .AddColumn("Nature probable")
            .AddColumn("Exemple");

        foreach (var candidate in report.Candidates.Take(25))
        {
            table.AddRow(
                candidate.Text.EscapeMarkup(),
                candidate.Occurrences.ToString(CultureInfo.InvariantCulture),
                candidate.SuggestedCategory.ToString().EscapeMarkup(),
                Truncate(candidate.SampleLine, 60).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Affiche le compte rendu d'un lot obfusqué.
    /// </summary>
    /// <param name="report">Compte rendu du lot</param>
    public static void WriteObfuscationReport(ObfuscationBatchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Fichier")
            .AddColumn(new TableColumn("Remplacements").RightAligned())
            .AddColumn(new TableColumn("Suspects").RightAligned())
            .AddColumn("Vérification");

        foreach (ObfuscatedFile file in report.Files)
        {
            table.AddRow(
                Path.GetFileName(file.OutputPath).EscapeMarkup(),
                file.Replacements.ToString(CultureInfo.InvariantCulture),
                file.Suspicious.Count.ToString(CultureInfo.InvariantCulture),
                file.Residuals.Count == 0 ? "[green]propre[/]" : $"[red]{file.Residuals.Count} fuite(s)[/]");
        }

        AnsiConsole.Write(table);
        WriteSuspicious(report);

        AnsiConsole.WriteLine();
        if (report.IsClean)
        {
            AnsiConsole.MarkupLine(
                "[green]Vérification réussie[/] : aucune valeur masquée par vos règles ne subsiste dans les sorties.");
        }
        else
        {
            AnsiConsole.MarkupLine(report.Written
                ? "[red]Vérification en échec[/] : des valeurs masquées par vos règles subsistent. N'envoyez pas ces fichiers."
                : "[red]Vérification en échec[/] : des valeurs masquées par vos règles subsisteraient dans les sorties.");
        }

        // Une valeur masquée autrefois mais que les règles ne couvrent plus
        // n'est pas une fuite de l'outil ; elle mérite néanmoins d'être dite.
        foreach (ObfuscatedFile file in report.Files)
        {
            foreach (ResidualLiteral known in file.KnownInClear)
            {
                AnsiConsole.MarkupLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[yellow]En clair[/] {Path.GetFileName(file.OutputPath).EscapeMarkup()} : valeur connue du coffre ({known.Pseudonym.EscapeMarkup()}) ×{known.Occurrences}, lignes {string.Join(", ", known.Lines.Take(8))} — vos règles ne la couvrent plus."));
            }
        }

        AnsiConsole.MarkupLine(
            "[grey]Cet outil réduit le risque de fuite, il ne le supprime pas : relisez le fichier avant de l'envoyer.[/]");
    }

    /// <summary>
    /// Affiche les éléments suspects laissés en clair.
    /// </summary>
    /// <param name="report">Compte rendu du lot</param>
    private static void WriteSuspicious(ObfuscationBatchReport report)
    {
        List<(string File, ResidualFinding Finding)> suspicious =
        [
            .. report.Files.SelectMany(file =>
                file.Suspicious.Select(finding => (Path.GetFileName(file.SourcePath), finding))),
        ];

        if (suspicious.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Éléments suspects non masqués[/]");

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Fichier")
            .AddColumn(new TableColumn("Ligne").RightAligned())
            .AddColumn("Nature")
            .AddColumn("Extrait")
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach ((string file, ResidualFinding finding) in suspicious.Take(30))
        {
            table.AddRow(
                file.EscapeMarkup(),
                finding.Line.ToString(CultureInfo.InvariantCulture),
                finding.Kind.EscapeMarkup(),
                finding.MaskedSample.EscapeMarkup(),
                finding.Score.ToString("0.00", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Affiche le compte rendu d'une restitution.
    /// </summary>
    /// <param name="report">Compte rendu de la restitution</param>
    public static void WriteDeobfuscationReport(DeobfuscationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        AnsiConsole.MarkupLine(
            "{0} pseudonyme(s) restitué(s).",
            report.Substitutions.Count.ToString(CultureInfo.InvariantCulture));

        if (report.UnknownTokens.Count == 0)
        {
            return;
        }

        // Un jeton inconnu n'est pas une erreur : un modèle à qui l'on demande
        // d'imaginer de nouvelles machines en invente légitimement.
        AnsiConsole.MarkupLine(
            "[yellow]{0} jeton(s) inconnu(s) du coffre, laissés en place :[/]",
            report.UnknownTokens.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var token in report.UnknownTokens.Take(10))
        {
            AnsiConsole.MarkupLine("  {0}", token.Canonical.EscapeMarkup());
        }
    }

    /// <summary>
    /// Raccourcit un texte pour l'affichage en tableau.
    /// </summary>
    /// <param name="value">Texte à raccourcir</param>
    /// <param name="maximum">Longueur maximale</param>
    /// <returns>Le texte, éventuellement suivi d'une ellipse</returns>
    private static string Truncate(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }
}
