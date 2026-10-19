using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Model.Reporting;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Décisions de l'utilisateur : règles de masquage, valeurs, propositions et
/// occurrences.
///
/// Toutes suivent le même chemin : la décision est portée au projet et
/// enregistrée, puis l'écran la reflète. Aucune ne relance l'analyse d'elle-même
/// — c'est ce qui rend la revue fluide. Celles que l'écran sait refléter
/// exactement (une occurrence exceptée, une proposition masquée ou gardée) le
/// sont sur-le-champ ; les autres rendent l'aperçu « dépassé », ce qu'il
/// annonce.
/// </summary>
public partial class MainWindow
{
    /// <summary>Nombre de valeurs par catégorie relevé par la dernière analyse.</summary>
    private IReadOnlyList<(SpanCategory Category, int Occurrences, int Distinct)> _categoryCounts = [];

    /// <summary>
    /// Porte une décision au projet et l'enregistre.
    /// </summary>
    /// <param name="decision">Décision à porter</param>
    /// <param name="message">Phrase décrivant la décision prise</param>
    /// <returns><c>true</c> si la décision a été enregistrée</returns>
    private bool Decide(Action<ReviewPresenter> decision, string message)
    {
        if (_project is null || _busy)
        {
            return false;
        }

        ObfuscationProject project = _project;
        bool done = Run(() =>
        {
            ReviewPresenter presenter = new(project);
            decision(presenter);
            presenter.Commit();
            _currentFingerprint = project.RulesFingerprint();
        });

        if (!done)
        {
            return false;
        }

        // La liste blanche et les occurrences gardées sont relues du projet :
        // l'onglet qui les montre suit chaque décision.
        RebuildWhitelistView();

        // Toute décision relance l'analyse en arrière-plan, après un court
        // délai qui regroupe les clics rapprochés : l'écran reflète la décision
        // sur-le-champ, et l'analyse confirme ensuite partout — pseudonymes,
        // autres occurrences, autres fichiers. Relancer annule l'analyse en
        // cours, dont le résultat serait déjà dépassé.
        if (_preferences.AnalyseAfterEachChange && _analysis is not null)
        {
            SetStatus($"{message} L'aperçu se met à jour en arrière-plan.");
            ScheduleAnalysis(ChangeAnalysisDelay, null);
        }
        else
        {
            SetStatus(IsStale
                ? $"{message} Analyse nécessaire pour en voir l'effet partout dans l'aperçu."
                : message);
        }

        RefreshAnalysisState();
        return true;
    }

    // ═══════════════════════════ Règles de masquage ═════════════════════════

    /// <summary>
    /// Reconstruit la liste « Ce qui sera masqué » : les catégories relevées
    /// ou désactivées, puis les termes retenus, lus depuis le projet.
    /// </summary>
    private void RebuildMaskTargets()
    {
        foreach (MaskTargetRow existing in _maskTargets)
        {
            existing.SelectionChanged -= OnMaskTargetChanged;
        }

        _maskTargets.Clear();
        if (_project is null)
        {
            return;
        }

        HashSet<string> disabled = [.. _project.Settings.DisabledCategories];

        foreach ((SpanCategory category, int occurrences, int distinct) in _categoryCounts
            .OrderByDescending(entry => entry.Occurrences))
        {
            Add(MaskTargetRow.ForCategory(category, occurrences, distinct, !disabled.Contains(category.ToString())));
        }

        // Une catégorie décochée ne produit plus rien, donc plus aucune ligne
        // dans le relevé : sans cet ajout, elle disparaîtrait de la liste et
        // ne pourrait plus être recochée.
        foreach (string name in _project.Settings.DisabledCategories)
        {
            if (Enum.TryParse(name, ignoreCase: true, out SpanCategory category)
                && !_categoryCounts.Any(entry => entry.Category == category))
            {
                Add(MaskTargetRow.ForDisabledCategory(category));
            }
        }

        foreach (VocabularyTerm term in _project.Vocabulary.Terms
            .OrderBy(term => term.Text, StringComparer.CurrentCultureIgnoreCase))
        {
            Add(MaskTargetRow.ForTerm(term.Text, term.Category));
        }

        void Add(MaskTargetRow row)
        {
            row.SelectionChanged += OnMaskTargetChanged;
            _maskTargets.Add(row);
        }
    }

