using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Résultat d'une analyse, prêt à être affiché.
/// </summary>
/// <param name="Result">Résultat produit par le Presenter</param>
/// <param name="Files">Aperçu de chaque fichier, composé en arrière-plan</param>
/// <param name="Paths">Fichiers analysés, dans l'ordre</param>
/// <param name="Generation">Session de projet à laquelle l'analyse appartient</param>
internal sealed record AnalysisOutcome(
    AnalysisResult Result,
    IReadOnlyList<FilePreviewModel> Files,
    IReadOnlyList<string> Paths,
    int Generation);

/// <summary>
/// Analyse en arrière-plan, état de l'aperçu, obfuscation et restitution.
/// </summary>
public partial class MainWindow
{
    /// <summary>Famille des tâches d'analyse, qui s'annulent mutuellement.</summary>
    private const string AnalysisKind = "analysis";

    /// <summary>Délai qui regroupe des dépôts de fichiers rapprochés.</summary>
    private static readonly TimeSpan FileDropDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Délai qui regroupe des décisions rapprochées avant de relancer l'analyse :
    /// assez court pour que l'aperçu suive, assez long pour que trois clics
    /// successifs ne lancent qu'une analyse.
    /// </summary>
    private static readonly TimeSpan ChangeAnalysisDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Dernière analyse appliquée à l'écran.</summary>
    private AnalysisOutcome? _analysis;

    /// <summary>Empreinte des règles sur lesquelles repose l'analyse affichée.</summary>
    private string? _analysedFingerprint;

    /// <summary>Empreinte des règles actuelles du projet, recalculée après chaque décision.</summary>
    private string? _currentFingerprint;

    /// <summary>
    /// Empreinte pour laquelle l'utilisateur a refermé l'annonce « analyse
    /// nécessaire » : elle ne revient qu'après une nouvelle décision.
    /// </summary>
    private string? _dismissedStaleFingerprint;

    /// <summary>
    /// Vrai si l'annonce centrale doit s'afficher : l'utilisateur vient de
    /// revenir sur un onglet dont l'analyse est dépassée.
    /// </summary>
    private bool _staleOverlayPending;

    private Action? _overlayPrimaryAction;
    private Action? _overlaySecondaryAction;
    private Action? _bannerAction;

    /// <summary>
    /// Vrai si les règles ont changé depuis l'analyse affichée : l'aperçu ne
    /// montre pas encore leur effet.
    /// </summary>
    private bool IsStale => _analysis is not null
        && _currentFingerprint is not null
        && !string.Equals(_analysedFingerprint, _currentFingerprint, StringComparison.Ordinal);

    // ═══════════════════════════════ Analyse ════════════════════════════════

    /// <summary>
    /// Relance l'analyse à la demande.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnScan(object sender, RoutedEventArgs e)
    {
        RequestAnalysis();
    }

    /// <summary>
    /// Lance l'analyse tout de suite, en annulant celle qui tournerait encore.
    /// </summary>
    private void RequestAnalysis()
    {
        if (_project is null || _files.Count == 0 || _busy)
        {
            return;
        }

        _staleOverlayPending = false;
        ScheduleAnalysis(TimeSpan.Zero, null);
    }

    /// <summary>
    /// Planifie une analyse en arrière-plan.
    /// </summary>
    /// <param name="delay">Délai avant démarrage</param>
    /// <param name="reason">Phrase à placer devant le compte rendu, s'il y en a une</param>
    private void ScheduleAnalysis(TimeSpan delay, string? reason)
    {
        if (_project is null || _files.Count == 0)
        {
            return;
        }

        int generation = _projectGeneration;
        string title = string.Create(CultureInfo.CurrentCulture, $"Analyse de {_files.Count} fichier(s)");

        _tasks.Schedule(
            AnalysisKind,
            title,
            delay,
            () => PrepareAnalysis(generation),
            outcome => ApplyAnalysis(outcome, reason));

        RefreshAnalysisState();
    }

