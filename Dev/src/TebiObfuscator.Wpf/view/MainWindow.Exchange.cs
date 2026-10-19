using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Échanges entre la source et la cible : les dossiers liés, l'obfuscation de
/// l'une vers l'autre, le rapatriement du travail de l'IA, et l'état des deux
/// côtés, suivi en direct.
///
/// Chaque geste se fait en trois temps : le plan est calculé en arrière-plan,
/// l'écran le montre fichier par fichier, et rien n'est écrit avant la
/// confirmation. La cible est surveillée : dès que l'IA y modifie un fichier,
/// le bouton « 3 · Rapatrier » le compte, et devient l'action principale.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Une fenêtre WPF n'est pas libérée par Dispose : la surveillance de la cible est arrêtée et libérée à la fermeture du projet comme à celle de la fenêtre.")]
public partial class MainWindow
{
    /// <summary>Délai qui regroupe les changements rapprochés de la cible.</summary>
    private static readonly TimeSpan TargetSyncDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>Surveillance de la cible du projet ouvert.</summary>
    private FileSystemWatcher? _targetWatcher;

    /// <summary>Minuterie qui regroupe les changements de la cible.</summary>
    private DispatcherTimer? _targetSyncTimer;

    /// <summary>Dernier état connu des deux côtés de l'échange.</summary>
    private ExchangeStatus? _exchangeStatus;

    /// <summary>Numéro de la dernière demande d'état : une réponse plus ancienne est ignorée.</summary>
    private int _statusRequest;

    // ═══════════════════════════════ Liens ════════════════════════════════

    /// <summary>
    /// Lie un dossier en clair comme source.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnLinkSource(object sender, RoutedEventArgs e)
    {
        string? folder = ChooseExchangeFolder("Dossier en clair : la source des fichiers à obfusquer, où reviendra le travail de l'IA");
        if (folder is not null)
        {
            LinkFolder(ExchangeSide.Source, folder);
        }
    }

    /// <summary>
    /// Revient au dossier « in » comme source.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnUnlinkSource(object sender, RoutedEventArgs e)
    {
        LinkFolder(ExchangeSide.Source, null);
    }

    /// <summary>
    /// Lie le dossier où travaille l'IA comme cible.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnLinkTarget(object sender, RoutedEventArgs e)
    {
        string? folder = ChooseExchangeFolder("Dossier où travaille l'IA : la cible de l'obfuscation, séparée du dossier en clair et du projet");
        if (folder is not null)
        {
            LinkFolder(ExchangeSide.Target, folder);
        }
    }

    /// <summary>
    /// Revient au dossier « out » comme cible.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnUnlinkTarget(object sender, RoutedEventArgs e)
    {
        LinkFolder(ExchangeSide.Target, null);
    }