    /// <summary>
    /// Reconstruit la liste des règles après la fin de l'événement en cours :
    /// la ligne qui l'a déclenché peut en disparaître, ce qu'on ne fait pas
    /// pendant que sa case est encore en train de basculer.
    /// </summary>
    private void DeferRebuildMaskTargets()
    {
        Dispatcher.BeginInvoke(RebuildMaskTargets, DispatcherPriority.Background);
    }

    /// <summary>
    /// Applique une case cochée ou décochée dans la liste des règles.
    /// </summary>
    /// <param name="sender">Ligne modifiée</param>
    /// <param name="e">Données de l'événement</param>
    private void OnMaskTargetChanged(object? sender, EventArgs e)
    {
        if (sender is not MaskTargetRow row)
        {
            return;
        }

        if (row.Kind == MaskTargetKind.Category)
        {
            SetCategoryMasked(row, row.IsSelected);
            return;
        }

        if (row.Term is null)
        {
            return;
        }

        string term = row.Term;
        SpanCategory category = row.Category;
        bool selected = row.IsSelected;

        bool done = Decide(
            presenter =>
            {
                if (selected)
                {
                    presenter.AddMaskedTerm(term, category);
                }
                else
                {
                    presenter.RemoveMaskedTerm(term);
                }
            },
            $"Terme « {term} » {(selected ? "ajouté aux" : "retiré des")} règles.");

        if (done)
        {
            ApplyProposalState(term, selected ? PreviewMarkerState.PendingMask : PreviewMarkerState.Suspect, selected ? SuggestionState.Accepted : SuggestionState.Proposed);
        }

        DeferRebuildMaskTargets();
    }

    /// <summary>
    /// Masque ou laisse en clair toute une famille de valeurs.
    /// </summary>
    /// <param name="row">Ligne de la catégorie</param>
    /// <param name="masked">Vrai pour masquer</param>
    private void SetCategoryMasked(MaskTargetRow row, bool masked)
    {
        SpanCategory category = row.Category;

        bool done = Decide(
            presenter =>
            {
                if (masked)
                {
                    presenter.EnableCategory(category);
                }
                else
                {
                    presenter.DisableCategory(category);
                }
            },
            $"« {row.Label} » : {(masked ? "sera masqué" : "restera en clair")}.");

        if (!done)
        {
            row.SetSelectedSilently(!masked);
        }
    }

    /// <summary>
    /// Ouvre le menu d'une ligne des règles : changer la nature d'un terme, le
    /// retirer, ou basculer une famille.
    /// </summary>
    /// <param name="sender">Grille des règles</param>
    /// <param name="e">Clic droit</param>
    private void OnMaskTargetRightClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Le clic droit ne sélectionne pas la ligne : c'est la ligne sous le
        // curseur qui compte, pas celle sélectionnée auparavant.
        DataGridRow? gridRow = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (gridRow?.Item is not MaskTargetRow row || _project is null || _busy)
        {
            return;
        }

        gridRow.IsSelected = true;
        ContextMenu menu = new()
        {
            PlacementTarget = gridRow,
            Placement = PlacementMode.MousePoint,
        };