    /// <summary>
    /// Prépare une analyse au moment où elle démarre : c'est ici, sur le fil
    /// de l'écran, que le projet est recopié.
    /// </summary>
    /// <param name="generation">Session de projet pour laquelle l'analyse a été demandée</param>
    /// <returns>Le travail à exécuter en arrière-plan, ou <c>null</c> s'il n'y a plus rien à faire</returns>
    private Func<IProgress<BatchProgress>, CancellationToken, AnalysisOutcome>? PrepareAnalysis(int generation)
    {
        if (_project is null || generation != _projectGeneration || _files.Count == 0 || _busy)
        {
            return null;
        }

        AnalysisPresenter presenter = new(_project);
        List<string> paths = [.. _files];

        return (progress, token) =>
        {
            AnalysisResult result = presenter.Analyse(paths, progress, token);

            List<FilePreviewModel> files = new(result.Previews.Count);
            foreach (FilePreview preview in result.Previews)
            {
                token.ThrowIfCancellationRequested();
                files.Add(PreviewBuilder.Build(preview, result.Exemptions));
            }

            return new AnalysisOutcome(result, files, paths, generation);
        };
    }

    /// <summary>
    /// Applique le résultat d'une analyse à l'écran.
    /// </summary>
    /// <param name="outcome">Résultat de l'analyse</param>
    /// <param name="reason">Phrase à placer devant le compte rendu, s'il y en a une</param>
    private void ApplyAnalysis(AnalysisOutcome outcome, string? reason)
    {
        if (_project is null || outcome.Generation != _projectGeneration)
        {
            return;
        }

        // La liste a changé pendant l'analyse : son résultat ne décrit plus le
        // lot, on en relance une sur la liste actuelle.
        if (!outcome.Paths.SequenceEqual(_files, StringComparer.OrdinalIgnoreCase))
        {
            ScheduleAnalysis(TimeSpan.Zero, reason);
            return;
        }

        _analysis = outcome;
        _analysedFingerprint = outcome.Result.RulesFingerprint;
        _dismissedStaleFingerprint = null;
        _staleOverlayPending = false;

        ScanReport scan = outcome.Result.Scan;
        _categoryCounts = [.. scan.Findings
            .GroupBy(finding => finding.Category)
            .Select(group => (group.Key, group.Sum(finding => finding.Occurrences), group.Count()))];

        FillFindings(scan);
        FillSuggestions(outcome);
        RebuildMaskTargets();
        WirePreviewModels();
        RebuildPreviewTabs();

        // Les décisions prises pendant que l'analyse tournait ne figurent pas
        // dans son instantané : elles sont reportées sur le résultat, pour que
        // l'écran ne revienne pas en arrière.
        Reconcile();
        RefreshPreviewCounts();

        ReportViewer.Document = ReportComposer.Configuration(_project, this);
        UpdateActionState();
        RefreshAnalysisState();

        int suspects = outcome.Files.Sum(file => file.Markers.Count(marker => marker.State == PreviewMarkerState.Suspect));
        string summary = string.Create(
            CultureInfo.CurrentCulture,
            $"Analyse terminée en {BackgroundTaskItem.FormatDuration(outcome.Result.Duration)} · {scan.FileCount} fichier(s) · {scan.Findings.Count} valeur(s) seront masquées · {suspects} signalement(s) à trancher · {scan.Candidates.Count} terme(s) proposé(s).");

        SetStatus(reason is null ? summary : $"{reason} {summary}");
    }

    /// <summary>
    /// Oublie l'analyse affichée, après un changement de projet ou de lot.
    /// </summary>
    private void ClearAnalysis()
    {
        ForgetSelection();
        _analysis = null;
        _analysedFingerprint = null;
        _dismissedStaleFingerprint = null;
        _staleOverlayPending = false;
        _categoryCounts = [];
        _markerOwners.Clear();
        _previewLists.Clear();
        _previewHeaders.Clear();
        _expandedRanges.Clear();

        PreviewTabs.Items.Clear();
        UpdatePreviewEmptyHint();
        FillFindings(new ScanReport([], [], 0));
        ReplaceSuggestions([]);
        RebuildMaskTargets();
    }

    /// <summary>
    /// Annule l'analyse en cours depuis le bandeau d'état.
    /// </summary>
    /// <param name="sender">Lien d'annulation</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCancelAnalysis(object sender, RoutedEventArgs e)
    {
        _tasks.Cancel(AnalysisKind, "annulée à votre demande");
    }

