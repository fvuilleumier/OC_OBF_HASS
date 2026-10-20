using System.Globalization;
using Spectre.Console;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Reporting;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View;

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

    /// <summary>
    /// Affiche le rapport d'un échange piloté — état, publication ou
    /// rapatriement — pour une lecture à l'écran. Il ne porte, comme sa forme
    /// JSON, aucune valeur réelle.
    /// </summary>
    /// <param name="report">Rapport à afficher</param>
    public static void WriteAutomation(AutomationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string error in report.Errors)
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", error.EscapeMarkup());
        }

        if (report.Project is not null)
        {
            AnsiConsole.MarkupLine("Projet : [bold]{0}[/]", report.Project.EscapeMarkup());
            AnsiConsole.MarkupLine("Source : {0}", (report.SourceFolder ?? string.Empty).EscapeMarkup());
            AnsiConsole.MarkupLine("Cible  : {0}", (report.TargetFolder ?? string.Empty).EscapeMarkup());
        }

        if (!report.Isolation.Safe && report.Errors.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Isolement non sûr :[/] {0}", report.Isolation.Message.EscapeMarkup());
        }

        if (report.Status is AutomationStatus status)
        {
            AnsiConsole.MarkupLine(
                "À rapatrier : {0} · à obfusquer de nouveau : {1} · dernière obfuscation : {2} · dernier rapatriement : {3}",
                status.TargetPending.ToString(CultureInfo.InvariantCulture),
                status.SourcePending.ToString(CultureInfo.InvariantCulture),
                (status.LastObfuscation ?? "jamais").EscapeMarkup(),
                (status.LastReturn ?? "jamais").EscapeMarkup());
        }
        else if (report.Errors.Count == 0)
        {
            AnsiConsole.MarkupLine(string.Join(" · ", report.Summary
                .Where(pair => pair.Value > 0)
                .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Value} {pair.Key}"))).EscapeMarkup());
        }

        WriteAutomationChanges(report);

        foreach (PendingDecision pending in report.Pending)
        {
            AnsiConsole.MarkupLine(
                "[yellow]À trancher[/] {0} « {1} » ({2} occ.) : {3}",
                pending.Kind.ToString().EscapeMarkup(),
                pending.Value.EscapeMarkup(),
                pending.Occurrences.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", pending.Files).EscapeMarkup());
        }

        foreach (AutomationConflict conflict in report.Conflicts)
        {
            AnsiConsole.MarkupLine(
                "[yellow]{0}[/] {1}{2}",
                conflict.Reason == AutomationConflictReason.Conflict ? "Conflit" : "Choix requis",
                conflict.Path.EscapeMarkup(),
                conflict.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({conflict.Count})") : string.Empty);
        }

        if (report.Outcome is AutomationOutcome outcome)
        {
            AnsiConsole.MarkupLine(
                "[green]Écrit :[/] {0} créé(s), {1} modifié(s), {2} retiré(s), {3} laissé(s) de côté.",
                outcome.Created.ToString(CultureInfo.InvariantCulture),
                outcome.Modified.ToString(CultureInfo.InvariantCulture),
                outcome.Deleted.ToString(CultureInfo.InvariantCulture),
                outcome.Skipped.Count.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.MarkupLine("Code de retour : [bold]{0}[/] — {1}", report.ExitCode.ToString(CultureInfo.InvariantCulture), AutomationVerdict(report).EscapeMarkup());
    }

    /// <summary>
    /// Affiche les fichiers d'un échange piloté, avec leurs alertes.
    /// </summary>
    /// <param name="report">Rapport</param>
    private static void WriteAutomationChanges(AutomationReport report)
    {
        if (report.Changes.Count == 0)
        {
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Action")
            .AddColumn("Fichier (cible)")
            .AddColumn(new TableColumn("+/-").RightAligned())
            .AddColumn("Alertes");

        foreach (AutomationChange change in report.Changes)
        {
            table.AddRow(
                change.Action.ToString().EscapeMarkup(),
                change.Path.EscapeMarkup(),
                string.Create(CultureInfo.InvariantCulture, $"+{change.Added} -{change.Removed}"),
                string.Join(Environment.NewLine, change.Warnings.Select(warning => Truncate(warning.Message, 90))).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Dit en clair ce que signifie le code d'un rapport.
    /// </summary>
    /// <param name="report">Rapport</param>
    /// <returns>Le verdict</returns>
    private static string AutomationVerdict(AutomationReport report)
    {
        return (AutomationExitCode)report.ExitCode switch
        {
            AutomationExitCode.Ok when report.Applied => "écrit.",
            AutomationExitCode.Ok when report.DryRun => "tout passerait ; rien n'a été écrit.",
            AutomationExitCode.Ok => "rien à faire.",
            AutomationExitCode.Error => "erreur technique.",
            AutomationExitCode.Blocked => "bloqué (fuite, isolement, fichier refusé ou bloqué) ; rien n'a été écrit.",
            AutomationExitCode.Pending => "des éléments sont à trancher dans l'application ; rien n'a été écrit.",
            _ => "des conflits ou des choix sont à trancher dans l'application ; rien n'a été écrit.",
        };
    }
}
