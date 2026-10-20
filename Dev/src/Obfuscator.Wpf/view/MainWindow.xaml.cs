using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Choix proposé dans la liste des catégories.
/// </summary>
/// <param name="Value">Catégorie technique</param>
/// <param name="Label">Libellé lisible</param>
public sealed record CategoryChoice(SpanCategory Value, string Label)
{
    /// <summary>
    /// Rend le libellé plutôt que la forme générée par l'enregistrement : c'est
    /// ce texte que la liste déroulante affiche une fois le choix fait.
    /// </summary>
    /// <returns>Le libellé lisible</returns>
    public override string ToString() => Label;
}

/// <summary>
/// Fenêtre principale : ouverture du projet, dépôt des fichiers, arbitrage de
/// ce qui sera masqué, aperçu côte à côte, obfuscation et restitution.
///
/// Trois règles de conduite organisent l'écran.
///
/// Rien ne fige la fenêtre. L'analyse tourne en arrière-plan sur un instantané
/// du projet ; l'obfuscation, qui écrit des fichiers, affiche sa progression
/// et attend sa fin.
///
/// Une décision s'applique sur-le-champ. Elle est écrite dans le projet, et
/// l'écran la reflète sans relancer l'analyse quand il le peut — une
/// occurrence exceptée, une proposition tranchée. Quand il ne le peut pas — un
/// terme ajouté, une catégorie décochée —, il le dit : l'aperçu annonce qu'une
/// analyse est nécessaire, au lieu de montrer un état périmé comme s'il était
/// à jour.
///
/// La fenêtre ne contient aucune règle de détection ni aucune décision métier :
/// elle enchaîne les gestes en déléguant chacun à son Presenter.
///
/// Le code est réparti en plusieurs fichiers partiels, un par responsabilité :
/// projet et fichiers ici, analyse, décisions, aperçu et glisser-déposer à
/// côté.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _files = [];
    private readonly ObservableCollection<MaskTargetRow> _maskTargets = [];
    private readonly List<string> _pendingPaths = [];
    private readonly UserPreferences _preferences = UserPreferences.Load();
    private readonly TaskCenter _tasks;

    private ObservableCollection<FindingRow> _findings = [];
    private ObservableCollection<SuggestionRow> _suggestions = [];
    private ObfuscationProject? _project;
    private TasksWindow? _tasksWindow;

    /// <summary>Historique des messages du bandeau d'état, que la console présente en entier.</summary>
    private readonly StatusJournal _statusJournal = new();

    /// <summary>Console ouverte, s'il y en a une.</summary>
    private ConsoleWindow? _consoleWindow;

    private bool _busy;

    /// <summary>
    /// Numéro de la session de projet : il change à chaque ouverture ou
    /// fermeture, pour qu'un résultat d'analyse arrivant après coup ne soit
    /// jamais appliqué au mauvais projet.
    /// </summary>
    private int _projectGeneration;

    /// <summary>
    /// Construit la fenêtre et raccorde les collections aux grilles.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);

        _tasks = new TaskCenter(Dispatcher);
        _tasks.Changed += (_, _) => RefreshAnalysisState();

        MaskTargetsGrid.ItemsSource = _maskTargets;
        FindingsGrid.ItemsSource = _findings;
        SuggestionsGrid.ItemsSource = _suggestions;
        FilesList.ItemsSource = _fileRows;

        NewTermCategory.ItemsSource = CategoryChoices();
        NewTermCategory.SelectedIndex = 0;

        VersionText.Text = $"v{ProductVersion.Current}";
        VersionText.ToolTip = $"Obfuscator {ProductVersion.Current}";

        InitializeDragDrop();
        RefreshRecentProjects();
        ShowProjectState();
        RefreshAnalysisState();
    }

    /// <summary>
    /// Compose la liste des natures proposées à l'ajout d'un terme.
    /// </summary>
    /// <returns>Les choix, dans l'ordre d'usage</returns>
    private static List<CategoryChoice> CategoryChoices()
    {
        return [.. CategoryLabels.ManualCategories()
            .Select(category => new CategoryChoice(category, CategoryLabels.Label(category)))];
    }

    // ═══════════════════════════════ Projet ═════════════════════════════════

    /// <summary>
    /// Ouvre le menu du projet : ouvrir, créer, rouvrir un projet récent,
    /// fermer.
    /// </summary>
    /// <param name="sender">Bouton du menu</param>
    /// <param name="e">Données de l'événement</param>
    private void OnProjectMenu(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new()
        {
            PlacementTarget = ProjectMenuButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
        };

        menu.Items.Add(MenuEntry("Ouvrir un projet…", "Ctrl+O", () => ChooseProject(create: false)));
        menu.Items.Add(MenuEntry("Créer un projet…", "Ctrl+N", () => ChooseProject(create: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Projets récents", IsEnabled = false });

        List<RecentProjectItem> recents = _preferences.RecentItems();
        if (recents.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "aucun pour l'instant", IsEnabled = false });
        }

        foreach (RecentProjectItem recent in recents)
        {
            MenuItem item = MenuEntry(MenuText(recent.Name), recent.Shortcut, () => OpenRecent(recent));
            item.ToolTip = recent.Detail;
            item.IsChecked = _project is not null && SameFolder(_project.RootPath, recent.Path);
            menu.Items.Add(item);
        }

        if (_project is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Fermer le projet", string.Empty, CloseProject));
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Compose une entrée de menu.
    /// </summary>
    /// <param name="header">Libellé</param>
    /// <param name="gesture">Raccourci affiché</param>
    /// <param name="action">Action à exécuter</param>
    /// <returns>L'entrée de menu</returns>
    private static MenuItem MenuEntry(string header, string gesture, Action action)
    {
        MenuItem item = new() { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Ouvre un projet existant.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        ChooseProject(create: false);
    }

    /// <summary>
    /// Crée un projet.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCreateProject(object sender, RoutedEventArgs e)
    {
        ChooseProject(create: true);
    }

    /// <summary>
    /// Fait choisir le dossier d'un projet, puis l'ouvre ou le crée.
    /// </summary>
    /// <param name="create">Vrai pour créer un projet, faux pour en ouvrir un</param>
    private void ChooseProject(bool create)
    {
        if (_busy)
        {
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = create
                ? "Dossier du nouveau projet : les fichiers à traiter iront dans son sous-dossier « in », les fichiers obfusqués dans « out »"
                : "Dossier du projet à ouvrir",
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenProjectAt(dialog.FolderName, create);
        }
    }

    /// <summary>
    /// Ouvre le projet d'un dossier, ou l'y crée.
    /// </summary>
    /// <param name="root">Dossier du projet</param>
    /// <param name="create">Vrai si l'utilisateur a demandé une création</param>
    private void OpenProjectAt(string root, bool create)
    {
        bool exists = IsProjectFolder(root);

        if (!exists && !create && !ConfirmDialog.Ask(
                this,
                "Ce dossier n'est pas encore un projet",
                $"« {root} » ne contient pas de projet Obfuscator.\n\nEn créer un ici ? Un sous-dossier .obf y sera ajouté pour le coffre chiffré, le vocabulaire et les réglages ; vos fichiers ne sont pas modifiés.",
                "Créer le projet"))
        {
            return;
        }

        Run(() =>
        {
            ObfuscationProject project = exists
                ? ObfuscationProject.Open(root, VaultProtectors.CreateDefault())
                : ObfuscationProject.Create(root, VaultProtectors.CreateDefault());

            // Avant d'afficher l'interrupteur, le coffre est mis dans le
            // chiffrement du projet : l'écran doit dire l'état du fichier.
            string? realigned = AlignVaultProtection(project);
            string? upgraded = PersistSettingsUpgrade(project);
            AttachProject(project);

            string opened = exists ? "ouvert" : "créé";
            string source = project.IsSourceLinked
                ? $"le dossier en clair « {ExchangeLabel(project.SourcePath, linked: true, "in")} »"
                : "son dossier « in »";
            string contents = (_files.Count, project.IsSourceLinked) switch
            {
                (0, true) => Directory.Exists(project.SourcePath)
                    ? $"Aucun fichier à traiter dans {source}."
                    : $"Dossier en clair introuvable : « {project.SourcePath} » ; liez-le de nouveau dans l'onglet « Échanges ».",
                (0, false) => "Déposez vos fichiers : ils sont copiés dans son dossier « in », puis analysés en arrière-plan.",
                _ => string.Create(CultureInfo.CurrentCulture, $"{_files.Count} fichier(s) dans {source} : analyse en arrière-plan."),
            };

            string status = exists && create
                ? $"Ce dossier contenait déjà un projet : « {project.DisplayName} » a été ouvert tel quel. {contents}"
                : $"Projet « {project.DisplayName} » {opened}. {contents}";

            SetStatus(string.Join(' ', new[] { status, realigned, upgraded }.Where(part => !string.IsNullOrEmpty(part))));

            if (_pendingPaths.Count > 0)
            {
                List<string> pending = [.. _pendingPaths];
                _pendingPaths.Clear();
                UpdatePendingNotice();
                AddPaths(pending);
            }
        });
    }

    /// <summary>
    /// Enregistre les réglages d'un projet que leur mise à jour vient de
    /// changer, pour qu'elle n'ait lieu qu'une fois, et dit ce qui a changé.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    /// <returns>Ce que la mise à jour a changé, ou <c>null</c> s'il n'y avait rien à mettre à jour</returns>
    private static string? PersistSettingsUpgrade(ObfuscationProject project)
    {
        if (project.Settings.UpgradeNotes.Count == 0)
        {
            return null;
        }

        string notes = string.Join(' ', project.Settings.UpgradeNotes);
        project.Settings.UpgradeNotes.Clear();
        project.SaveRules();
        return notes;
    }

    /// <summary>
    /// Fait du projet ouvert le projet courant, et remet l'écran à zéro.
    /// </summary>
    /// <param name="project">Projet ouvert ou créé</param>
    private void AttachProject(ObfuscationProject project)
    {
        _tasks.CancelAll("projet changé");

        // L'état des échanges du projet précédent — compte du bouton
        // « 3 · Rapatrier », surveillance de sa cible — ne doit pas déteindre
        // sur le nouveau, le temps que le sien soit calculé.
        DetachExchange();
        _projectGeneration++;
        _project = project;
        _currentFingerprint = project.RulesFingerprint();

        _files.Clear();
        _fileRows.Clear();
        ClearAnalysis();

        _preferences.Remember(project.RootPath, project.DisplayName);
        _preferences.Save();
        RefreshRecentProjects();

        ReportViewer.Document = ReportComposer.Configuration(project, this);
        DetailTabs.SelectedItem = PreviewTab;
        RebuildWhitelistView();

        // Les aperçus laissés ouverts la dernière fois sont repris ; aucun
        // autre ne s'ouvre de lui-même.
        RestoreOpenPreviews(project);

        ShowProjectState();
        UpdateActionState();
        RefreshAnalysisState();

        // Les dossiers liés, la surveillance de la cible et l'état des
        // échanges suivent le projet.
        AttachExchange(project);

        // Le lot est le contenu de la source — « in », ou le dossier en clair
        // lié — : il est chargé tout de suite, puis suivi tant que le projet
        // reste ouvert.
        WatchInputFolder(project);
        SyncInputFiles(null);
        RebuildPreviewTabs();
    }

    /// <summary>
    /// Ferme le projet courant et revient à l'accueil.
    /// </summary>
    private void CloseProject()
    {
        if (_project is null || _busy)
        {
            return;
        }

        CommitProjectName();
        string name = _project.DisplayName;

        _tasks.CancelAll("projet fermé");
        StopWatchingInputFolder();
        DetachExchange();
        _projectGeneration++;
        _project = null;
        _currentFingerprint = null;

        // Les aperçus ouverts restent retenus pour la prochaine ouverture du
        // projet : seul l'écran les oublie.
        _openPreviews.Clear();
        _files.Clear();
        _fileRows.Clear();
        ClearAnalysis();
        RebuildWhitelistView();
        ShowProjectState();
        UpdateActionState();
        RefreshAnalysisState();
        SetStatus($"Projet « {name} » fermé.");
    }

    /// <summary>
    /// Rouvre un projet récent.
    /// </summary>
    /// <param name="sender">Ligne du projet récent</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenRecent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentProjectItem item)
        {
            OpenRecent(item);
        }
    }

    /// <summary>
    /// Rouvre le projet récent d'un rang donné, pour les raccourcis clavier.
    /// </summary>
    /// <param name="rank">Rang dans la liste, à partir de 1</param>
    private void OpenRecentByRank(int rank)
    {
        List<RecentProjectItem> recents = _preferences.RecentItems();
        if (rank >= 1 && rank <= recents.Count)
        {
            OpenRecent(recents[rank - 1]);
        }
    }

    /// <summary>
    /// Rouvre un projet récent, ou propose de l'oublier s'il a disparu.
    /// </summary>
    /// <param name="item">Projet récent</param>
    private void OpenRecent(RecentProjectItem item)
    {
        if (_busy)
        {
            return;
        }

        if (_project is not null && SameFolder(_project.RootPath, item.Path))
        {
            SetStatus($"« {item.Name} » est déjà le projet ouvert.");
            return;
        }

        if (!IsProjectFolder(item.Path))
        {
            if (ConfirmDialog.Ask(
                    this,
                    "Projet introuvable",
                    $"« {item.Path} » n'existe plus ou ne contient plus de projet.\n\nLe retirer de la liste des projets récents ?",
                    "Retirer de la liste"))
            {
                _preferences.Forget(item.Path);
                _preferences.Save();
                RefreshRecentProjects();
            }

            return;
        }

        CommitProjectName();
        OpenProjectAt(item.Path, create: false);
    }

    /// <summary>
    /// Met à jour la liste des projets récents de l'accueil.
    /// </summary>
    private void RefreshRecentProjects()
    {
        List<RecentProjectItem> recents = _preferences.RecentItems();
        RecentProjectsList.ItemsSource = recents;
        NoRecentProjects.Visibility = recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshClipboardProjects();
    }

    /// <summary>
    /// Adapte l'écran à la présence ou non d'un projet : l'accueil tant
    /// qu'aucun n'est ouvert, l'espace de travail ensuite.
    /// </summary>
    private void ShowProjectState()
    {
        bool open = _project is not null;

        WelcomePanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        WorkPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ProjectIdentity.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ProjectActions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ShowVaultProtection();
        ShowAutoAnalysis();

        if (_project is null)
        {
            Title = "Obfuscator";
            TokenBadge.Text = string.Empty;
            UpdatePendingNotice();
            return;
        }

        Title = $"{_project.DisplayName} — Obfuscator";
        ProjectNameBox.Text = _project.DisplayName;
        ProjectPathText.Text = _project.RootPath;
        ProjectPathText.ToolTip = _project.RootPath;
        TokenBadge.Text = $"jetons {_project.Format.Prefix}{_project.Format.Nonce}";
    }

    /// <summary>
    /// Place la saisie dans le nom du projet, tout sélectionné.
    /// </summary>
    /// <param name="sender">Bouton crayon</param>
    /// <param name="e">Données de l'événement</param>
    private void OnEditProjectName(object sender, RoutedEventArgs e)
    {
        ProjectNameBox.Focus();
        ProjectNameBox.SelectAll();
    }

    /// <summary>
    /// Valide ou annule le nom du projet au clavier.
    /// </summary>
    /// <param name="sender">Zone de saisie</param>
    /// <param name="e">Touche pressée</param>
    private void OnProjectNameKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter)
        {
            CommitProjectName();
            Keyboard.Focus(this);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ProjectNameBox.Text = _project?.DisplayName ?? string.Empty;
            Keyboard.Focus(this);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Enregistre le nom du projet quand la zone de saisie perd le focus.
    /// </summary>
    /// <param name="sender">Zone de saisie</param>
    /// <param name="e">Données de l'événement</param>
    private void OnProjectNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitProjectName();
    }

    /// <summary>
    /// Enregistre le nom saisi, s'il a changé.
    ///
    /// L'enregistrement ne peut pas dépendre de la seule perte de focus : un
    /// clic sur une zone qui ne prend pas le focus la laisse dans la zone de
    /// saisie, et le nom tapé serait perdu à la fermeture. Il est donc aussi
    /// déclenché par un clic ailleurs, par Entrée, et avant toute fermeture.
    /// </summary>
    private void CommitProjectName()
    {
        if (_project is null || _busy)
        {
            return;
        }

        string name = ProjectNameBox.Text.Trim();
        if (name == _project.DisplayName)
        {
            return;
        }

        Run(() =>
        {
            ReviewPresenter presenter = new(_project);
            presenter.Rename(name);
            presenter.Commit();

            _preferences.Rename(_project.RootPath, _project.DisplayName);
            _preferences.Save();
            RefreshRecentProjects();
            ShowProjectState();

            SetStatus(name.Length == 0
                ? $"Nom effacé : le projet reprend le nom de son dossier, « {_project.DisplayName} »."
                : $"Projet renommé « {_project.DisplayName} ».");
        });
    }

    // ═════════════════════════ Fichiers en attente ══════════════════════════

    /// <summary>
    /// Oublie les fichiers déposés en attente d'un projet.
    /// </summary>
    /// <param name="sender">Lien de l'avis</param>
    /// <param name="e">Données de l'événement</param>
    private void OnForgetPendingFiles(object sender, RoutedEventArgs e)
    {
        _pendingPaths.Clear();
        UpdatePendingNotice();
    }

    /// <summary>
    /// Met à jour l'avis des fichiers en attente, sur l'accueil.
    /// </summary>
    private void UpdatePendingNotice()
    {
        PendingFilesNotice.Visibility = _pendingPaths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PendingFilesText.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{_pendingPaths.Count} élément(s) déposé(s) en attente : ouvrez ou créez un projet, ils seront copiés aussitôt dans son dossier « in ».");
    }

    // ═══════════════════════════ Clavier et souris ══════════════════════════

    /// <summary>
    /// Raccourcis clavier de la fenêtre, et blocage de la saisie pendant une
    /// obfuscation.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Touche pressée</param>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_busy)
        {
            e.Handled = true;
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Le va-et-vient avec l'IA se fait au clavier : copier, pseudonymiser,
        // coller ; puis copier la réponse, restaurer, coller.
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.C or Key.V)
        {
            if (key == Key.C)
            {
                OnObfuscateClipboard(this, e);
            }
            else
            {
                OnDeobfuscateClipboard(this, e);
            }

            e.Handled = true;
            return;
        }

        // Dans l'aperçu, la sélection de texte se copie comme ailleurs. Hors de
        // l'aperçu — une zone de saisie, une grille —, les raccourcis restent
        // ceux du contrôle qui a le focus.
        if (PreviewTabs.IsKeyboardFocusWithin && HandlePreviewSelectionKey(key))
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.O:
                    ChooseProject(create: false);
                    e.Handled = true;
                    break;

                case Key.N:
                    ChooseProject(create: true);
                    e.Handled = true;
                    break;

                case >= Key.D1 and <= Key.D5:
                    OpenRecentByRank(key - Key.D1 + 1);
                    e.Handled = true;
                    break;

                case >= Key.NumPad1 and <= Key.NumPad5:
                    OpenRecentByRank(key - Key.NumPad1 + 1);
                    e.Handled = true;
                    break;

                default:
                    break;
            }

            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // Les trois gestes du cycle, au clavier : analyser, obfusquer,
        // rapatrier.
        switch (key)
        {
            case Key.F5:
                RequestAnalysis();
                e.Handled = true;
                break;

            case Key.F6 when ObfuscateButton.IsEnabled:
                OnObfuscate(this, e);
                e.Handled = true;
                break;

            case Key.F7 when ReturnButton.IsEnabled:
                OnReturn(this, e);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Raccourcis de la sélection de texte de l'aperçu : Ctrl+C ou Ctrl+Inser
    /// pour copier, Ctrl+A pour tout sélectionner, Échap pour désélectionner.
    /// </summary>
    /// <param name="key">Touche pressée</param>
    /// <returns><c>true</c> si la touche a été traitée</returns>
    private bool HandlePreviewSelectionKey(Key key)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.C or Key.Insert)
        {
            CopySelection();
            return true;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.A)
        {
            SelectAllInPreview();
            return true;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Escape && _selection is not null)
        {
            ForgetSelection();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Bloque la saisie de texte pendant une obfuscation.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Texte saisi</param>
    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_busy)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Valide le nom du projet quand l'utilisateur clique ailleurs, même sur
    /// une zone qui ne prend pas le focus.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Clic</param>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ProjectNameBox.IsKeyboardFocusWithin && !ProjectNameBox.IsMouseOver)
        {
            CommitProjectName();
            Keyboard.Focus(this);
        }
    }

    /// <summary>
    /// Termine proprement la session : nom enregistré, analyses annulées.
    /// Une obfuscation en cours empêche la fermeture, puisqu'elle doit encore
    /// enregistrer le coffre.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Données de l'événement</param>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_busy)
        {
            e.Cancel = true;
            return;
        }

        CommitProjectName();
        _tasks.CancelAll("fenêtre fermée");
        StopWatchingInputFolder();
        StopWatchingTarget();
        _preferences.Save();
        _tasksWindow?.Close();
    }

    // ══════════════════════════════ Utilitaires ═════════════════════════════

    /// <summary>
    /// Indique si un dossier abrite un projet.
    /// </summary>
    /// <param name="path">Chemin à examiner</param>
    /// <returns><c>true</c> si le dossier contient les données d'un projet</returns>
    private static bool IsProjectFolder(string path)
    {
        return Directory.Exists(path) && ObfuscationProject.IsProjectFolder(path);
    }

    /// <summary>
    /// Compare deux chemins de dossier.
    /// </summary>
    /// <param name="left">Premier chemin</param>
    /// <param name="right">Second chemin</param>
    /// <returns><c>true</c> s'ils désignent le même dossier</returns>
    private static bool SameFolder(string left, string right)
    {
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(separators),
            Path.GetFullPath(right).TrimEnd(separators),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Active ou désactive les actions selon l'état courant.
    /// </summary>
    private void UpdateActionState()
    {
        bool ready = _project is not null && _files.Count > 0 && !_busy;
        bool open = _project is not null && !_busy;

        ScanButton.IsEnabled = ready;

        // Une source vidée s'obfusque encore quand la cible a déjà reçu des
        // fichiers : leur retrait est proposé.
        ObfuscateButton.IsEnabled = ready || (open && _exchangeStatus is { HasExchanged: true });
        ReturnButton.IsEnabled = open;
        AddFilesButton.IsEnabled = open;
        OpenInputButton.IsEnabled = open;
        OpenOutputButton.IsEnabled = open;
        FilesList.IsEnabled = open;
        VaultProtectionSwitch.IsEnabled = open;

        foreach (UIElement control in new UIElement[]
        {
            LinkSourceButton, UnlinkSourceButton, OpenSourceButton,
            LinkTargetButton, UnlinkTargetButton, OpenTargetButton, WriteClaudeMdCheck,
            CommitSourceButton, PushSourceButton, CommitTargetButton, PushTargetButton,
        })
        {
            control.IsEnabled = open;
        }

        if (_files.Count > 0)
        {
            FilesCount.Text = string.Create(CultureInfo.CurrentCulture, $"{_files.Count} fichier(s)");
        }
        else
        {
            FilesCount.Text = _project switch
            {
                null => string.Empty,
                { IsSourceLinked: true } => "la source est vide",
                _ => "« in » est vide",
            };
        }

        UpdatePreviewEmptyHint();
    }

    /// <summary>
    /// Affiche un message d'état.
    /// </summary>
    /// <param name="message">Message à afficher</param>
    /// <param name="success">Vrai pour un succès, faux pour un échec, null pour une information</param>
    private void SetStatus(string message, bool? success = null)
    {
        StatusText.Text = message;

        // Le bandeau est borné à quelques lignes : un nouveau message se lit
        // depuis son début, et la console le garde en entier.
        StatusScroller.ScrollToTop();
        if (_statusJournal.Add(message, success, DateTimeOffset.Now) is StatusEntry entry)
        {
            _consoleWindow?.Append(entry);
        }

        StatusText.Foreground = Themed<Brush>(success switch
        {
            true => "Success",
            false => "Danger",
            _ => "TextSecondary",
        });

        StatusBorder.Background = Themed<Brush>(success switch
        {
            true => "SuccessDim",
            false => "DangerDim",
            _ => "Surface",
        });
    }

    /// <summary>
    /// Ouvre la console des messages d'état, ou la ramène au premier plan.
    /// </summary>
    /// <param name="sender">Bouton « Console » du bandeau</param>
    /// <param name="e">Données de l'événement</param>
    private void OnShowConsole(object sender, RoutedEventArgs e)
    {
        if (_consoleWindow is not null)
        {
            _consoleWindow.Activate();
            return;
        }

        _consoleWindow = new ConsoleWindow(_statusJournal)
        {
            Owner = this,
        };

        _consoleWindow.Closed += (_, _) => _consoleWindow = null;
        _consoleWindow.Show();
    }

    /// <summary>
    /// Récupère une ressource du thème.
    ///
    /// La recherche passe par TryFindResource et non par l'indexeur Resources :
    /// ce dernier ne consulte que le dictionnaire de la fenêtre, alors que le
    /// thème est fusionné au niveau de l'application.
    /// </summary>
    /// <typeparam name="T">Type attendu de la ressource</typeparam>
    /// <param name="key">Clé de la ressource</param>
    /// <returns>La ressource trouvée</returns>
    /// <exception cref="InvalidOperationException">Si la ressource est absente du thème</exception>
    private T Themed<T>(object key)
        where T : class
    {
        return TryFindResource(key) as T
            ?? throw new InvalidOperationException($"Ressource de thème introuvable : {key}");
    }

    /// <summary>
    /// Exécute une action courte en transformant toute erreur attendue en
    /// message lisible.
    ///
    /// Une exception non rattrapée fermerait la fenêtre en pleine session, avec
    /// un projet potentiellement à moitié écrit : mieux vaut un message et un
    /// état conservé.
    /// </summary>
    /// <param name="action">Action à exécuter</param>
    /// <returns><c>true</c> si l'action s'est déroulée sans erreur</returns>
    private bool Run(Action action)
    {
        Cursor? previous = Mouse.OverrideCursor;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            action();
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, success: false);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = previous;
        }
    }

    /// <summary>
    /// Indique si une erreur relève des aléas d'un poste de travail — fichier
    /// verrouillé, droits insuffisants, coffre illisible — plutôt que d'un
    /// défaut du programme.
    /// </summary>
    /// <param name="exception">Erreur survenue</param>
    /// <returns><c>true</c> si l'erreur peut être affichée et la session poursuivie</returns>
    private static bool IsExpected(Exception exception)
    {
        // Un coffre d'un format inconnu lève une InvalidDataException, qui n'est
        // pas une IOException : sans elle, un coffre abîmé fermerait la fenêtre.
        return exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException;
    }
}