    /// <summary>
    /// Ouvre la fenêtre des tâches d'arrière-plan, ou la ramène au premier plan.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnShowTasks(object sender, RoutedEventArgs e)
    {
        if (_tasksWindow is not null)
        {
            _tasksWindow.Activate();
            return;
        }

        _tasksWindow = new TasksWindow(_tasks, _preferences.AnalyseAfterEachChange, OnAnalyseAfterEachChangeToggled)
        {
            Owner = this,
        };

        _tasksWindow.Closed += (_, _) => _tasksWindow = null;
        _tasksWindow.Show();
    }

    /// <summary>
    /// Change le mode d'analyse, et l'applique aussitôt si l'aperçu est dépassé.
    /// </summary>
    /// <param name="enabled">Vrai pour relancer l'analyse après chaque modification</param>
    private void OnAnalyseAfterEachChangeToggled(bool enabled)
    {
        _preferences.AnalyseAfterEachChange = enabled;
        _preferences.Save();

        // L'interrupteur du bandeau et la case de la fenêtre Tâches disent le
        // même réglage : chacun suit l'autre.
        ShowAutoAnalysis();
        _tasksWindow?.ShowAutoAnalyse(enabled);

        // Les messages sont posés avant de relancer : une analyse qui part
        // aussitôt écrit son propre état dans le bandeau.
        SetStatus(enabled
            ? "Analyse auto active : chaque décision relance l'analyse en arrière-plan, l'aperçu reste à jour."
            : "Analyse auto inactive : Masquer, Garder ici et Partout s'enchaînent sans lancer d'analyse. L'aperçu signale quand elle est nécessaire ; « 1 · Analyser » (F5) la lance.");

        if (enabled && IsStale && _tasks.ActiveCount == 0)
        {
            ScheduleAnalysis(TimeSpan.Zero, "Analyse auto réactivée.");
        }
    }