    /// <summary>
    /// Ouvre la source dans l'explorateur.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            OpenInExplorer(_project.SourcePath, create: !_project.IsSourceLinked);
        }
    }

    /// <summary>
    /// Ouvre la cible dans l'explorateur.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenTarget(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            OpenInExplorer(_project.TargetPath, create: !_project.IsTargetLinked);
        }
    }

    /// <summary>
    /// Fait choisir un dossier à lier.
    /// </summary>
    /// <param name="title">Titre du sélecteur</param>
    /// <returns>Le dossier choisi, ou <c>null</c></returns>
    private string? ChooseExchangeFolder(string title)
    {
        if (_project is null || _busy)
        {
            return null;
        }

        OpenFolderDialog dialog = new()
        {
            Title = title,
            InitialDirectory = Path.GetDirectoryName(_project.RootPath) ?? _project.RootPath,
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Lie un dossier, ou le délie, après le contrôle d'isolement : un dossier
    /// que rien ne sépare de l'autre côté est refusé, et l'écran dit pourquoi.
    /// </summary>
    /// <param name="side">Côté à lier</param>
    /// <param name="folder">Dossier à lier, ou <c>null</c> pour revenir à « in » ou « out »</param>
    private void LinkFolder(ExchangeSide side, string? folder)
    {
        if (_project is null || _busy)
        {
            return;
        }

        ObfuscationProject project = _project;
        Run(() =>
        {
            IsolationReport report = ExchangePresenter.Link(project, side, folder);
            if (!report.IsSafe)
            {
                ShowIsolation(report);
                SetStatus("Lien refusé : " + report.Blocking[0], success: false);
                return;
            }

            if (side == ExchangeSide.Source)
            {
                // La liste des fichiers change de racine : elle est relue, et
                // l'analyse suit.
                _inputSignature = null;
                WatchInputFolder(project);
                SyncInputFiles(folder is null
                    ? "La source redevient le dossier « in » du projet."
                    : $"Source liée : « {project.SourcePath} ». Le dossier n'est jamais modifié par l'application, sauf par un rapatriement que vous confirmez.");
            }
            else
            {
                WatchTarget(project);
                SetStatus(folder is null
                    ? "La cible redevient le dossier « out » du projet."
                    : $"Cible liée : « {project.TargetPath} ». « 2 · Obfusquer » y écrira la version pseudonymisée de la source, avec un AGENTS.md pour l'IA.");
            }

            UpdateFilesCard();
            RefreshExchangeView();
            ScheduleExchangeStatus();
            UpdateActionState();
        });
    }

    /// <summary>
    /// Enregistre l'option d'écriture de CLAUDE.md.
    /// </summary>
    /// <param name="sender">Case à cocher</param>
    /// <param name="e">Données de l'événement</param>
    private void OnWriteClaudeMdClick(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            return;
        }

        ObfuscationProject project = _project;
        bool write = WriteClaudeMdCheck.IsChecked == true;
        Run(() =>
        {
            project.Settings.Exchange.WriteClaudeMd = write;
            project.SaveRules();
            SetStatus(write
                ? "CLAUDE.md sera créé dans le dossier de l'IA à la prochaine obfuscation, s'il manque."
                : "CLAUDE.md ne sera plus créé ; un fichier déjà présent reste en place.");
        });
    }

    /// <summary>
    /// Remet dans les échanges un chemin tenu à l'écart.
    /// </summary>
    /// <param name="sender">Lien de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnIncludeExcluded(object sender, RoutedEventArgs e)
    {
        if (_project is null || (sender as FrameworkElement)?.DataContext is not ExchangePathRow row)
        {
            return;
        }

        ObfuscationProject project = _project;
        Run(() =>
        {
            ExchangePresenter.Include(project, row.Path);
            AfterExclusionChanged($"« {row.Path} » part de nouveau vers la cible.");
        });
    }

    /// <summary>
    /// Fait de nouveau examiner au rapatriement un fichier ignoré.
    /// </summary>
    /// <param name="sender">Lien de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnUnignoreTarget(object sender, RoutedEventArgs e)
    {
        if (_project is null || (sender as FrameworkElement)?.DataContext is not ExchangePathRow row)
        {
            return;
        }

        ObfuscationProject project = _project;
        Run(() =>
        {
            ExchangePresenter.Unignore(project, row.Path);
            RefreshExchangeView();
            ScheduleExchangeStatus();
            SetStatus($"« {row.Path} » sera de nouveau examiné au rapatriement.");
        });
    }

    // ═══════════════════════════ Exclusions ════════════════════════════

    /// <summary>
    /// Tient à l'écart des échanges le fichier d'une ligne de la liste.
    /// </summary>
    /// <param name="sender">Bouton de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnExcludeInputFile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InputFileRow row)
        {
            ExcludeSourcePath(RelativeToSource(row.FullPath), isFolder: false);
        }
    }

    /// <summary>
    /// Propose, sur un fichier de la liste, de l'ouvrir ou de le tenir à
    /// l'écart — lui, ou l'un de ses dossiers.
    /// </summary>
    /// <param name="sender">Liste des fichiers</param>
    /// <param name="e">Clic</param>
    private void OnFilesListRightClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Le clic peut tomber sur un fragment du nom, qui n'est pas un élément
        // visuel : la ligne est retrouvée en remontant jusqu'à elle.
        if (_project is null || _busy
            || FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not InputFileRow row)
        {
            return;
        }

        e.Handled = true;
        ContextMenu menu = new() { PlacementTarget = FilesList };
        menu.Items.Add(MenuEntry("Ouvrir l'aperçu", "Entrée", () => OpenPreview(row.FullPath, select: true)));

        if (_project.IsSourceLinked)
        {
            string relative = RelativeToSource(row.FullPath);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(MenuText($"Tenir « {relative} » à l'écart"), string.Empty, () => ExcludeSourcePath(relative, isFolder: false)));

            // Chaque dossier de son chemin, du plus proche au plus large.
            string[] segments = relative.Split('/');
            for (int depth = segments.Length - 1; depth >= 1; depth--)
            {
                string folder = string.Join('/', segments.Take(depth)) + "/";
                menu.Items.Add(MenuEntry(MenuText($"Tenir le dossier « {folder} » à l'écart"), string.Empty, () => ExcludeSourcePath(folder, isFolder: true)));
            }
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Tient un chemin de la source à l'écart des échanges.
    /// </summary>
    /// <param name="relative">Chemin relatif à la source</param>
    /// <param name="isFolder">Vrai pour un dossier et tout ce qu'il contient</param>
    private void ExcludeSourcePath(string relative, bool isFolder)
    {
        if (_project is null || _busy)
        {
            return;
        }

        ObfuscationProject project = _project;
        Run(() =>
        {
            ExchangePresenter.Exclude(project, isFolder && !relative.EndsWith('/') ? relative + "/" : relative);
            AfterExclusionChanged(isFolder
                ? $"Le dossier « {relative} » est tenu à l'écart : il reste dans le dossier en clair, mais ne part plus vers la cible."
                : $"« {relative} » est tenu à l'écart : il reste dans le dossier en clair, mais ne part plus vers la cible.");
        });
    }

    /// <summary>
    /// Relit la source après un changement des exclusions.
    /// </summary>
    /// <param name="message">Compte rendu</param>
    private void AfterExclusionChanged(string message)
    {
        _inputSignature = null;
        SyncInputFiles(message);
        RefreshExchangeView();
        ScheduleExchangeStatus();
    }

    /// <summary>
    /// Donne le chemin d'un fichier relatif à la source, avec des barres
    /// obliques.
    /// </summary>
    /// <param name="path">Chemin absolu</param>
    /// <returns>Le chemin relatif</returns>
    private string RelativeToSource(string path)
    {
        return _project is null ? Path.GetFileName(path) : RelativePaths.Of(_project.SourcePath, path);
    }

    // ═════════════════════════════ Gestes ═════════════════════════════

    /// <summary>
    /// Obfusque la source vers la cible : le plan est calculé en arrière-plan,
    /// montré fichier par fichier, puis écrit sur confirmation.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnObfuscate(object sender, RoutedEventArgs e)
    {
        if (_project is null || _busy)
        {
            return;
        }

        CommitProjectName();
        ObfuscationProject project = _project;
        ExchangePresenter presenter = new(project);
        bool resume = _tasks.ActiveCount > 0;
        _tasks.CancelAll("interrompue par l'obfuscation, reprise ensuite");

        try
        {
            SetBusy(true, "Préparation de l'obfuscation…", "Chaque fichier de la source est obfusqué et contrôlé en mémoire, puis comparé à la cible. Rien n'est encore écrit.");
            Progress<BatchProgress> progress = BusyReporter();
            PublicationPlan plan = await Task.Run(() => presenter.PreparePublication(progress)).ConfigureAwait(true);
            SetBusy(false, string.Empty, string.Empty);

            // Un dossier lié introuvable n'est jamais créé ni remplacé par
            // « in » ou « out » : l'écran le dit, et rien n'est préparé.
            if (!Directory.Exists(plan.SourceRoot))
            {
                SetStatus($"Dossier source introuvable : « {plan.SourceRoot} ». Liez-le de nouveau, ou revenez à « in », dans l'onglet « Échanges ».", success: false);
                return;
            }

            if (plan.TargetIsLinked && !Directory.Exists(plan.TargetRoot))
            {
                SetStatus($"Dossier de l'IA introuvable : « {plan.TargetRoot} ». L'application ne le crée pas : créez-le — un dépôt git à part —, ou liez-en un autre dans l'onglet « Échanges ».", success: false);
                return;
            }

            if (plan.Isolation.IsSafe && plan.Changes.Count == 0)
            {
                SetStatus("Rien à obfusquer : la source ne contient aucun fichier à traiter, et la cible n'a rien à retirer.");
                return;
            }

            ExchangeDialogResult? decision = ExchangeDialog.Ask(this, PublicationContent(project, plan));
            if (decision is null)
            {
                SetStatus("Obfuscation annulée : rien n'a été écrit.");
                return;
            }

            SetBusy(true, "Écriture de l'obfuscation…", "Le coffre est enregistré, puis chaque fichier retenu est écrit d'un seul geste dans la cible.");
            RecycleBin recycler = new(Dispatcher);
            ExchangeOutcome outcome = await Task.Run(() => presenter.CommitPublication(plan, decision.Selected, recycler)).ConfigureAwait(true);

            ReportViewer.Document = ReportComposer.Publication(project, plan, decision.Selected, outcome, this);
            DetailTabs.SelectedItem = ReportTab;
            SetStatus(PublicationSummary(plan, outcome), success: outcome.Skipped.Count == 0);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, success: false);
        }
        finally
        {
            SetBusy(false, string.Empty, string.Empty);

            // « out » n'existe qu'après la première obfuscation : sa
            // surveillance commence alors.
            if (_targetWatcher is null && ReferenceEquals(_project, project))
            {
                WatchTarget(project);
            }

            ScheduleExchangeStatus();

            if (resume)
            {
                ScheduleAnalysis(TimeSpan.Zero, null);
            }
        }
    }

    /// <summary>
    /// Rapatrie dans la source ce que l'IA a changé dans la cible : le plan —
    /// restaurations, fusions, alertes — est calculé en arrière-plan, montré
    /// fichier par fichier, puis écrit sur confirmation.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private async void OnReturn(object sender, RoutedEventArgs e)
    {
        if (_project is null || _busy)
        {
            return;
        }

        CommitProjectName();
        ObfuscationProject project = _project;
        ExchangePresenter presenter = new(project);

        try
        {
            SetBusy(true, "Préparation du rapatriement…", "Chaque fichier que l'IA a changé est restauré et fusionné avec le fichier en clair actuel, en mémoire. Rien n'est encore écrit.");
            Progress<BatchProgress> progress = BusyReporter();
            ReturnPlan plan = await Task.Run(() => presenter.PrepareReturn(progress)).ConfigureAwait(true);
            SetBusy(false, string.Empty, string.Empty);

            // Rien à montrer : la liste vide n'apprendrait rien de plus que le
            // bandeau. Un défaut d'isolement, lui, est toujours montré.
            string target = ExchangeLabel(plan.TargetRoot, project.IsTargetLinked, "out");
            if (plan.Isolation.IsSafe && !plan.HasExchanged)
            {
                SetStatus($"Rien à rapatrier : aucune obfuscation n'a encore été faite vers « {target} » depuis cette source. « 2 · Obfusquer » d'abord : le rapatriement part de ce que la cible a reçu.");
                return;
            }

            if (plan.Isolation.IsSafe && plan.Changes.Count == 0)
            {
                SetStatus($"Rien à rapatrier : l'IA n'a rien changé dans « {target} » depuis le dernier échange.");
                return;
            }

            ExchangeDialogResult? decision = ExchangeDialog.Ask(this, ReturnContent(presenter, plan));
            if (decision is null)
            {
                SetStatus("Rapatriement annulé : rien n'a été écrit.");
                return;
            }

            // Un fichier à soi, dans la cible, est ignoré désormais : il n'est
            // pas écrit dans la source.
            foreach (string ignored in decision.Ignored)
            {
                ExchangePresenter.Ignore(project, ignored);
            }

            // Le rapatriement est écrit même sans fichier coché : une
            // suppression laissée de côté est une décision, que le registre
            // retient.
            ExchangeOutcome outcome = new(0, 0, 0, 0, 0, []);
            if (plan.CanCommit)
            {
                SetBusy(true, "Écriture du rapatriement…", "Chaque fichier retenu est écrit d'un seul geste dans la source.");
                RecycleBin recycler = new(Dispatcher);
                outcome = await Task.Run(() => presenter.CommitReturn(plan, decision.Selected, recycler)).ConfigureAwait(true);
            }

            // Le rapatriement a pu recaler des occurrences gardées ici.
            RebuildWhitelistView();

            ReportViewer.Document = ReportComposer.Return(project, plan, decision.Selected, decision.Ignored, outcome, this);
            DetailTabs.SelectedItem = ReportTab;
            SetStatus(ReturnSummary(plan, outcome, decision.Ignored.Count), success: outcome.Conflicts == 0 && outcome.Skipped.Count == 0 ? true : null);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, success: false);
        }
        finally
        {
            SetBusy(false, string.Empty, string.Empty);
            RefreshExchangeView();
            ScheduleExchangeStatus();
        }
    }

    /// <summary>
    /// Donne un destinataire d'avancement qui alimente l'attente affichée. À
    /// créer sur le fil de l'écran, pour que l'avancement y soit rapporté.
    /// </summary>
    /// <returns>Le destinataire</returns>
    private Progress<BatchProgress> BusyReporter()
    {
        return new Progress<BatchProgress>(report =>
        {
            BusyProgress.Value = report.Fraction;
            BusyDetail.Text = report.FileName.Length == 0
                ? report.Step
                : string.Create(CultureInfo.CurrentCulture, $"fichier {report.FileIndex + 1}/{report.FileCount} · {report.FileName}");
        });
    }

    /// <summary>
    /// Compose la liste à confirmer d'une obfuscation.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="plan">Plan de l'obfuscation</param>
    /// <returns>Le contenu de la liste</returns>
    private static ExchangeDialogContent PublicationContent(ObfuscationProject project, PublicationPlan plan)
    {
        string label = ExchangeLabel(plan.TargetRoot, plan.TargetIsLinked, "out");
        List<ExchangeChange> shown = [.. plan.Changes.Where(change => change.Action != ExchangeAction.Identical)];

        List<string> blocking = [.. plan.Isolation.Blocking];
        if (!plan.IsClean)
        {
            blocking.Add("Une valeur masquée ailleurs reste en clair dans au moins un fichier : rien ne sera écrit tant qu'elle n'est pas masquée. Les fichiers en cause sont marqués « refusé ».");
        }

        List<string> warnings = [.. plan.Isolation.Warnings];
        int suspects = plan.Changes.Count(change => change.Warnings.Any(warning => warning.Kind == ExchangeWarningKind.Suspect));
        if (suspects > 0)
        {
            warnings.Add(string.Create(CultureInfo.CurrentCulture, $"{suspects} fichier(s) portent des valeurs signalées non tranchées : elles partent en clair. Tranchez-les dans l'aperçu si elles doivent être masquées."));
        }

        string summary = string.Join(" · ", new[]
        {
            Count(plan.Changes, ExchangeAction.Create, "créé(s)"),
            Count(plan.Changes, ExchangeAction.Update, "modifié(s)"),
            Count(plan.Changes, ExchangeAction.Delete, "retiré(s)"),
            Count(plan.Changes, ExchangeAction.Identical, "identique(s)"),
            Count(plan.Changes, ExchangeAction.Blocked, "bloqué(s)"),
            Count(plan.Changes, ExchangeAction.Legacy, "ancien(s) produit(s)"),
            Count(plan.Changes, ExchangeAction.Refused, "refusé(s)"),
            Count(plan.Changes, ExchangeAction.Generated, "généré(s)"),
        }.Where(part => part.Length > 0));

        string footer = plan.TargetIsLinked
            ? "Rien n'est écrit avant de confirmer ; un fichier retiré va à la corbeille. Vérifiez ensuite le dossier de l'IA avec git, et commitez avant de la lancer."
            : "Rien n'est écrit avant de confirmer. Chaque fichier part dans « out » avec sa légende ; un fichier retiré va à la corbeille.";

        // Une cible déjà à jour se confirme quand même : le registre retient
        // alors ce qu'elle a, pour reconnaître ensuite le travail de l'IA —
        // le cas de fichiers recopiés à la main avant cette version.
        int identical = plan.Changes.Count(change => change.Action == ExchangeAction.Identical);
        return new ExchangeDialogContent(
            $"Obfusquer « {project.DisplayName} » vers « {label} »",
            plan.TargetRoot,
            summary.Length == 0 ? "La source ne contient aucun fichier à obfusquer." : summary,
            blocking,
            warnings,
            [.. shown.Select(change => new ExchangeRowModel(change, isReturn: false))],
            shown.Count == 0 ? "Enregistrer l'état" : $"Écrire dans « {label} »",
            plan.CanCommit && plan.Changes.Any(change => change.Action != ExchangeAction.Refused),
            footer)
        {
            EmptyMessage = identical > 0
                ? string.Create(CultureInfo.CurrentCulture, $"La cible a déjà le contenu de ces {identical} fichier(s) : rien à écrire. Confirmer enregistre seulement leur état, pour reconnaître ensuite le travail de l'IA.")
                : "Aucun fichier à écrire.",
        };
    }

    /// <summary>
    /// Compose la liste à confirmer d'un rapatriement.
    /// </summary>
    /// <param name="presenter">Présentateur qui a préparé le plan : il refait les fusions dont on tranche les conflits</param>
    /// <param name="plan">Plan du rapatriement</param>
    /// <returns>Le contenu de la liste</returns>
    private static ExchangeDialogContent ReturnContent(ExchangePresenter presenter, ReturnPlan plan)
    {
        string label = ExchangeLabel(plan.SourceRoot, plan.SourceIsLinked, "in");
        List<string> warnings = [.. plan.Isolation.Warnings];
        string summary = ReturnCounts(plan);

        string footer = plan.SourceIsLinked
            ? "Rien n'est écrit avant de confirmer. Les lignes que l'IA n'a pas touchées restent celles du fichier en clair. Vérifiez ensuite le dossier en clair avec git — conflits et alertes compris —, puis commitez."
            : "Rien n'est écrit avant de confirmer. Les versions remplacées de « in » vont à la corbeille.";

        return new ExchangeDialogContent(
            $"Rapatrier dans « {label} »",
            plan.SourceRoot,
            summary.Length == 0 ? "L'IA n'a rien changé dans la cible depuis le dernier échange." : summary,
            plan.Isolation.Blocking,
            warnings,
            [.. plan.Changes.Select(change => new ExchangeRowModel(change, isReturn: true) { CanResolve = plan.ConflictsOf(change.Id).Count > 0 })],
            $"Écrire dans « {label} »",
            plan.CanCommit && plan.Changes.Any(change => change.Action != ExchangeAction.Refused),
            footer)
        {
            // Un fichier en conflit se tranche depuis la liste : la fusion est
            // refaite avec les choix, puis la ligne et les comptes suivent.
            Resolve = (owner, row) =>
            {
                IReadOnlyList<ConflictChoice>? choices = ConflictDialog.Ask(owner, row.Change.SourcePath, plan.ConflictsOf(row.Id), plan.ChoicesOf(row.Id));
                return choices is null ? null : presenter.ResolveConflicts(plan, row.Id, choices);
            },
            Summarize = () => ReturnCounts(plan),
        };
    }

    /// <summary>
    /// Compose les comptes d'un rapatriement, pour l'en-tête de sa liste.
    /// </summary>
    /// <param name="plan">Plan du rapatriement</param>
    /// <returns>Les comptes non nuls, séparés par des points</returns>
    private static string ReturnCounts(ReturnPlan plan)
    {
        int conflicts = plan.Changes.Sum(change => change.Conflicts);
        int serious = plan.Changes.Sum(change => change.Warnings.Count(warning => warning.IsSerious));
        return string.Join(" · ", new[]
        {
            Count(plan.Changes, ExchangeAction.Modified, "modifié(s) par l'IA"),
            Count(plan.Changes, ExchangeAction.Created, "créé(s) par l'IA"),
            Count(plan.Changes, ExchangeAction.Deleted, "supprimé(s) par l'IA"),
            Count(plan.Changes, ExchangeAction.Refused, "refusé(s)"),
            conflicts > 0 ? string.Create(CultureInfo.CurrentCulture, $"{conflicts} conflit(s) à résoudre") : string.Empty,
            serious > 0 ? string.Create(CultureInfo.CurrentCulture, $"{serious} alerte(s) à traiter avant de commiter") : string.Empty,
        }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// Compte les fichiers d'un état, pour un résumé.
    /// </summary>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <param name="action">État compté</param>
    /// <param name="label">Libellé du compte</param>
    /// <returns>Le compte et son libellé, ou une chaîne vide s'il est nul</returns>
    private static string Count(IReadOnlyList<ExchangeChange> changes, ExchangeAction action, string label)
    {
        int count = changes.Count(change => change.Action == action);
        return count == 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{count} {label}");
    }

    /// <summary>
    /// Donne le nom court d'un côté de l'échange.
    /// </summary>
    /// <param name="folder">Dossier</param>
    /// <param name="linked">Vrai pour un dossier lié</param>
    /// <param name="local">Nom du dossier du projet : « in » ou « out »</param>
    /// <returns>Le nom à afficher</returns>
    private static string ExchangeLabel(string folder, bool linked, string local)
    {
        return linked ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : local;
    }

    /// <summary>
    /// Résume une obfuscation écrite, pour le bandeau d'état.
    /// </summary>
    /// <param name="plan">Plan écrit</param>
    /// <param name="outcome">Compte rendu</param>
    /// <returns>Le message</returns>
    private static string PublicationSummary(PublicationPlan plan, ExchangeOutcome outcome)
    {
        string label = ExchangeLabel(plan.TargetRoot, plan.TargetIsLinked, "out");
        string skipped = outcome.Skipped.Count == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $" {outcome.Skipped.Count} fichier(s) laissé(s) de côté, modifiés pendant la confirmation : voir le rapport.");

        // Le guide de l'IA est écrit d'office, hors des comptes.
        string guide = plan.Changes.Any(change => change.Action == ExchangeAction.Generated)
            ? " Guide de l'IA mis à jour."
            : string.Empty;

        if (outcome.Created + outcome.Modified + outcome.Deleted == 0)
        {
            return $"« {label} » était déjà à jour : aucun fichier de la source n'a été réécrit.{guide}{skipped}";
        }

        string written = string.Create(
            CultureInfo.CurrentCulture,
            $"Obfuscation écrite dans « {label} » : {outcome.Created} créé(s), {outcome.Modified} modifié(s), {outcome.Deleted} retiré(s).{guide}");

        string next = plan.TargetIsLinked
            ? " Vérifiez le dossier de l'IA avec git et commitez, puis lancez l'IA."
            : " Donnez à l'IA les fichiers de « out », avec leur légende.";

        return written + skipped + next;
    }

    /// <summary>
    /// Résume un rapatriement écrit, pour le bandeau d'état.
    /// </summary>
    /// <param name="plan">Plan écrit</param>
    /// <param name="outcome">Compte rendu</param>
    /// <param name="ignored">Fichiers de la cible ignorés désormais</param>
    /// <returns>Le message</returns>
    private static string ReturnSummary(ReturnPlan plan, ExchangeOutcome outcome, int ignored)
    {
        string label = ExchangeLabel(plan.SourceRoot, plan.SourceIsLinked, "in");
        string ignoredText = ignored == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $" {ignored} fichier(s) de la cible ignoré(s) désormais : l'onglet « Échanges » les liste.");

        if (outcome.Created + outcome.Modified + outcome.Deleted == 0)
        {
            string skipped = outcome.Skipped.Count == 0
                ? string.Empty
                : string.Create(CultureInfo.CurrentCulture, $" {outcome.Skipped.Count} fichier(s) laissé(s) de côté, modifiés pendant la confirmation : voir le rapport.");
            return $"Rien n'a été écrit dans « {label} ».{skipped}{ignoredText}";
        }

        string written = string.Create(
            CultureInfo.CurrentCulture,
            $"Rapatriement écrit dans « {label} » : {outcome.Modified} modifié(s), {outcome.Created} créé(s), {outcome.Deleted} retiré(s).");

        string conflicts = outcome.Conflicts == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $" {outcome.Conflicts} conflit(s) à résoudre entre les marqueurs <<<<<<< et >>>>>>>.");

        string next = plan.SourceIsLinked
            ? " Vérifiez le dossier en clair avec git, puis commitez ; obfusquez de nouveau avant de relancer l'IA."
            : " Obfusquez de nouveau avant de relancer l'IA.";

        return written + conflicts + next + ignoredText;
    }

    // ═══════════════════════════ État et vue ═══════════════════════════

    /// <summary>
    /// Raccorde les échanges au projet ouvert : surveillance de la cible, vue,
    /// état.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    private void AttachExchange(ObfuscationProject project)
    {
        _exchangeStatus = null;
        WatchTarget(project);
        UpdateFilesCard();
        RefreshExchangeView();
        ScheduleExchangeStatus();
    }

    /// <summary>
    /// Détache les échanges du projet fermé.
    /// </summary>
    private void DetachExchange()
    {
        StopWatchingTarget();
        _exchangeStatus = null;
        _statusRequest++;
        ReturnButton.Content = "3 · Rapatrier";
        ForgetGitResults();
    }

    /// <summary>
    /// Met à jour l'onglet « Échanges » d'après les réglages du projet.
    /// </summary>
    private void RefreshExchangeView()
    {
        if (_project is null)
        {
            return;
        }

        ObfuscationProject project = _project;
        ExchangeSettings exchange = project.Settings.Exchange;

        // Le nom du dossier vient d'abord : un chemin long est tronqué par la
        // fin, et le nom resterait caché dans une fenêtre étroite.
        SourceFolderText.Text = project.IsSourceLinked
            ? $"{ExchangeLabel(project.SourcePath, linked: true, "in")}  ·  {project.SourcePath}"
            : "« in » du projet";
        SourceFolderText.ToolTip = project.SourcePath;
        SourceFolderHint.Text = (project.IsSourceLinked, Directory.Exists(project.SourcePath)) switch
        {
            (true, true) => "dossier lié : l'application y lit, et seul un rapatriement confirmé y écrit",
            (true, false) => "dossier introuvable : liez-le de nouveau, ou revenez à « in »",
            _ => "le dossier « in » du projet ; liez votre dépôt en clair pour éviter les copies",
        };
        UnlinkSourceButton.Visibility = project.IsSourceLinked ? Visibility.Visible : Visibility.Collapsed;

        TargetFolderText.Text = project.IsTargetLinked
            ? $"{ExchangeLabel(project.TargetPath, linked: true, "out")}  ·  {project.TargetPath}"
            : "« out » du projet";
        TargetFolderText.ToolTip = project.TargetPath;
        TargetFolderHint.Text = (project.IsTargetLinked, Directory.Exists(project.TargetPath)) switch
        {
            (true, true) => "dossier lié : noms d'origine pseudonymisés, un AGENTS.md à la racine",
            (true, false) => "dossier introuvable : liez-le de nouveau, ou revenez à « out »",
            _ => "le dossier « out » du projet : fichiers _obfusc et légendes",
        };
        UnlinkTargetButton.Visibility = project.IsTargetLinked ? Visibility.Visible : Visibility.Collapsed;

        RefreshGitView();

        WriteClaudeMdCheck.IsChecked = exchange.WriteClaudeMd;
        WriteClaudeMdCheck.Visibility = project.IsTargetLinked ? Visibility.Visible : Visibility.Collapsed;
        OutWarningPanel.Visibility = project.IsTargetLinked ? Visibility.Collapsed : Visibility.Visible;

        List<ExchangePathRow> excluded = [.. exchange.Excluded.Select(path => new ExchangePathRow(path))];
        ExcludedSection.Visibility = project.IsSourceLinked || excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludedList.ItemsSource = excluded;
        ExcludedEmpty.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        List<ExchangePathRow> ignored = [.. exchange.Ignored.Select(path => new ExchangePathRow(path))];
        IgnoredList.ItemsSource = ignored;
        IgnoredEmpty.Visibility = ignored.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ObfuscateButton.ToolTip = $"Obfusque la source et écrit la cible, « {ExchangeLabel(project.TargetPath, project.IsTargetLinked, "out")} », après la liste exacte des fichiers à confirmer. (F6)";
        ReturnButton.ToolTip = $"Ramène dans « {ExchangeLabel(project.SourcePath, project.IsSourceLinked, "in")} » ce que l'IA a changé dans la cible : chaque fichier est restauré, fusionné avec le fichier en clair actuel et contrôlé, puis la liste à confirmer s'affiche. (F7)";
    }

    /// <summary>
    /// Demande, en arrière-plan, l'état des deux côtés et le contrôle
    /// d'isolement. Une réponse arrivée après une demande plus récente est
    /// ignorée.
    ///
    /// Rien n'est demandé pendant une écriture : le coffre et les réglages
    /// changent alors sur un autre fil, et l'instantané ne doit pas les lire à
    /// moitié. La fin de chaque geste redemande l'état.
    /// </summary>
    private void ScheduleExchangeStatus()
    {
        if (_project is null || _busy)
        {
            return;
        }

        // L'état est calculé sur un instantané : l'écran reste libre de
        // modifier les réglages pendant ce temps.
        _ = RefreshExchangeStatusAsync(++_statusRequest, _projectGeneration, _project.CreateSnapshot());
    }

    /// <summary>
    /// Calcule l'état des deux côtés et le contrôle d'isolement hors du fil de
    /// l'écran, puis les applique.
    /// </summary>
    /// <param name="request">Numéro de la demande</param>
    /// <param name="generation">Session de projet de la demande</param>
    /// <param name="snapshot">Instantané du projet</param>
    /// <returns>La tâche du calcul</returns>
    private async Task RefreshExchangeStatusAsync(int request, int generation, ObfuscationProject snapshot)
    {
        ExchangeStatus? status = null;
        IsolationReport? isolation = null;
        string? error = null;

        try
        {
            (status, isolation) = await Task.Run(() =>
                (ExchangePresenter.Status(snapshot, CancellationToken.None), ExchangePresenter.CheckIsolation(snapshot))).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            error = exception.Message;
        }

        ApplyExchangeStatus(request, generation, status, isolation, error);
    }

    /// <summary>
    /// Applique l'état des deux côtés à l'écran.
    /// </summary>
    /// <param name="request">Numéro de la demande</param>
    /// <param name="generation">Session de projet de la demande</param>
    /// <param name="status">État, s'il a pu être calculé</param>
    /// <param name="isolation">Contrôle d'isolement, s'il a pu être mené</param>
    /// <param name="error">Raison de l'échec, le cas échéant</param>
    private void ApplyExchangeStatus(int request, int generation, ExchangeStatus? status, IsolationReport? isolation, string? error)
    {
        if (_project is null || request != _statusRequest || generation != _projectGeneration)
        {
            return;
        }

        _exchangeStatus = status;
        if (isolation is not null)
        {
            ShowIsolation(isolation);
        }

        ReturnButton.Content = status is { TargetPending: > 0 }
            ? string.Create(CultureInfo.CurrentCulture, $"3 · Rapatrier ({status.TargetPending})")
            : "3 · Rapatrier";

        ExchangeStateText.Text = error is not null
            ? "État des échanges illisible : " + error
            : DescribeStatus(_project, status);

        ExchangeHistoryList.ItemsSource = status is null
            ? []
            : status.History.Reverse().Select(item => new ExchangeEventRow(item)).ToList();
        ExchangeHistoryEmpty.Visibility = status is { History.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

        RefreshGitView();
        UpdateActionState();
        RefreshAnalysisState();
    }

    /// <summary>
    /// Montre le verdict de l'isolement dans l'onglet « Échanges ».
    /// </summary>
    /// <param name="report">Verdict</param>
    private void ShowIsolation(IsolationReport report)
    {
        if (!report.IsSafe)
        {
            IsolationPanel.Background = Themed<System.Windows.Media.Brush>("DangerDim");
            IsolationText.Foreground = Themed<System.Windows.Media.Brush>("Danger");
            IsolationText.Text = "Isolement compromis : " + string.Join(" ", report.Blocking);
            return;
        }

        if (report.Warnings.Count > 0)
        {
            IsolationPanel.Background = Themed<System.Windows.Media.Brush>("WarningDim");
            IsolationText.Foreground = Themed<System.Windows.Media.Brush>("SuspectForeground");
            IsolationText.Text = "Isolement vérifié, à surveiller : " + string.Join(" ", report.Warnings);
            return;
        }

        // Sans dossier lié, il n'y a rien à isoler : le message reste neutre, à
        // côté de l'avertissement sur « out ».
        bool linked = _project is { IsTargetLinked: true } or { IsSourceLinked: true };
        IsolationPanel.Background = Themed<System.Windows.Media.Brush>(linked ? "SuccessDim" : "Raised");
        IsolationText.Foreground = Themed<System.Windows.Media.Brush>(linked ? "Success" : "TextSecondary");
        IsolationText.Text = _project switch
        {
            { IsTargetLinked: true } => "Isolement vérifié : aucun lien, aucun renvoi git ne relie le dossier de l'IA au dossier en clair ni au projet.",
            { IsSourceLinked: true } => "Isolement vérifié : le dossier en clair et le projet sont distincts.",
            _ => "Aucun dossier lié : la source et la cible sont « in » et « out », dans le projet.",
        };
    }

    /// <summary>
    /// Décrit l'état des deux côtés en clair.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="status">État, ou <c>null</c></param>
    /// <returns>Le texte</returns>
    private static string DescribeStatus(ObfuscationProject project, ExchangeStatus? status)
    {
        if (status is null || !status.HasExchanged)
        {
            return "Aucune obfuscation n'a encore été faite vers cette cible depuis cette source.";
        }

        List<string> lines = [];
        if (status.LastObfuscation is { } obfuscation)
        {
            lines.Add($"Dernière obfuscation : {obfuscation.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)}.");
        }

        if (status.LastReturn is { } returned)
        {
            lines.Add($"Dernier rapatriement : {returned.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)}.");
        }

        string target = ExchangeLabel(project.TargetPath, project.IsTargetLinked, "out");
        lines.Add(status.TargetPending == 0
            ? $"Côté « {target} » : rien de changé depuis le dernier échange."
            : string.Create(CultureInfo.CurrentCulture, $"Côté « {target} » : {status.TargetModified} modifié(s), {status.TargetCreated} créé(s), {status.TargetDeleted} supprimé(s) — à rapatrier."));

        string source = ExchangeLabel(project.SourcePath, project.IsSourceLinked, "in");
        lines.Add(status.SourcePending == 0
            ? $"Côté « {source} » : rien de changé depuis le dernier échange."
            : string.Create(CultureInfo.CurrentCulture, $"Côté « {source} » : {status.SourceChanged} modifié(s), {status.SourceAdded} nouveau(x) — à obfusquer de nouveau."));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Adapte la carte « Fichiers à traiter » à la source : « in », que l'on
    /// alimente par copie, ou un dossier en clair lié, que l'on ne modifie pas.
    /// </summary>
    private void UpdateFilesCard()
    {
        if (_project is null)
        {
            return;
        }

        bool linked = _project.IsSourceLinked;
        string source = ExchangeLabel(_project.SourcePath, linked, "in");

        FilesTitle.Text = linked ? $"FICHIERS À TRAITER · « {source.ToUpperInvariant()} »" : "FICHIERS À TRAITER";
        FilesTitle.ToolTip = _project.SourcePath;
        DropHint.Text = linked
            ? $"Les fichiers texte du dossier en clair « {source} » ; double-cliquez sur un fichier pour ouvrir son aperçu. Le bouton d'une ligne, ou un clic droit, tient un fichier ou un dossier à l'écart : il reste dans le dossier, mais ne part plus vers la cible."
            : "Le contenu du dossier « in » du projet ; double-cliquez sur un fichier pour ouvrir son aperçu. Glissez-déposez des fichiers ou des dossiers ici, ou n'importe où sur la fenêtre : ils y sont copiés, vos originaux restent en place.";
        if (_dropWithoutHighlight && !linked)
        {
            DropHint.Text += " " + ElevatedDropNote;
        }

        AddFilesButton.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        OpenInputButton.Content = linked ? "Ouvrir la source" : "Ouvrir « in »";
        OpenInputButton.ToolTip = linked
            ? $"Ouvre le dossier en clair « {_project.SourcePath} » dans l'explorateur."
            : "Ouvre le dossier des fichiers à traiter dans l'explorateur : ce qu'on y dépose apparaît aussitôt ici.";
        OpenOutputButton.Content = _project.IsTargetLinked ? "Ouvrir la cible" : "Ouvrir « out »";
        OpenOutputButton.ToolTip = _project.IsTargetLinked
            ? $"Ouvre le dossier de l'IA « {_project.TargetPath} » dans l'explorateur."
            : "Ouvre le dossier des fichiers obfusqués (nom_obfusc) et de leurs légendes : ce sont eux qu'on joint à la conversation avec l'IA.";
    }

    // ═══════════════════════ Surveillance de la cible ═══════════════════════

    /// <summary>
    /// Surveille la cible du projet, si elle existe : un fichier que l'IA y
    /// modifie met à jour le compte du bouton « 3 · Rapatrier ».
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    private void WatchTarget(ObfuscationProject project)
    {
        StopWatchingTarget();
        if (!Directory.Exists(project.TargetPath))
        {
            return;
        }

        FileSystemWatcher watcher = new(project.TargetPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        watcher.Created += OnTargetFolderChanged;
        watcher.Deleted += OnTargetFolderChanged;
        watcher.Changed += OnTargetFolderChanged;
        watcher.Renamed += OnTargetFolderChanged;
        watcher.Error += (sender, _) => OnTargetFolderChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.All, string.Empty, null));
        watcher.EnableRaisingEvents = true;

        _targetWatcher = watcher;
    }

    /// <summary>
    /// Arrête la surveillance de la cible.
    /// </summary>
    private void StopWatchingTarget()
    {
        _targetSyncTimer?.Stop();
        if (_targetWatcher is not null)
        {
            _targetWatcher.EnableRaisingEvents = false;
            _targetWatcher.Dispose();
            _targetWatcher = null;
        }
    }

    /// <summary>
    /// Reçoit un changement de la cible, sur le fil du système de fichiers. Les
    /// changements sous <c>.git</c> — chaque commande git en produit — et les
    /// fichiers temporaires de l'outil sont écartés avant même de rejoindre le
    /// fil de l'écran.
    /// </summary>
    /// <param name="sender">Surveillance à l'origine de l'événement</param>
    /// <param name="e">Changement survenu</param>
    private void OnTargetFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (IsNoise(e.Name))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(sender, _targetWatcher))
            {
                return;
            }

            if (_targetSyncTimer is null)
            {
                DispatcherTimer timer = new(DispatcherPriority.Background, Dispatcher) { Interval = TargetSyncDelay };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    ScheduleExchangeStatus();
                };

                _targetSyncTimer = timer;
            }

            _targetSyncTimer.Stop();
            _targetSyncTimer.Start();
        });
    }

    /// <summary>
    /// Indique si un changement de dossier est du bruit pour l'échange : il
    /// concerne le dossier <c>.git</c>, ou un fichier temporaire de l'outil.
    /// </summary>
    /// <param name="relative">Chemin relatif du changement, tel que la surveillance le donne</param>
    /// <returns><c>true</c> si le changement peut être ignoré</returns>
    private static bool IsNoise(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return false;
        }

        string normalized = "/" + relative.Replace('\\', '/');
        return normalized.EndsWith("/.git", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
            || AtomicFile.IsTemporary(Path.GetFileName(normalized));
    }
}
