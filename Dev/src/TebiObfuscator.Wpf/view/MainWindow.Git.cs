using System.Windows;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Commit et push des dossiers liés, depuis l'onglet « Échanges » : ce qu'on
/// taperait dans la console du dossier, avec la configuration git et le compte
/// ssh de l'utilisateur. L'écran attend la fin de git, et montre sa sortie.
/// </summary>
public partial class MainWindow
{
    /// <summary>Nombre de lignes de la sortie de git gardées à l'écran.</summary>
    private const int GitOutputLines = 20;

    /// <summary>Dernier commit ou push de chaque côté, pour sa pastille.</summary>
    private readonly Dictionary<ExchangeSide, (GitResult Result, string Command, DateTime At)> _gitResults = [];

    /// <summary>
    /// Commite le dossier en clair.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnCommitSource(object sender, RoutedEventArgs e)
    {
        await RunGitAsync(ExchangeSide.Source, commit: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Pousse le dossier en clair.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnPushSource(object sender, RoutedEventArgs e)
    {
        await RunGitAsync(ExchangeSide.Source, commit: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Commite le dossier de l'IA.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnCommitTarget(object sender, RoutedEventArgs e)
    {
        await RunGitAsync(ExchangeSide.Target, commit: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Pousse le dossier de l'IA.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnPushTarget(object sender, RoutedEventArgs e)
    {
        await RunGitAsync(ExchangeSide.Target, commit: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Montre, pour chaque côté lié et versionné, sa ligne de commit et de
    /// push ; la section entière disparaît quand aucun ne l'est.
    /// </summary>
    private void RefreshGitView()
    {
        if (_project is null)
        {
            return;
        }

        string? source = ExchangePresenter.RepositoryOf(_project, ExchangeSide.Source);
        string? target = ExchangePresenter.RepositoryOf(_project, ExchangeSide.Target);

        SourceGitRow.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        SourceGitText.Text = source is null ? string.Empty : "dépôt " + ExchangeLabel(source, linked: true, "in");
        SourceGitText.ToolTip = source;
        TargetGitRow.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        TargetGitText.Text = target is null ? string.Empty : "dépôt " + ExchangeLabel(target, linked: true, "out");
        TargetGitText.ToolTip = target;
        GitSection.Visibility = source is null && target is null ? Visibility.Collapsed : Visibility.Visible;

        ShowGitState(ExchangeSide.Source, SourceBranchPill, SourceBranchText, SourceResultPill, SourceResultText);
        ShowGitState(ExchangeSide.Target, TargetBranchPill, TargetBranchText, TargetResultPill, TargetResultText);
    }

    /// <summary>
    /// Peint les pastilles d'un côté : l'accord de la branche avec sa branche
    /// distante — vert à jour, orange à pousser —, et le résultat du dernier
    /// commit ou push — vert réussi, rouge échoué.
    /// </summary>
    /// <param name="side">Côté de l'échange</param>
    /// <param name="branchPill">Pastille de la branche</param>
    /// <param name="branchText">Texte de la branche</param>
    /// <param name="resultPill">Pastille du dernier geste</param>
    /// <param name="resultText">Texte du dernier geste</param>
    private void ShowGitState(ExchangeSide side, System.Windows.Controls.Border branchPill, System.Windows.Controls.TextBlock branchText, System.Windows.Controls.Border resultPill, System.Windows.Controls.TextBlock resultText)
    {
        GitState? state = _project is null ? null : ExchangePresenter.GitStateOf(_project, side);
        if (state is not null)
        {
            string branch = state.Branch.Length == 0 ? "branche indéterminée" : $"branche {state.Branch}";
            (string text, string background, string foreground) = state.Sync switch
            {
                GitSync.InSync => ($"{branch} · à jour avec {state.Upstream}", "SuccessDim", "Success"),
                GitSync.Differs => ($"{branch} · diffère de {state.Upstream} : commits à pousser (ou à tirer)", "WarningDim", "SuspectForeground"),
                GitSync.NeverPushed => ($"{branch} · jamais poussée vers {state.Upstream}", "WarningDim", "SuspectForeground"),
                GitSync.NoUpstream => ($"{branch} · sans branche distante : « Push » échouera", "Raised", "TextSecondary"),
                _ => ($"{branch} · état distant inconnu", "Raised", "TextMuted"),
            };

            Paint(branchPill, branchText, text, background, foreground);
            branchText.ToolTip = "Lu dans les fichiers du dépôt, sans lancer git : l'état distant est celui du dernier push, fetch ou pull.";
        }

        if (!_gitResults.TryGetValue(side, out (GitResult Result, string Command, DateTime At) last))
        {
            resultPill.Visibility = Visibility.Collapsed;
            return;
        }

        string time = last.At.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        (string label, string tone, string ink) = last.Result switch
        {
            { NothingToDo: true } => ($"{time} · rien à commiter", "Raised", "TextSecondary"),
            { Success: true } => ($"✓ {time} · {last.Command} fait", "SuccessDim", "Success"),
            _ => ($"✗ {time} · {last.Command} échoué : voir la sortie ci-dessous", "DangerDim", "Danger"),
        };

        Paint(resultPill, resultText, label, tone, ink);
        resultPill.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Peint une pastille.
    /// </summary>
    /// <param name="pill">Pastille</param>
    /// <param name="text">Son texte</param>
    /// <param name="content">Texte à afficher</param>
    /// <param name="background">Clé du fond dans le thème</param>
    /// <param name="foreground">Clé du texte dans le thème</param>
    private void Paint(System.Windows.Controls.Border pill, System.Windows.Controls.TextBlock text, string content, string background, string foreground)
    {
        pill.Background = Themed<System.Windows.Media.Brush>(background);
        text.Foreground = Themed<System.Windows.Media.Brush>(foreground);
        text.Text = content;
    }

    /// <summary>
    /// Lance un commit ou un push sur un côté, hors du fil de l'écran, puis en
    /// montre le résultat.
    /// </summary>
    /// <param name="side">Côté de l'échange</param>
    /// <param name="commit">Vrai pour un commit, faux pour un push</param>
    /// <returns>La tâche de la commande</returns>
    private async Task RunGitAsync(ExchangeSide side, bool commit)
    {
        if (_project is null || _busy)
        {
            return;
        }

        ObfuscationProject project = _project;
        string folder = side == ExchangeSide.Source ? project.SourcePath : project.TargetPath;
        string label = ExchangeLabel(folder, linked: true, side == ExchangeSide.Source ? "in" : "out");
        string message = (side == ExchangeSide.Source ? SourceCommitMessage : TargetCommitMessage).Text;
        string command = commit ? "commit" : "push";

        try
        {
            SetBusy(true, commit ? "Commit en cours…" : "Push en cours…", $"git {command} est lancé dans « {label} », comme dans sa console.");
            GitResult result = await Task.Run(() => commit
                ? ExchangePresenter.Commit(project, side, message)
                : ExchangePresenter.Push(project, side)).ConfigureAwait(true);

            _gitResults[side] = (result, command, DateTime.Now);
            ShowGitOutput($"« {label} » · git {command} · {result.Summary}", result);
            RefreshGitView();
            SetStatus($"« {label} » : {result.Summary}", success: result.Success);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, success: false);
        }
        finally
        {
            SetBusy(false, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Montre la fin de la sortie de git sous les lignes de commit et de push.
    /// </summary>
    /// <param name="title">Dossier, commande et résumé</param>
    /// <param name="result">Résultat de git</param>
    private void ShowGitOutput(string title, GitResult result)
    {
        string[] lines = result.Output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> shown = lines.Length > GitOutputLines ? ["…", .. lines[^GitOutputLines..]] : lines;

        // Le cadre prend la couleur du résultat ; le titre la porte aussi, la
        // sortie de git reste lisible en texte secondaire.
        (string background, string foreground) = result switch
        {
            { NothingToDo: true } => ("Raised", "TextPrimary"),
            { Success: true } => ("SuccessDim", "Success"),
            _ => ("DangerDim", "Danger"),
        };

        GitOutputPanel.Background = Themed<System.Windows.Media.Brush>(background);
        GitOutputText.Inlines.Clear();
        GitOutputText.Inlines.Add(new System.Windows.Documents.Run(title)
        {
            Foreground = Themed<System.Windows.Media.Brush>(foreground),
            FontWeight = FontWeights.SemiBold,
        });

        foreach (string line in shown)
        {
            GitOutputText.Inlines.Add(new System.Windows.Documents.LineBreak());
            GitOutputText.Inlines.Add(new System.Windows.Documents.Run(line));
        }

        GitOutputPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Oublie les résultats git du projet fermé.
    /// </summary>
    private void ForgetGitResults()
    {
        _gitResults.Clear();
        GitOutputPanel.Visibility = Visibility.Collapsed;
        SourceResultPill.Visibility = Visibility.Collapsed;
        TargetResultPill.Visibility = Visibility.Collapsed;
    }
}