    /// <summary>
    /// Aligne l'interrupteur « Analyse auto » du bandeau d'état sur la
    /// préférence ; il n'apparaît qu'avec un projet ouvert.
    /// </summary>
    private void ShowAutoAnalysis()
    {
        bool enabled = _preferences.AnalyseAfterEachChange;

        AutoAnalysePanel.Visibility = _project is null ? Visibility.Collapsed : Visibility.Visible;
        AutoAnalyseSwitch.IsChecked = enabled;
        AutoAnalyseText.Text = enabled ? "active" : "inactive";
        AutoAnalyseText.Foreground = Themed<System.Windows.Media.Brush>(enabled ? "TextPrimary" : "Warning");
        AutoAnalyseText.FontWeight = enabled ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>
    /// Active ou coupe l'analyse automatique depuis l'interrupteur du bandeau.
    /// </summary>
    /// <param name="sender">Interrupteur</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAutoAnalyseSwitchClick(object sender, RoutedEventArgs e)
    {
        OnAnalyseAfterEachChangeToggled(AutoAnalyseSwitch.IsChecked == true);
    }

    // ═══════════════════════════ État de l'aperçu ═══════════════════════════

    /// <summary>
    /// Met à jour tout ce qui dépend de l'état de l'analyse : l'indicateur de
    /// tâches, le bouton « Analyser », le bandeau et l'annonce centrale.
    /// </summary>
    private void RefreshAnalysisState()
    {
        BackgroundTaskItem? running = _tasks.Running;
        BackgroundTaskItem? active = running ?? _tasks.Active;
        bool stale = IsStale;

        UpdateTaskIndicator(running, active);

        // Le curseur « flèche et sablier » dit qu'un travail tourne sans
        // bloquer : l'écran reste utilisable.
        Cursor = running is not null ? Cursors.AppStarting : null;

        // Une seule action principale à la fois, qui suit le cycle : analyser
        // quand l'aperçu est dépassé, rapatrier quand l'IA a travaillé dans la
        // cible, obfusquer sinon. Le style est effacé plutôt que mis à
        // « aucun » : seule une valeur effacée laisse s'appliquer le style
        // implicite du thème.
        bool analyseFirst = stale && active is null;
        bool returnFirst = !analyseFirst && _exchangeStatus is { TargetPending: > 0 };
        SetPrimary(ScanButton, analyseFirst);
        SetPrimary(ReturnButton, returnFirst);
        SetPrimary(ObfuscateButton, !analyseFirst && !returnFirst);
        ScanButton.Content = analyseFirst ? "1 · Analyser (nécessaire)" : "1 · Analyser";

        UpdateBanner(active, stale);
        UpdateOverlay(active, stale);
    }

    /// <summary>
    /// Donne ou retire à un bouton l'apparence de l'action principale.
    /// </summary>
    /// <param name="button">Bouton concerné</param>
    /// <param name="primary">Vrai pour l'action principale</param>
    private void SetPrimary(Button button, bool primary)
    {
        if (primary)
        {
            button.Style = Themed<Style>("PrimaryButton");
        }
        else
        {
            button.ClearValue(StyleProperty);
        }
    }

    /// <summary>
    /// Met à jour l'indicateur de tâches, en bas à droite.
    /// </summary>
    /// <param name="running">Tâche en cours d'exécution</param>
    /// <param name="active">Tâche en cours ou prévue</param>
    private void UpdateTaskIndicator(BackgroundTaskItem? running, BackgroundTaskItem? active)
    {
        int count = _tasks.ActiveCount;
        TasksButton.Content = count == 0
            ? "Tâches"
            : string.Create(CultureInfo.CurrentCulture, $"Tâches ({count})");

        TaskSpinner.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        TaskCancelButton.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        TaskProgress.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
        TaskSummary.Foreground = Themed<System.Windows.Media.Brush>("TextMuted");

        if (running is not null)
        {
            TaskProgress.Value = running.Progress;
            TaskSummary.Text = $"Analyse · {running.Detail}";
            TaskSummary.ToolTip = TaskSummary.Text;
            return;
        }

        if (active is not null)
        {
            TaskSummary.Text = "Analyse prévue…";
            TaskSummary.ToolTip = null;
            return;
        }

        BackgroundTaskItem? last = _tasks.Tasks.FirstOrDefault(task => !task.IsActive);
        TaskSummary.ToolTip = last?.Detail;

        switch (last?.State)
        {
            case null:
                TaskSummary.Text = "Aucune tâche en cours";
                break;

            case BackgroundTaskState.Completed:
                TaskSummary.Text = $"Dernière analyse {last.Detail}";
                break;

            case BackgroundTaskState.Failed:
                TaskSummary.Text = $"Dernière analyse en échec : {last.Detail}";
                TaskSummary.Foreground = Themed<System.Windows.Media.Brush>("Danger");
                break;

            default:
                TaskSummary.Text = $"Dernière analyse annulée : {last.Detail}";
                break;
        }
    }

    /// <summary>
    /// Met à jour le bandeau des onglets d'analyse : analyse en cours, ou
    /// analyse nécessaire. Il n'apparaît que lorsqu'un aperçu est affiché ;
    /// sans aperçu, c'est l'annonce centrale qui parle.
    /// </summary>
    /// <param name="task">Analyse en cours ou prévue</param>
    /// <param name="stale">Vrai si l'aperçu est dépassé</param>
    private void UpdateBanner(BackgroundTaskItem? task, bool stale)
    {
        if (!IsAnalysisTab(DetailTabs.SelectedItem) || _analysis is null || (task is null && !stale))
        {
            AnalysisBanner.Visibility = Visibility.Collapsed;
            return;
        }

        AnalysisBanner.Visibility = Visibility.Visible;

        if (task is not null)
        {
            // Un fond propre au travail en arrière-plan : on reconnaît le
            // bandeau sans le lire.
            AnalysisBanner.Background = Themed<System.Windows.Media.Brush>("ProgressBackground");
            AnalysisBanner.BorderBrush = Themed<System.Windows.Media.Brush>("ProgressBorder");
            BannerText.Foreground = Themed<System.Windows.Media.Brush>("ProgressForeground");
            BannerSpinner.Visibility = Visibility.Visible;
            BannerProgress.Visibility = task.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            BannerProgress.Value = task.Progress;
            BannerText.Text = task.IsRunning
                ? $"Analyse en cours · {task.Detail}. L'aperçu affiché date de l'analyse précédente."
                : "Analyse prévue : elle démarre dans un instant.";
            BannerButton.Content = "Annuler";
            _bannerAction = () => _tasks.Cancel(AnalysisKind, "annulée à votre demande");
            return;
        }

        AnalysisBanner.Background = Themed<System.Windows.Media.Brush>("WarningDim");
        AnalysisBanner.BorderBrush = Themed<System.Windows.Media.Brush>("WarningBorder");
        BannerText.Foreground = Themed<System.Windows.Media.Brush>("SuspectForeground");
        BannerSpinner.Visibility = Visibility.Collapsed;
        BannerProgress.Visibility = Visibility.Collapsed;
        BannerText.Text = "Analyse nécessaire : vos règles ont changé depuis la dernière analyse, et cet onglet ne montre pas encore leur effet.";
        BannerButton.Content = "Analyser";
        _bannerAction = RequestAnalysis;
    }

    /// <summary>
    /// Met à jour l'annonce centrale des onglets d'analyse.
    ///
    /// Elle couvre l'onglet quand il n'y a rien de juste à y montrer : pas
    /// encore de fichier, pas encore d'analyse, ou une analyse dépassée que
    /// l'utilisateur retrouve en revenant sur l'onglet. Dans ce dernier cas,
    /// elle peut être refermée pour consulter l'aperçu tel quel ; le bandeau
    /// continue alors de signaler qu'il est dépassé.
    /// </summary>
    /// <param name="task">Analyse en cours ou prévue</param>
    /// <param name="stale">Vrai si l'aperçu est dépassé</param>
    private void UpdateOverlay(BackgroundTaskItem? task, bool stale)
    {
        if (_project is null || !IsAnalysisTab(DetailTabs.SelectedItem))
        {
            AnalysisOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_files.Count == 0)
        {
            if (_project.IsSourceLinked)
            {
                ShowOverlay(
                    spinner: false,
                    "La source ne contient aucun fichier à traiter",
                    Directory.Exists(_project.SourcePath)
                        ? "Le dossier en clair lié ne contient aucun fichier texte, ou ils sont tous tenus à l'écart. Ce que vous y ajoutez apparaît ici de lui-même ; les exclusions se révoquent dans l'onglet « Échanges »."
                        : $"Le dossier en clair lié est introuvable : « {_project.SourcePath} ». Liez-le de nouveau, ou revenez à « in », dans l'onglet « Échanges ».",
                    null,
                    ("Onglet « Échanges »", () => DetailTabs.SelectedItem = ExchangeTab),
                    ("Ouvrir la source", () => OnOpenSource(this, new RoutedEventArgs())));
                return;
            }

            ShowOverlay(
                spinner: false,
                "Le dossier « in » est vide",
                "Déposez vos fichiers dans la zone de gauche, ou n'importe où sur la fenêtre : ils sont copiés dans le dossier « in » du projet, puis analysés en arrière-plan. Ce que vous placez directement dans « in » apparaît aussi. Pour travailler sur un dépôt en clair sans copie, liez-le dans l'onglet « Échanges ».",
                null,
                ("Ajouter…", () => OnAddFiles(this, new RoutedEventArgs())),
                ("Ouvrir « in »", () => OnOpenInputFolder(this, new RoutedEventArgs())));
            return;
        }

        if (_analysis is null)
        {
            if (task is not null)
            {
                ShowOverlay(
                    spinner: true,
                    task.IsRunning ? "Analyse en cours…" : "Analyse prévue…",
                    "Vous pouvez continuer à travailler pendant ce temps : l'aperçu s'affichera dès qu'elle sera terminée.",
                    task,
                    null,
                    ("Annuler", () => _tasks.Cancel(AnalysisKind, "annulée à votre demande")));
                return;
            }

            ShowOverlay(
                spinner: false,
                "Analyse nécessaire",
                "Ces fichiers n'ont pas encore été analysés, ou la dernière analyse a été annulée.",
                null,
                ("Analyser", RequestAnalysis),
                null);
            return;
        }

        if (stale && task is null && _staleOverlayPending
            && !string.Equals(_dismissedStaleFingerprint, _currentFingerprint, StringComparison.Ordinal))
        {
            ShowOverlay(
                spinner: false,
                "Analyse nécessaire",
                "Vos règles ont changé depuis la dernière analyse — un terme ajouté, une catégorie ou une valeur modifiée : cet onglet ne montre pas encore leur effet.",
                null,
                ("Analyser", RequestAnalysis),
                ("Afficher quand même", DismissStaleOverlay));
            return;
        }

        AnalysisOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Affiche l'annonce centrale.
    /// </summary>
    /// <param name="spinner">Vrai pour afficher l'indicateur d'activité</param>
    /// <param name="title">Titre de l'annonce</param>
    /// <param name="text">Explication</param>
    /// <param name="task">Tâche dont afficher l'avancement, s'il y en a une</param>
    /// <param name="primary">Action principale, s'il y en a une</param>
    /// <param name="secondary">Action secondaire, s'il y en a une</param>
    private void ShowOverlay(
        bool spinner,
        string title,
        string text,
        BackgroundTaskItem? task,
        (string Label, Action Action)? primary,
        (string Label, Action Action)? secondary)
    {
        AnalysisOverlay.Visibility = Visibility.Visible;
        OverlaySpinner.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        OverlayTitle.Text = title;
        OverlayText.Text = text;

        OverlayProgress.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayProgress.IsIndeterminate = task is not null && !task.IsRunning;
        OverlayProgress.Value = task?.Progress ?? 0;
        OverlayDetail.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayDetail.Text = task?.Detail ?? string.Empty;

        OverlayPrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        OverlayPrimaryButton.Content = primary?.Label;
        _overlayPrimaryAction = primary?.Action;

        OverlaySecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        OverlaySecondaryButton.Content = secondary?.Label;
        _overlaySecondaryAction = secondary?.Action;
    }

    /// <summary>
    /// Referme l'annonce « analyse nécessaire » pour consulter l'aperçu tel
    /// quel.
    /// </summary>
    private void DismissStaleOverlay()
    {
        _dismissedStaleFingerprint = _currentFingerprint;
        _staleOverlayPending = false;
        RefreshAnalysisState();
    }

    /// <summary>
    /// Exécute l'action principale de l'annonce centrale.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOverlayPrimary(object sender, RoutedEventArgs e)
    {
        _overlayPrimaryAction?.Invoke();
    }

    /// <summary>
    /// Exécute l'action secondaire de l'annonce centrale.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOverlaySecondary(object sender, RoutedEventArgs e)
    {
        _overlaySecondaryAction?.Invoke();
    }

    /// <summary>
    /// Exécute l'action du bandeau.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnBannerAction(object sender, RoutedEventArgs e)
    {
        _bannerAction?.Invoke();
    }

    /// <summary>
    /// Arme l'annonce « analyse nécessaire » quand l'utilisateur revient sur
    /// un onglet d'analyse dépassé.
    /// </summary>
    /// <param name="sender">Onglets de droite</param>
    /// <param name="e">Changement de sélection</param>
    private void OnDetailTabChanged(object sender, SelectionChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Les sélections des grilles et des sous-onglets remontent jusqu'ici :
        // seules comptent celles des onglets de droite eux-mêmes.
        if (!ReferenceEquals(e.OriginalSource, DetailTabs))
        {
            return;
        }

        _staleOverlayPending = IsStale && IsAnalysisTab(DetailTabs.SelectedItem);
        RefreshAnalysisState();

        // Un commit ou un push fait dans la console se voit en revenant sur
        // l'onglet : l'état git se relit dans les fichiers du dépôt.
        if (ReferenceEquals(DetailTabs.SelectedItem, ExchangeTab))
        {
            RefreshGitView();
        }
    }

    /// <summary>
    /// Indique si un onglet montre un résultat d'analyse.
    /// </summary>
    /// <param name="tab">Onglet sélectionné</param>
    /// <returns><c>true</c> pour l'aperçu, les valeurs repérées et les suggestions</returns>
    private bool IsAnalysisTab(object? tab)
    {
        return ReferenceEquals(tab, PreviewTab) || ReferenceEquals(tab, FindingsTab) || ReferenceEquals(tab, SuggestionsTab);
    }

    // ═══════════════════════════════ Attente ═════════════════════════════════

    /// <summary>
    /// Bascule l'écran en attente, ou l'en sort.
    /// </summary>
    /// <param name="busy">Vrai pendant une obfuscation ou un rapatriement</param>
    /// <param name="title">Titre de l'attente</param>
    /// <param name="text">Explication</param>
    private void SetBusy(bool busy, string title, string text)
    {
        _busy = busy;

        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyTitle.Text = title;
        BusyText.Text = text;
        BusyProgress.Value = 0;
        BusyDetail.Text = string.Empty;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;

        if (busy)
        {
            // La saisie en cours est suspendue : une touche Entrée dans le
            // champ d'ajout de terme modifierait le projet pendant l'écriture.
            Keyboard.Focus(this);
        }

        UpdateActionState();
        RefreshAnalysisState();
    }
}