        if (row.Kind == MaskTargetKind.Category)
        {
            menu.Items.Add(new MenuItem { Header = MenuText($"« {row.Label} » — famille détectée par le moteur"), IsEnabled = false });
            menu.Items.Add(new Separator());

            MenuItem toggle = new() { Header = "Masquer cette famille", IsCheckable = true, IsChecked = row.IsSelected };
            toggle.Click += (_, _) =>
            {
                bool masked = !row.IsSelected;
                row.SetSelectedSilently(masked);
                SetCategoryMasked(row, masked);
            };

            menu.Items.Add(toggle);
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (row.Term is null)
        {
            return;
        }

        string term = row.Term;

        // Une adresse ou un début d'adresse est traité comme une adresse, quelle
        // que soit la nature choisie : proposer d'en changer laisserait croire
        // à un autre pseudonyme.
        if (IpAddressTools.TryParseAddressOrPrefix(term, out _, out _))
        {
            menu.Items.Add(new MenuItem { Header = MenuText($"« {Shorten(term)} » est une adresse : son pseudonyme garde la forme d'une adresse"), IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new MenuItem { Header = MenuText($"Traiter « {Shorten(term)} » comme…"), IsEnabled = false });
            menu.Items.Add(new Separator());
            AddCategoryChoices(menu, row, term);
        }

        menu.Items.Add(new Separator());
        MenuItem remove = new() { Header = "Retirer des règles" };
        remove.Click += (_, _) =>
        {
            if (Decide(presenter => presenter.RemoveMaskedTerm(term), $"Terme « {term} » retiré des règles."))
            {
                ApplyProposalState(term, PreviewMarkerState.Suspect, SuggestionState.Proposed);
                DeferRebuildMaskTargets();
            }
        };

        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Ajoute au menu d'un terme les natures qu'on peut lui donner. Un terme
    /// qui n'a pas la forme d'une adresse ne peut pas en devenir une : son
    /// pseudonyme en serait une.
    /// </summary>
    /// <param name="menu">Menu en cours de composition</param>
    /// <param name="row">Ligne du terme</param>
    /// <param name="term">Terme concerné</param>
    private void AddCategoryChoices(ContextMenu menu, MaskTargetRow row, string term)
    {
        foreach (CategoryChoice choice in CategoryChoices().Where(choice => choice.Value != SpanCategory.IpV4))
        {
            MenuItem item = new()
            {
                Header = choice.Label,
                IsCheckable = true,
                IsChecked = choice.Value == row.Category,
            };

            SpanCategory target = choice.Value;
            item.Click += (_, _) =>
            {
                if (target != row.Category && Decide(
                        presenter => presenter.ChangeTermCategory(term, target),
                        $"« {term} » est désormais traité comme : {CategoryLabels.Label(target)}."))
                {
                    DeferRebuildMaskTargets();
                }
            };

            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Ajoute à la main un terme à masquer.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAddTerm(object sender, RoutedEventArgs e)
    {
        string term = NewTermBox.Text.Trim();
        if (term.Length == 0 || _project is null)
        {
            return;
        }

        if (term.Length < Vocabulary.MinimumTermLength)
        {
            SetStatus($"Terme trop court : il faut au moins {Vocabulary.MinimumTermLength} caractères.", success: false);
            return;
        }

        SpanCategory category = (NewTermCategory.SelectedItem as CategoryChoice)?.Value ?? SpanCategory.Organization;
        bool address = ReviewPresenter.IsAddressLike(term);

        if (category == SpanCategory.IpV4 && !address)
        {
            SetStatus(
                $"« {Shorten(term)} » n'est ni une adresse IPv4 ni un début d'adresse (192.168.1.25, 192.168.1, 10.10) : choisissez une autre nature.",
                success: false);
            return;
        }

        string treated = address && category != SpanCategory.IpV4
            ? " Il a la forme d'une adresse : il est traité comme une adresse IPv4."
            : string.Empty;

        if (Decide(
                presenter =>
                {
                    presenter.RestoreSuggestion(term);
                    presenter.AddMaskedTerm(term, category);
                },
                $"Terme « {term} » ajouté : il sera masqué partout où il apparaît.{treated}"))
        {
            NewTermBox.Clear();
            ApplyProposalState(term, PreviewMarkerState.PendingMask, SuggestionState.Accepted);
            DeferRebuildMaskTargets();
        }
    }

    /// <summary>
    /// Valide la saisie d'un terme à la touche Entrée.
    /// </summary>
    /// <param name="sender">Zone de saisie</param>
    /// <param name="e">Touche pressée</param>
    private void OnNewTermKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter)
        {
            OnAddTerm(sender, e);
            e.Handled = true;
        }
    }

    // ═════════════════════════════ Valeurs repérées ═════════════════════════

    /// <summary>
    /// Remplit la grille des valeurs repérées.
    /// </summary>
    /// <param name="report">Relevé de l'analyse</param>
    private void FillFindings(ScanReport report)
    {
        foreach (FindingRow existing in _findings)
        {
            existing.SelectionChanged -= OnFindingChanged;
        }

        List<FindingRow> rows = new(report.Findings.Count);
        foreach (ScanFinding finding in report.Findings)
        {
            FindingRow row = new(finding);
            row.SetSelectedSilently(true);
            row.SelectionChanged += OnFindingChanged;
            rows.Add(row);
        }

        // Une collection neuve plutôt que des ajouts un à un : la grille ne se
        // met à jour qu'une fois, même pour des milliers de valeurs.
        _findings = new ObservableCollection<FindingRow>(rows);
        FindingsGrid.ItemsSource = _findings;
    }

    /// <summary>
    /// Applique une valeur cochée ou décochée dans la grille des valeurs
    /// repérées : elle vaut pour toutes les occurrences, dans tous les fichiers.
    /// Décochée, la valeur rejoint la liste blanche du projet.
    /// </summary>
    /// <param name="sender">Ligne modifiée</param>
    /// <param name="e">Données de l'événement</param>
    private void OnFindingChanged(object? sender, EventArgs e)
    {
        if (sender is not FindingRow row)
        {
            return;
        }

        string value = row.Value;
        bool selected = row.IsSelected;

        bool done = Decide(
            presenter =>
            {
                if (selected)
                {
                    presenter.AlwaysObfuscate(value);
                }
                else
                {
                    presenter.KeepValue(value);
                }
            },
            selected
                ? $"Valeur « {Shorten(value)} » masquée partout."
                : $"Valeur « {Shorten(value)} » ajoutée à la liste blanche : en clair partout.");

        if (done)
        {
            ApplyLiveStates(value);
        }
        else
        {
            row.SetSelectedSilently(!selected);
        }
    }

    // ═══════════════════════════════ Propositions ═══════════════════════════

    /// <summary>
    /// Remplit la liste des suggestions : mots récurrents proposés par
    /// l'heuristique, puis chaînes signalées par l'analyse de résidus.
    /// </summary>
    /// <param name="outcome">Résultat de l'analyse</param>
    private void FillSuggestions(AnalysisOutcome outcome)
    {
        List<SuggestionRow> rows = [];

        foreach (VocabularyCandidate candidate in outcome.Result.Scan.Candidates)
        {
            rows.Add(SuggestionRow.ForCandidate(candidate));
        }

        // Une occurrence gardée en clair à son endroit n'est plus à trancher, et
        // une valeur gardée partout n'est plus relevée du tout. On revient sur
        // l'une comme sur l'autre depuis l'onglet « Liste blanche ».
        foreach (FilePreview preview in outcome.Result.Previews)
        {
            foreach (ResidualFinding finding in preview.Residuals.Where(finding => !finding.IsExempted))
            {
                rows.Add(SuggestionRow.ForResidual(finding, preview.Path));
            }
        }

        ReplaceSuggestions(rows);
    }

    /// <summary>
    /// Remplace la liste des suggestions, en alignant chacune sur les
    /// décisions déjà enregistrées dans le projet.
    /// </summary>
    /// <param name="rows">Suggestions à afficher</param>
    private void ReplaceSuggestions(List<SuggestionRow> rows)
    {
        foreach (SuggestionRow existing in _suggestions)
        {
            existing.StateChanged -= OnSuggestionChanged;
        }

        foreach (SuggestionRow row in rows)
        {
            row.SetStateSilently(LiveSuggestionState(row.Term));
            row.StateChanged += OnSuggestionChanged;
        }

        _suggestions = new ObservableCollection<SuggestionRow>(rows);
        SuggestionsGrid.ItemsSource = _suggestions;
    }

    /// <summary>
    /// Détermine la décision enregistrée pour une proposition.
    /// </summary>
    /// <param name="term">Terme proposé</param>
    /// <returns>La décision que le projet porte</returns>
    private SuggestionState LiveSuggestionState(string term)
    {
        if (_project is null)
        {
            return SuggestionState.Proposed;
        }

        if (_project.Policy.IsIgnored(term))
        {
            return SuggestionState.Ignored;
        }

        return _project.Vocabulary.Contains(term) ? SuggestionState.Accepted : SuggestionState.Proposed;
    }

    /// <summary>
    /// Applique une suggestion validée, gardée ou remise en proposition.
    /// </summary>
    /// <param name="sender">Suggestion modifiée</param>
    /// <param name="e">Données de l'événement</param>
    private void OnSuggestionChanged(object? sender, EventArgs e)
    {
        if (sender is not SuggestionRow row)
        {
            return;
        }

        string term = row.Term;
        SpanCategory category = row.SuggestedCategory;
        bool done;

        switch (row.State)
        {
            case SuggestionState.Accepted:
                done = Decide(
                    presenter =>
                    {
                        presenter.RestoreSuggestion(term);
                        presenter.AddMaskedTerm(term, category);
                    },
                    $"« {Shorten(term)} » sera masqué partout.");
                if (done)
                {
                    ApplyProposalState(term, PreviewMarkerState.PendingMask, SuggestionState.Accepted);
                }

                break;

            case SuggestionState.Ignored:
                done = Decide(
                    presenter => presenter.IgnoreSuggestion(term),
                    $"« {Shorten(term)} » ajouté à la liste blanche : en clair partout, il ne sera plus signalé.");
                if (done)
                {
                    ApplyLiveStates(term);
                }

                break;

            default:
                done = Decide(
                    presenter =>
                    {
                        presenter.RemoveMaskedTerm(term);
                        presenter.RestoreSuggestion(term);
                    },
                    $"« {Shorten(term)} » redevient une proposition.");
                if (done)
                {
                    ApplyLiveStates(term);
                }

                break;
        }

        if (!done)
        {
            row.SetStateSilently(LiveSuggestionState(term));
        }

        DeferRebuildMaskTargets();
    }

    /// <summary>
    /// Masque la valeur d'une proposition, depuis l'aperçu.
    /// </summary>
    /// <param name="sender">Bouton « Masquer »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnMaskProposal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreviewEntryRow entry)
        {
            return;
        }

        string value = entry.Value;
        SpanCategory category = entry.Category;

        if (Decide(
                presenter =>
                {
                    presenter.RestoreSuggestion(value);
                    presenter.AddMaskedTerm(value, category);
                },
                $"« {Shorten(value)} » sera masqué partout."))
        {
            ApplyProposalState(value, PreviewMarkerState.PendingMask, SuggestionState.Accepted);
            DeferRebuildMaskTargets();
        }
    }

    /// <summary>
    /// Garde en clair cette occurrence-ci d'une valeur, depuis l'aperçu : les
    /// autres occurrences de la valeur, dans ce fichier comme ailleurs, gardent
    /// leur sort.
    /// </summary>
    /// <param name="sender">Bouton « Garder ici »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnKeepHere(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreviewEntryRow entry
            || !_markerOwners.TryGetValue(entry.Marker, out FilePreviewModel? file))
        {
            return;
        }

