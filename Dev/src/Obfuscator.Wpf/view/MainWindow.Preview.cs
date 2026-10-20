using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Aperçu côte à côte : un onglet par fichier ouvert, lignes repliées ou non,
/// position de lecture conservée d'une analyse à l'autre.
///
/// Un aperçu s'ouvre à la demande : d'un double-clic dans la liste des fichiers,
/// ou de lui-même pour un fichier qu'on vient de déposer. Il reste ouvert
/// jusqu'à ce qu'on ferme son onglet — une nouvelle analyse le met à jour sans
/// le refermer, et il est retrouvé à la prochaine ouverture du projet. Ouvrir
/// d'office un onglet par fichier noyait l'écran dès que le lot dépassait
/// quelques fichiers.
///
/// L'en-tête de chaque onglet dit combien de valeurs signalées y restent à
/// trancher : c'est ce qui permet de voir, sans ouvrir chaque fichier, où le
/// travail n'est pas fini.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Au-delà de ce nombre de lignes, déplier un intervalle recompose la liste
    /// d'un bloc plutôt que ligne à ligne.
    /// </summary>
    private const int InPlaceExpansionLimit = 400;

    /// <summary>
    /// Nombre d'aperçus ouverts d'office par un dépôt : au-delà — un dossier
    /// entier déposé —, ouvrir un onglet par fichier noierait l'écran.
    /// </summary>
    private const int OpenedOnDrop = 5;

    /// <summary>
    /// Largeur de la colonne des décisions, la même pour l'en-tête et pour les
    /// lignes (gabarit <c>PreviewLineTemplate</c>) : trois petits boutons y
    /// tiennent de front.
    /// </summary>
    private const double DecisionColumnWidth = 214;

    /// <summary>Aperçu propriétaire de chaque repère, pour recomposer ses lignes.</summary>
    private readonly Dictionary<PreviewMarker, FilePreviewModel> _markerOwners = [];

    /// <summary>Liste d'aperçu de chaque fichier ouvert, par chemin.</summary>
    private readonly Dictionary<string, ListBox> _previewLists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>En-tête de chaque onglet ouvert, qui porte son compte de signalements, par chemin.</summary>
    private readonly Dictionary<string, PreviewTabHeader> _previewHeaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fichiers dont l'aperçu est ouvert, dans l'ordre de leurs onglets.</summary>
    private readonly List<string> _openPreviews = [];

    /// <summary>
    /// Intervalles de lignes dépliés par l'utilisateur, par chemin de fichier.
    /// Ils sont conservés d'une analyse à l'autre : une analyse relancée après
    /// un clic ne doit pas replier ce qu'on venait d'ouvrir.
    /// </summary>
    private readonly Dictionary<string, List<(int First, int Last)>> _expandedRanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si l'aperçu montre toutes les lignes, sans repli.</summary>
    private bool ShowsAllLines => ShowAllLinesCheck.IsChecked == true;

    /// <summary>
    /// Ce que l'aperçu met en avant, choisi par les boutons de la légende. Il
    /// vaut pour tous les onglets, et survit aux analyses.
    /// </summary>
    private PreviewFilter _previewFilter = PreviewFilter.All;

    // ═══════════════════════════ Ouverture et fermeture ═══════════════════════════

    /// <summary>
    /// Ouvre l'aperçu d'un fichier double-cliqué dans la liste.
    /// </summary>
    /// <param name="sender">Liste des fichiers</param>
    /// <param name="e">Double-clic</param>
    private void OnFilesListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Un double-clic sur la poubelle ne doit pas ouvrir l'aperçu du
        // fichier qu'on est en train de supprimer.
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(source) is not null
            || FindAncestor<ListBoxItem>(source)?.DataContext is not InputFileRow row)
        {
            return;
        }

        OpenPreview(row.FullPath, select: true);
        e.Handled = true;
    }

    /// <summary>
    /// Ouvre au clavier l'aperçu du fichier sélectionné.
    /// </summary>
    /// <param name="sender">Liste des fichiers</param>
    /// <param name="e">Touche pressée</param>
    private void OnFilesListKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter && FilesList.SelectedItem is InputFileRow row)
        {
            OpenPreview(row.FullPath, select: true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ouvre l'aperçu d'un fichier, ou montre son onglet s'il est déjà ouvert.
    /// </summary>
    /// <param name="path">Fichier du dossier « in »</param>
    /// <param name="select">Vrai pour afficher l'onglet</param>
    private void OpenPreview(string path, bool select)
    {
        if (_project is null || !_files.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        TabItem? tab = FindPreviewTab(path);
        if (tab is null)
        {
            if (!_openPreviews.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _openPreviews.Add(path);
                RememberOpenPreviews();
            }

            tab = BuildPreviewTab(path);
            PreviewTabs.Items.Add(tab);
            RefreshPreviewCounts();

            // Quand l'analyse auto est limitée aux fichiers ouverts, ouvrir un
            // aperçu laissé de côté le met à jour : c'est en l'ouvrant qu'on
            // dit vouloir le regarder.
            if (OpenFilesOnly && IsPreviewStale(path))
            {
                ScheduleAnalysis(ChangeAnalysisDelay, null, openOnly: true);
            }
        }

        if (select)
        {
            DetailTabs.SelectedItem = PreviewTab;
            PreviewTabs.SelectedItem = tab;
        }

        UpdatePreviewEmptyHint();
    }

    /// <summary>
    /// Ouvre les aperçus des fichiers qu'on vient de déposer ou d'ajouter, dans
    /// la limite de quelques onglets, et montre le dernier.
    /// </summary>
    /// <param name="paths">Fichiers du dossier « in » à ouvrir</param>
    private void OpenDroppedPreviews(IReadOnlyList<string> paths)
    {
        List<string> opened = [.. paths
            .Where(path => _files.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(OpenedOnDrop)];

        for (int index = 0; index < opened.Count; index++)
        {
            OpenPreview(opened[index], select: index == opened.Count - 1);
        }
    }

    /// <summary>
    /// Ferme un onglet d'aperçu depuis sa croix.
    /// </summary>
    /// <param name="sender">Croix de l'onglet</param>
    /// <param name="e">Données de l'événement</param>
    private void OnClosePreviewTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PreviewTabHeader header)
        {
            ClosePreview(header.Path);
        }
    }

    /// <summary>
    /// Ferme un onglet d'aperçu d'un clic du bouton du milieu, comme dans un
    /// navigateur.
    /// </summary>
    /// <param name="sender">Onglet</param>
    /// <param name="e">Clic</param>
    private void OnPreviewTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ChangedButton == MouseButton.Middle && (sender as TabItem)?.Tag is string path)
        {
            ClosePreview(path);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ferme l'aperçu d'un fichier. Le fichier reste dans le lot : seul son
    /// onglet disparaît.
    /// </summary>
    /// <param name="path">Fichier concerné</param>
    private void ClosePreview(string path)
    {
        if (_previewLists.TryGetValue(path, out ListBox? closing) && closing.Tag is FilePreviewModel closed)
        {
            ForgetSelection(closed);
        }

        TabItem? tab = FindPreviewTab(path);
        if (tab is not null)
        {
            int index = PreviewTabs.Items.IndexOf(tab);
            bool selected = ReferenceEquals(PreviewTabs.SelectedItem, tab);
            PreviewTabs.Items.Remove(tab);

            // L'onglet voisin prend la place de celui qu'on ferme, comme dans
            // un navigateur, plutôt que de laisser l'aperçu sans sélection.
            if (selected && PreviewTabs.Items.Count > 0)
            {
                PreviewTabs.SelectedIndex = Math.Min(index, PreviewTabs.Items.Count - 1);
            }
        }

        _openPreviews.RemoveAll(open => string.Equals(open, path, StringComparison.OrdinalIgnoreCase));
        _previewLists.Remove(path);
        _previewHeaders.Remove(path);
        RememberOpenPreviews();
        UpdatePreviewEmptyHint();
    }

    /// <summary>
    /// Referme les aperçus des fichiers qui ont quitté le dossier « in ».
    /// </summary>
    private void PrunePreviews()
    {
        foreach (string path in _openPreviews.Where(path => !_files.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            ClosePreview(path);
        }
    }

    /// <summary>
    /// Reprend les aperçus laissés ouverts lors de la dernière séance sur ce
    /// projet. Leurs onglets apparaissent avec l'analyse.
    /// </summary>
    /// <param name="project">Projet qui vient d'être ouvert</param>
    private void RestoreOpenPreviews(ObfuscationProject project)
    {
        // Les aperçus sont retenus relativement à la source : « in », ou le
        // dossier en clair lié.
        _openPreviews.Clear();
        string source = project.SourcePath;
        foreach (string relative in _preferences.OpenPreviewsOf(project.RootPath))
        {
            string path = Path.GetFullPath(Path.Combine(source, relative));

            // Une préférence modifiée à la main ne doit pas ouvrir un fichier
            // hors de la source.
            if (IsInside(path, source) && !_openPreviews.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _openPreviews.Add(path);
            }
        }
    }

    /// <summary>
    /// Enregistre les aperçus ouverts du projet courant, pour les retrouver à
    /// sa prochaine ouverture.
    /// </summary>
    private void RememberOpenPreviews()
    {
        if (_project is null)
        {
            return;
        }

        string source = _project.SourcePath;
        _preferences.SetOpenPreviews(_project.RootPath, _openPreviews.Select(path => Path.GetRelativePath(source, path)));
        _preferences.Save();
    }

    // ═══════════════════════════════ Onglets ═══════════════════════════════

    /// <summary>
    /// Raccorde les aperçus d'une nouvelle analyse aux décisions de l'écran :
    /// chaque repère connaît son fichier. Tous les fichiers le sont, ouverts ou
    /// non : un onglet ouvert ensuite doit réagir comme les autres.
    /// </summary>
    private void WirePreviewModels()
    {
        _markerOwners.Clear();
        if (_analysis is null)
        {
            return;
        }

        foreach (FilePreviewModel file in _analysis.Files)
        {
            foreach (PreviewMarker marker in file.Markers)
            {
                _markerOwners[marker] = file;
            }
        }
    }

    /// <summary>
    /// Reconstruit les onglets des aperçus ouverts, en conservant le fichier
    /// affiché et la ligne qu'on y lisait.
    /// </summary>
    private void RebuildPreviewTabs()
    {
        string? selectedPath = (PreviewTabs.SelectedItem as TabItem)?.Tag as string;
        int? readingLine = selectedPath is not null && _previewLists.TryGetValue(selectedPath, out ListBox? current)
            ? FirstVisibleLine(current)
            : null;

        // La sélection désigne les lignes de l'analyse précédente, que les
        // nouveaux onglets ne montrent plus.
        ForgetSelection();
        PreviewTabs.Items.Clear();
        _previewLists.Clear();
        _previewHeaders.Clear();

        // Seuls les fichiers du lot ont un onglet. Les autres ne sont pas
        // oubliés pour autant : c'est la relecture du dossier « in » qui les
        // retire, une fois qu'elle a réussi — une lecture empêchée, au moment
        // d'ouvrir le projet, ne doit pas effacer les aperçus retenus.
        TabItem? reselect = null;
        foreach (string path in _openPreviews.Where(path => _files.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            TabItem tab = BuildPreviewTab(path);
            PreviewTabs.Items.Add(tab);

            if (string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                reselect = tab;
            }
        }

        RefreshPreviewCounts();
        UpdatePreviewEmptyHint();

        if (PreviewTabs.Items.Count == 0)
        {
            return;
        }

        PreviewTabs.SelectedItem = reselect ?? PreviewTabs.Items[0];

        if (reselect is not null && readingLine is int line && _previewLists.TryGetValue(selectedPath!, out ListBox? list))
        {
            ScrollToLineWhenLoaded(list, line);
        }
    }

    /// <summary>
    /// Compose l'onglet d'un fichier : son aperçu s'il a été analysé, une
    /// attente sinon — l'analyse qui l'inclut est en route.
    /// </summary>
    /// <param name="path">Fichier du dossier « in »</param>
    /// <returns>L'onglet prêt à être ajouté</returns>
    private TabItem BuildPreviewTab(string path)
    {
        FilePreviewModel? file = FindPreviewModel(path);

        PreviewTabHeader header = new(path);
        _previewHeaders[path] = header;

        TabItem tab = new()
        {
            Header = header,
            HeaderTemplate = Themed<DataTemplate>("PreviewTabHeaderTemplate"),
            Tag = path,
            ToolTip = path,
        };

        tab.PreviewMouseUp += OnPreviewTabMouseUp;

        if (file is null)
        {
            tab.Content = new TextBlock
            {
                Text = "Ce fichier n'a pas encore été analysé : son aperçu s'affichera dès la fin de l'analyse en cours, ou de la prochaine (F5).",
                Style = Themed<Style>("Hint"),
                Margin = new Thickness(4, 12, 4, 0),
            };

            return tab;
        }

        ListBox list = new()
        {
            Style = Themed<Style>("PreviewList"),
            ItemTemplateSelector = Themed<DataTemplateSelector>("PreviewItemSelector"),
            ItemsSource = DisplayOf(file),
            Tag = file,
        };

        WireSelection(list);
        _previewLists[path] = list;

        Grid columns = BuildPreviewHeader(file);
        DockPanel.SetDock(columns, Dock.Top);

        DockPanel panel = new();
        panel.Children.Add(columns);
        panel.Children.Add(list);
        tab.Content = panel;

        return tab;
    }

    /// <summary>
    /// Met à jour, dans l'en-tête de chaque onglet, le nombre de valeurs
    /// signalées qui restent à trancher. Masquer ou garder une valeur le fait
    /// baisser aussitôt, dans tous les fichiers où elle apparaît.
    /// </summary>
    private void RefreshPreviewCounts()
    {
        foreach (TabItem tab in PreviewTabs.Items.OfType<TabItem>())
        {
            if (tab.Tag is not string path || !_previewHeaders.TryGetValue(path, out PreviewTabHeader? header))
            {
                continue;
            }

            FilePreviewModel? file = FindPreviewModel(path);
            int suspects = file?.CountOf(PreviewMarkerState.Suspect) ?? 0;
            string remaining = suspects == 0
                ? "aucun signalement à trancher"
                : string.Create(CultureInfo.CurrentCulture, $"{suspects} signalement(s) à trancher");

            header.Suspects = suspects;
            tab.ToolTip = file is null
                ? path
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{path}\nformat {file.Preview.Kind} · encodage {file.Preview.EncodingName} · {file.Rows.Length} ligne(s) · {file.Preview.Spans.Count} remplacement(s) · {remaining}");
        }

        UpdateFilterCounts();
    }

    /// <summary>
    /// Donne aux boutons du filtre le compte de chaque sorte dans le fichier
    /// affiché ; sans fichier affiché, ils ne portent que leur nom.
    /// </summary>
    private void UpdateFilterCounts()
    {
        FilePreviewModel? file = (PreviewTabs.SelectedItem as TabItem)?.Tag is string path ? FindPreviewModel(path) : null;

        FilterMaskedButton.Content = Label("remplacé", PreviewMarkerState.Masked);
        FilterSuspectButton.Content = Label("signalé, à trancher", PreviewMarkerState.Suspect);
        FilterPendingButton.Content = Label("à masquer", PreviewMarkerState.PendingMask);

        string Label(string name, PreviewMarkerState state) => file is null
            ? name
            : string.Create(CultureInfo.CurrentCulture, $"{name} · {file.CountOf(state)}");
    }

    /// <summary>
    /// Suit l'onglet d'aperçu affiché, pour les comptes du filtre.
    /// </summary>
    /// <param name="sender">Onglets des aperçus</param>
    /// <param name="e">Changement de sélection</param>
    private void OnPreviewTabSelected(object sender, SelectionChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Les sélections des listes d'aperçu remontent jusqu'ici : seules
        // comptent celles des onglets eux-mêmes.
        if (ReferenceEquals(e.OriginalSource, PreviewTabs))
        {
            UpdateFilterCounts();
        }
    }

    /// <summary>
    /// Change ce que l'aperçu met en avant.
    /// </summary>
    /// <param name="sender">Bouton du filtre</param>
    /// <param name="e">Données de l'événement</param>
    private void OnPreviewFilterClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse(tag, out PreviewFilter filter))
        {
            return;
        }

        _previewFilter = filter;
        RedisplayPreviews();

        // Un filtre sans rien à montrer replie tout le fichier : on dit
        // pourquoi plutôt que de laisser croire à un aperçu vide.
        FilePreviewModel? file = (PreviewTabs.SelectedItem as TabItem)?.Tag is string path ? FindPreviewModel(path) : null;
        (PreviewMarkerState State, string Name)? kind = filter switch
        {
            PreviewFilter.Masked => (PreviewMarkerState.Masked, "remplacé"),
            PreviewFilter.Suspect => (PreviewMarkerState.Suspect, "signalé, à trancher"),
            PreviewFilter.PendingMask => (PreviewMarkerState.PendingMask, "à masquer"),
            _ => null,
        };

        if (file is null || kind is not (PreviewMarkerState state, string name))
        {
            return;
        }

        string effect = ShowsAllLines ? "toutes ses lignes sont estompées" : "tout le fichier est replié";
        string others = ShowsAllLines ? "les autres lignes sont estompées" : "seules ces lignes restent, avec leur contexte";
        SetStatus(file.CountOf(state) == 0
            ? $"Rien de « {name} » dans {file.FileName} : {effect}. « Tout afficher » revient à l'aperçu complet."
            : $"Aperçu filtré sur « {name} » : {others}. « Tout afficher » revient à l'aperçu complet.");
    }

    /// <summary>
    /// Affiche l'invitation à ouvrir un aperçu quand aucun onglet n'est ouvert.
    /// Sans fichier ou sans analyse, c'est l'annonce centrale qui parle.
    /// </summary>
    private void UpdatePreviewEmptyHint()
    {
        PreviewEmptyHint.Visibility = _project is not null && _files.Count > 0 && PreviewTabs.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Retrouve l'onglet ouvert d'un fichier.
    /// </summary>
    /// <param name="path">Fichier concerné</param>
    /// <returns>L'onglet, ou <c>null</c> s'il n'est pas ouvert</returns>
    private TabItem? FindPreviewTab(string path)
    {
        return PreviewTabs.Items.OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Tag as string, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Retrouve l'aperçu d'un fichier dans la dernière analyse.
    /// </summary>
    /// <param name="path">Fichier concerné</param>
    /// <returns>L'aperçu, ou <c>null</c> si le fichier n'a pas été analysé</returns>
    private FilePreviewModel? FindPreviewModel(string path)
    {
        return _analysis?.Files.FirstOrDefault(file => string.Equals(file.Preview.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════════════════════ Contenu ══════════════════════════════

    /// <summary>
    /// Compose la liste affichée d'un fichier, avec les intervalles dépliés.
    /// </summary>
    /// <param name="file">Aperçu du fichier</param>
    /// <returns>La liste à présenter</returns>
    private ObservableCollection<object> DisplayOf(FilePreviewModel file)
    {
        IReadOnlyCollection<(int First, int Last)> expanded =
            _expandedRanges.TryGetValue(file.Preview.Path, out List<(int First, int Last)>? ranges) ? ranges : [];

        return new ObservableCollection<object>(file.Display(ShowsAllLines, expanded, _previewFilter));
    }

    /// <summary>
    /// Compose l'en-tête d'un aperçu : les deux volets y sont nommés, sans quoi
    /// rien ne dit lequel est l'original. Ses colonnes reprennent exactement
    /// celles des lignes.
    /// </summary>
    /// <param name="file">Aperçu du fichier</param>
    /// <returns>L'en-tête prêt à être affiché</returns>
    private Grid BuildPreviewHeader(FilePreviewModel file)
    {
        // La marge de droite réserve la place de la barre de défilement de la
        // liste, pour que les colonnes proportionnelles s'alignent.
        Grid header = new() { Margin = new Thickness(1, 0, 12, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DecisionColumnWidth) });

        double gutterWidth = file.Rows.Length > 0 ? file.Rows[0].GutterWidth : 12;
        Border gutter = new() { Width = gutterWidth + 16 };

        const string Selecting = "\n\nSélectionnez du texte à la souris — double-clic : un mot, triple-clic : la ligne, Maj+clic : jusqu'ici. Ctrl+C copie, Ctrl+A sélectionne tout, clic droit pour masquer ou garder le passage.";

        TextBlock left = HeaderText("ORIGINAL");
        left.ToolTip = "Le fichier tel qu'il est, valeurs réelles comprises." + Selecting;
        TextBlock right = HeaderText(string.Create(
            CultureInfo.CurrentCulture,
            $"PSEUDONYMISÉ  ·  {file.Preview.Spans.Count} remplacement(s)"));
        right.ToolTip = "Le fichier tel qu'il sera envoyé. Copier ce volet enregistre ses pseudonymes au coffre : la réponse de l'IA pourra être restaurée." + Selecting;
        TextBlock actions = HeaderText("DÉCISION");
        actions.ToolTip = "Valeur masquée : « Garder ici » la laisse en clair à cet endroit seulement ; « Partout » l'ajoute à la liste blanche du projet.\nValeur signalée : « Masquer » l'ajoute aux règles du projet, « Garder ici » et « Partout » la laissent en clair.\n« annuler » revient sur une décision prise.";

        header.Children.Add(gutter);
        header.Children.Add(left);
        header.Children.Add(right);
        header.Children.Add(actions);
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(left, 1);
        Grid.SetColumn(right, 2);
        Grid.SetColumn(actions, 3);

        return header;

        TextBlock HeaderText(string text) => new()
        {
            Text = text,
            Style = Themed<Style>("SectionTitle"),
            Margin = new Thickness(0),
        };
    }

    /// <summary>
    /// Montre toutes les lignes, ou seulement les lignes utiles — celles que
    /// le filtre met en avant.
    /// </summary>
    /// <param name="sender">Case à cocher</param>
    /// <param name="e">Données de l'événement</param>
    private void OnToggleAllLines(object sender, RoutedEventArgs e)
    {
        RedisplayPreviews();
    }

    /// <summary>
    /// Recompose la liste de chaque aperçu ouvert, selon le repli et le
    /// filtre, en gardant la ligne qu'on y lisait.
    /// </summary>
    private void RedisplayPreviews()
    {
        foreach (ListBox list in _previewLists.Values)
        {
            if (list.Tag is not FilePreviewModel file)
            {
                continue;
            }

            int? line = list.IsLoaded ? FirstVisibleLine(list) : null;
            list.ItemsSource = DisplayOf(file);

            if (line is int reading)
            {
                ScrollToLineWhenLoaded(list, reading);
            }
        }
    }

    /// <summary>
    /// Déplie un intervalle de lignes cachées.
    ///
    /// Un intervalle raisonnable est déplié sur place : les lignes s'insèrent
    /// là où était le repli, et ce qu'on lisait ne bouge pas. Un très grand
    /// intervalle recompose la liste d'un bloc, ce qui reste instantané, puis
    /// amène sa première ligne en haut de l'écran.
    /// </summary>
    /// <param name="sender">Ligne de repli</param>
    /// <param name="e">Données de l'événement</param>
    private void OnExpandGap(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreviewGapRow gap)
        {
            return;
        }

        ListBox? list = FindAncestor<ListBox>(sender as DependencyObject);
        if (list?.Tag is not FilePreviewModel file || list.ItemsSource is not ObservableCollection<object> items)
        {
            return;
        }

        if (!_expandedRanges.TryGetValue(file.Preview.Path, out List<(int First, int Last)>? ranges))
        {
            ranges = [];
            _expandedRanges[file.Preview.Path] = ranges;
        }

        ranges.Add((gap.FirstLine, gap.LastLine));
        int index = items.IndexOf(gap);

        if (index >= 0 && gap.Count <= InPlaceExpansionLimit)
        {
            items.RemoveAt(index);
            for (int line = gap.FirstLine; line <= gap.LastLine; line++)
            {
                items.Insert(index + (line - gap.FirstLine), file.Rows[line - 1]);
            }

            return;
        }

        list.ItemsSource = DisplayOf(file);
        ScrollToLineWhenLoaded(list, gap.FirstLine);
    }

    /// <summary>
    /// Retrouve le numéro de la première ligne visible d'un aperçu.
    ///
    /// Seuls les éléments réellement construits sont examinés — une
    /// cinquantaine au plus grâce à la virtualisation —, dans l'ordre où la
    /// liste les a disposés.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <returns>Le numéro de ligne, ou <c>null</c> si la liste n'est pas affichée</returns>
    private static int? FirstVisibleLine(ListBox list)
    {
        if (!list.IsLoaded || FindDescendant<VirtualizingStackPanel>(list) is not VirtualizingStackPanel panel)
        {
            return null;
        }

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement container || !container.IsVisible)
            {
                continue;
            }

            double bottom = container.TranslatePoint(new Point(0, container.ActualHeight), list).Y;
            if (bottom <= 1)
            {
                continue;
            }

            return container.DataContext switch
            {
                PreviewLineRow row => row.Number,
                PreviewGapRow gap => gap.FirstLine,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Amène une ligne en haut d'un aperçu, dès que la liste est affichée.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="line">Numéro de la ligne à montrer</param>
    private void ScrollToLineWhenLoaded(ListBox list, int line)
    {
        if (list.IsLoaded)
        {
            Dispatcher.BeginInvoke(() => ScrollToLine(list, line), DispatcherPriority.Loaded);
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            list.Loaded -= OnLoaded;
            Dispatcher.BeginInvoke(() => ScrollToLine(list, line), DispatcherPriority.Loaded);
        }

        list.Loaded += OnLoaded;
    }

    /// <summary>
    /// Amène une ligne en haut d'un aperçu.
    ///
    /// Faire défiler vers un élément ne l'amène qu'au bord le plus proche :
    /// en passant d'abord par la fin de la liste, l'élément visé arrive par le
    /// haut, là où l'œil le cherche.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="line">Numéro de la ligne à montrer</param>
    private static void ScrollToLine(ListBox list, int line)
    {
        object? target = null;
        foreach (object item in list.Items)
        {
            if ((item is PreviewLineRow row && row.Number >= line) || (item is PreviewGapRow gap && gap.LastLine >= line))
            {
                target = item;
                break;
            }
        }

        if (target is null || list.Items.Count == 0)
        {
            return;
        }

        list.ScrollIntoView(list.Items[^1]);
        list.UpdateLayout();
        list.ScrollIntoView(target);
    }

    /// <summary>
    /// Descend l'arbre visuel jusqu'au premier descendant d'un type donné.
    /// </summary>
    /// <typeparam name="T">Type recherché</typeparam>
    /// <param name="node">Point de départ</param>
    /// <returns>Le descendant trouvé, ou <c>null</c></returns>
    private static T? FindDescendant<T>(DependencyObject node)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, index);
            if (child is T found)
            {
                return found;
            }

            if (FindDescendant<T>(child) is T nested)
            {
                return nested;
            }
        }

        return null;
    }
}
