using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View.Commands;

/// <summary>
/// Écran de revue : l'utilisateur coche ce qu'il veut réellement masquer.
///
/// C'est le moment où l'automatisme rend la main. Dans une configuration
/// réseau, on veut souvent conserver le plan d'adressage ; dans une note
/// interne, on veut au contraire tout masquer. Aucune heuristique ne peut
/// trancher à la place de la personne qui connaît le fichier.
/// </summary>
internal sealed class ReviewCommand : Command<ReviewCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande de revue.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Fichier ou dossier sur lequel porte la revue.</summary>
        [CommandArgument(0, "[chemin]")]
        [Description("Fichier ou dossier à relever. Dossier « in » du projet par défaut.")]
        public string? Path { get; init; }
    }

    /// <summary>
    /// Exécute la revue interactive.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie du processus</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject project = ProjectLocator.Open(settings.Path);
        string target = settings.Path ?? project.InputPath;

        IReadOnlyList<string> files = new TextFileCollector { SkipProducedFiles = true }.Collect(target);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Aucun fichier texte à relever.[/]");
            return 0;
        }

        ScanReport report = new ScanPresenter(project).Scan(files);
        if (report.Findings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Aucune valeur sensible repérée : rien à arbitrer.[/]");
            return 0;
        }

        ReviewPresenter presenter = new(project);
        ReviewCategories(report, presenter);
        ReviewValues(report, presenter);
        ReviewCandidates(report, presenter);
        presenter.Commit();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Décisions enregistrées[/] dans {0}", project.SettingsPath.EscapeMarkup());
        AnsiConsole.MarkupLine("Lancez « tebiobf obfuscate » pour produire les fichiers.");

        return 0;
    }

    /// <summary>
    /// Fait cocher les catégories à masquer.
    /// </summary>
    /// <param name="report">Relevé servant de base</param>
    /// <param name="presenter">Presenter recevant les décisions</param>
    private static void ReviewCategories(ScanReport report, ReviewPresenter presenter)
    {
        Dictionary<SpanCategory, int> counts = report.Findings
            .GroupBy(finding => finding.Category)
            .ToDictionary(group => group.Key, group => group.Sum(finding => finding.Occurrences));

        MultiSelectionPrompt<SpanCategory> prompt = new MultiSelectionPrompt<SpanCategory>()
            .Title("Quelles [green]catégories[/] faut-il masquer ?")
            .NotRequired()
            .PageSize(20)
            .InstructionsText("[grey](espace pour cocher, entrée pour valider)[/]")
            .UseConverter(category =>
                $"{category} ({counts[category].ToString(CultureInfo.InvariantCulture)} occurrences)");

        foreach (SpanCategory category in counts.Keys.OrderBy(category => category.ToString(), StringComparer.Ordinal))
        {
            prompt.AddChoice(category);
        }

        // Tout est coché au départ : la position par défaut doit être la plus
        // protectrice, l'utilisateur décoche ce qu'il veut conserver.
        foreach (SpanCategory category in counts.Keys)
        {
            prompt.Select(category);
        }

        HashSet<SpanCategory> kept = [.. AnsiConsole.Prompt(prompt)];
        foreach (SpanCategory category in counts.Keys)
        {
            if (kept.Contains(category))
            {
                presenter.EnableCategory(category);
            }
            else
            {
                presenter.DisableCategory(category);
            }
        }
    }

    /// <summary>
    /// Fait décocher les valeurs particulières à conserver en clair.
    /// </summary>
    /// <param name="report">Relevé servant de base</param>
    /// <param name="presenter">Presenter recevant les décisions</param>
    private static void ReviewValues(ScanReport report, ReviewPresenter presenter)
    {
        if (!AnsiConsole.Confirm("Souhaitez-vous conserver certaines valeurs en clair ?", defaultValue: false))
        {
            return;
        }

        MultiSelectionPrompt<string> prompt = new MultiSelectionPrompt<string>()
            .Title("Cochez les valeurs à [yellow]conserver en clair[/]")
            .NotRequired()
            .PageSize(20)
            .InstructionsText("[grey](espace pour cocher, entrée pour valider)[/]");

        foreach (ScanFinding finding in report.Findings.Take(100))
        {
            prompt.AddChoice($"{finding.Category} · {finding.Value}");
        }

        foreach (string choice in AnsiConsole.Prompt(prompt))
        {
            int separator = choice.IndexOf(" · ", StringComparison.Ordinal);
            presenter.KeepValue(choice[(separator + 3)..]);
        }
    }

    /// <summary>
    /// Fait cocher les termes à promouvoir dans le vocabulaire.
    /// </summary>
    /// <param name="report">Relevé contenant les candidats</param>
    /// <param name="presenter">Presenter recevant les décisions</param>
    private static void ReviewCandidates(ScanReport report, ReviewPresenter presenter)
    {
        if (report.Candidates.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        ConsoleRenderer.WriteCandidates(report);

        if (!AnsiConsole.Confirm("Certains de ces termes doivent-ils être masqués à l'avenir ?", defaultValue: false))
        {
            return;
        }

        MultiSelectionPrompt<string> prompt = new MultiSelectionPrompt<string>()
            .Title("Cochez les termes à ajouter au [green]vocabulaire[/]")
            .NotRequired()
            .PageSize(20)
            .InstructionsText("[grey](espace pour cocher, entrée pour valider)[/]");

        Dictionary<string, SpanCategory> suggestions = [];
        foreach (VocabularyCandidate candidate in report.Candidates.Take(50))
        {
            suggestions[candidate.Text] = candidate.SuggestedCategory;
            prompt.AddChoice(candidate.Text);
        }

        foreach (string term in AnsiConsole.Prompt(prompt))
        {
            presenter.PromoteToVocabulary(term, suggestions[term]);
        }
    }
}