        PreviewMarker marker = entry.Marker;
        int occurrence = file.OccurrenceOf(marker);
        string others = marker.IsProposal ? "elles restent signalées" : "elles restent masquées";

        if (Decide(
                presenter => presenter.ExemptOccurrence(entry.File, marker.FirstLine, marker.Value, occurrence),
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"Ligne {marker.FirstLine} de {entry.FileLabel} : cette occurrence de « {Shorten(marker.Value)} » reste en clair ; les autres ne changent pas, {others}.")))
        {
            SetMarkerState(marker, PreviewMarkerState.Exempted);
            RefreshPreviewCounts();
        }
    }

    /// <summary>
    /// Garde une valeur en clair partout, depuis l'aperçu : elle rejoint la
    /// liste blanche du projet.
    /// </summary>
    /// <param name="sender">Bouton « Partout »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnKeepEverywhere(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PreviewEntryRow entry)
        {
            KeepEverywhere(entry.Value);
        }
    }

    /// <summary>
    /// Revient sur la décision rappelée par une pastille de l'aperçu : une
    /// occurrence gardée ici, une valeur de la liste blanche, une proposition
    /// retenue pour être masquée.
    /// </summary>
    /// <param name="sender">Lien « annuler »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnUndoDecision(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreviewEntryRow entry
            || !_markerOwners.TryGetValue(entry.Marker, out FilePreviewModel? file))
        {
            return;
        }

        PreviewMarker marker = entry.Marker;
        string value = marker.Value;

        switch (marker.State)
        {
            case PreviewMarkerState.Exempted:
            {
                int occurrence = file.OccurrenceOf(marker);
                if (Decide(
                        presenter => presenter.RestoreOccurrence(entry.File, marker.FirstLine, value, occurrence),
                        string.Create(CultureInfo.CurrentCulture, $"Ligne {marker.FirstLine} de {entry.FileLabel} : l'occurrence n'est plus gardée en clair.")))
                {
                    SetMarkerState(marker, LiveMarkerState(marker, file));
                    RefreshPreviewCounts();
                }

                break;
            }

            case PreviewMarkerState.Whitelisted:
                ReleaseWhitelistValue(value);
                break;

            default:
                if (Decide(
                        presenter =>
                        {
                            presenter.RemoveMaskedTerm(value);
                            presenter.RestoreSuggestion(value);
                        },
                        $"« {Shorten(value)} » redevient une proposition."))
                {
                    ApplyLiveStates(value);
                    DeferRebuildMaskTargets();
                }

                break;
        }
    }

    /// <summary>
    /// Ajoute une valeur à la liste blanche du projet, et le reflète partout
    /// où elle apparaît.
    /// </summary>
    /// <param name="value">Valeur à garder en clair partout</param>
    private void KeepEverywhere(string value)
    {
        if (Decide(
                presenter => presenter.KeepValue(value),
                $"« {Shorten(value)} » rejoint la liste blanche : il restera en clair dans tous les fichiers du projet."))
        {
            ApplyLiveStates(value);
            DeferRebuildMaskTargets();
        }
    }

    /// <summary>
    /// Retire une valeur de la liste blanche du projet, et le reflète partout
    /// où elle apparaît.
    /// </summary>
    /// <param name="value">Valeur à reconsidérer</param>
    private void ReleaseWhitelistValue(string value)
    {
        if (Decide(
                presenter => presenter.ReleaseValue(value),
                $"« {Shorten(value)} » quitte la liste blanche : les règles décident de nouveau de son sort."))
        {
            ApplyLiveStates(value);
            DeferRebuildMaskTargets();
        }
    }

    /// <summary>
    /// Réaligne sur le projet l'état de toutes les occurrences d'une valeur,
    /// dans tous les fichiers, et ses lignes de suggestion.
    /// </summary>
    /// <param name="value">Valeur concernée</param>
    private void ApplyLiveStates(string value)
    {
        foreach (SuggestionRow row in _suggestions.Where(row => string.Equals(row.Term, value, StringComparison.OrdinalIgnoreCase)))
        {
            row.SetStateSilently(LiveSuggestionState(row.Term));
        }

        if (_project is null || _analysis is null)
        {
            return;
        }

        foreach (FilePreviewModel file in _analysis.Files)
        {
            foreach (PreviewMarker marker in file.Markers.Where(marker => string.Equals(marker.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                PreviewMarkerState state = LiveMarkerState(marker, file);
                if (state != marker.State)
                {
                    marker.State = state;
                    file.Refresh(marker);
                }
            }
        }

        RefreshPreviewCounts();
    }

    /// <summary>
    /// Reflète la décision prise sur une proposition partout où elle apparaît :
    /// toutes ses occurrences dans l'aperçu, et ses lignes de suggestion.
    /// </summary>
    /// <param name="value">Valeur proposée</param>
    /// <param name="markerState">État à donner à ses occurrences</param>
    /// <param name="suggestionState">Décision à afficher dans les suggestions</param>
    private void ApplyProposalState(string value, PreviewMarkerState markerState, SuggestionState suggestionState)
    {
        foreach (SuggestionRow row in _suggestions.Where(row => string.Equals(row.Term, value, StringComparison.OrdinalIgnoreCase)))
        {
            row.SetStateSilently(suggestionState);
        }

        if (_analysis is null)
        {
            return;
        }

        foreach (FilePreviewModel file in _analysis.Files)
        {
            foreach (PreviewMarker marker in file.Markers.Where(marker => marker.IsProposal
                && marker.State != markerState
                && string.Equals(marker.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                marker.State = markerState;
                file.Refresh(marker);
            }
        }

        // Le nombre de signalements restants, dans l'en-tête des onglets, suit
        // la décision sur-le-champ.
        RefreshPreviewCounts();
    }

    // ═══════════════════════════════ Occurrences ════════════════════════════

    /// <summary>
    /// Change l'état d'un repère et recompose les lignes qu'il couvre.
    /// </summary>
    /// <param name="marker">Repère à modifier</param>
    /// <param name="state">Nouvel état</param>
    private void SetMarkerState(PreviewMarker marker, PreviewMarkerState state)
    {
        marker.State = state;
        if (_markerOwners.TryGetValue(marker, out FilePreviewModel? file))
        {
            file.Refresh(marker);
        }
    }

    /// <summary>
    /// Reporte sur l'aperçu affiché les décisions prises pendant que l'analyse
    /// tournait : l'instantané analysé ne les connaissait pas.
    /// </summary>
    private void Reconcile()
    {
        if (_project is null || _analysis is null)
        {
            return;
        }

        foreach (FilePreviewModel file in _analysis.Files)
        {
            foreach (PreviewMarker marker in file.Markers)
            {
                PreviewMarkerState state = LiveMarkerState(marker, file);
                if (state != marker.State)
                {
                    marker.State = state;
                    file.Refresh(marker);
                }
            }
        }
    }

    /// <summary>
    /// Détermine l'état qu'une portion doit avoir d'après le projet actuel :
    /// la liste blanche d'abord, qui vaut partout, puis l'occurrence gardée à
    /// cet endroit, enfin la proposition retenue ou non.
    /// </summary>
    /// <param name="marker">Portion concernée</param>
    /// <param name="file">Aperçu de son fichier</param>
    /// <returns>L'état correspondant aux décisions enregistrées</returns>
    private PreviewMarkerState LiveMarkerState(PreviewMarker marker, FilePreviewModel file)
    {
        ObfuscationProject project = _project!;
        ObfuscationPolicy policy = project.Policy;

        if (policy.IsKeptEverywhere(marker.Value))
        {
            return PreviewMarkerState.Whitelisted;
        }

        // Les exceptions sont retenues par le chemin du fichier dans la source :
        // son seul nom ne les retrouverait pas, et la décision prise semblerait
        // annulée à l'analyse suivante.
        if (policy.IsExcepted(file.FileKey, marker.FirstLine, marker.Value, file.OccurrenceOf(marker)))
        {
            return PreviewMarkerState.Exempted;
        }

        if (marker.IsProposal)
        {
            return project.Vocabulary.Contains(marker.Value) ? PreviewMarkerState.PendingMask : PreviewMarkerState.Suspect;
        }

        return PreviewMarkerState.Masked;
    }

    /// <summary>
    /// Protège un libellé de menu : un souligné y marquerait sinon un raccourci
    /// clavier et disparaîtrait de l'affichage — gênant pour un terme comme
    /// <c>srv_backup</c>.
    /// </summary>
    /// <param name="text">Libellé à afficher</param>
    /// <returns>Le libellé, soulignés doublés</returns>
    private static string MenuText(string text)
    {
        return text.Replace("_", "__", StringComparison.Ordinal);
    }

    /// <summary>
    /// Raccourcit une valeur pour le bandeau d'état, un secret pouvant être
    /// très long.
    /// </summary>
    /// <param name="value">Valeur à citer</param>
    /// <returns>La valeur, tronquée au besoin</returns>
    private static string Shorten(string value)
    {
        const int Limit = 48;
        string singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= Limit ? singleLine : string.Concat(singleLine.AsSpan(0, Limit), "…");
    }

    /// <summary>
    /// Remonte l'arbre jusqu'au premier ancêtre d'un type donné.
    /// </summary>
    /// <typeparam name="T">Type recherché</typeparam>
    /// <param name="node">Point de départ</param>
    /// <returns>L'ancêtre trouvé, ou <c>null</c></returns>
    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
