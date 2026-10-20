using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;
using VisualBasicIO = Microsoft.VisualBasic.FileIO;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Fichier de la source, tel que la liste l'affiche.
/// </summary>
/// <param name="FullPath">Chemin complet</param>
/// <param name="Name">Nom du fichier</param>
/// <param name="Folder">Sous-dossier de la source, vide à la racine</param>
/// <param name="Linked">Vrai si la source est un dossier en clair lié, faux pour « in »</param>
public sealed record InputFileRow(string FullPath, string Name, string Folder, bool Linked)
{
    /// <summary>Sous-dossier affiché en retrait après le nom, s'il y en a un.</summary>
    public string FolderLabel => Folder.Length == 0 ? string.Empty : $"  · {Folder}";

    /// <summary>Vrai si la ligne propose la poubelle : seulement dans « in », que l'application alimente.</summary>
    public bool CanDelete => !Linked;

    /// <summary>
    /// Vrai si la ligne propose de tenir le fichier à l'écart : dans un
    /// dossier en clair lié, l'application ne supprime jamais rien.
    /// </summary>
    public bool CanExclude => Linked;

    /// <summary>
    /// Décrit un fichier de la source.
    /// </summary>
    /// <param name="path">Chemin complet du fichier</param>
    /// <param name="sourceRoot">Dossier source du projet</param>
    /// <param name="linked">Vrai si la source est un dossier en clair lié</param>
    /// <returns>La ligne à afficher</returns>
    public static InputFileRow Of(string path, string sourceRoot, bool linked)
    {
        string relative = Path.GetRelativePath(sourceRoot, path);
        return new InputFileRow(path, Path.GetFileName(path), Path.GetDirectoryName(relative) ?? string.Empty, linked);
    }
}

/// <summary>
/// Les fichiers à traiter : le contenu de la source du projet — son dossier
/// « in », ou le dossier en clair lié dans l'onglet « Échanges ».
///
/// La liste n'est plus une sélection d'écran qui pointerait n'importe où sur
/// le disque, mais le reflet exact d'un dossier. Dans « in », ajouter un
/// fichier l'y copie — l'original reste à sa place, intact — et le supprimer
/// l'envoie à la corbeille. Un dossier en clair lié, lui, n'est jamais modifié
/// par la liste : un fichier y est seulement tenu à l'écart des échanges. Dans
/// les deux cas, ce que l'on dépose dans la source depuis l'explorateur
/// apparaît de lui-même, comme ce qu'on y modifie relance l'analyse. Rouvrir
/// le projet retrouve donc le lot tel qu'on l'a laissé, et l'obfuscation
/// reproduit dans la cible l'arborescence de la source.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Une fenêtre WPF n'est pas libérée par Dispose : la surveillance du dossier « in » est arrêtée et libérée à la fermeture du projet comme à celle de la fenêtre.")]
public partial class MainWindow
{
    /// <summary>
    /// Délai qui regroupe les changements rapprochés du dossier « in » : une
    /// copie par l'explorateur en produit plusieurs par fichier.
    /// </summary>
    private static readonly TimeSpan InputSyncDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Nombre de fichiers au-delà duquel une copie dans « in » est confirmée :
    /// déposer par mégarde un dossier trop large le recopierait en entier.
    /// </summary>
    private const int LargeCopyThreshold = 100;

    /// <summary>Lignes de la liste des fichiers, dans l'ordre de « in ».</summary>
    private readonly ObservableCollection<InputFileRow> _fileRows = [];

    /// <summary>Surveillance du dossier « in » du projet ouvert.</summary>
    private FileSystemWatcher? _inputWatcher;

    /// <summary>Minuterie qui regroupe les changements du dossier « in ».</summary>
    private DispatcherTimer? _inputSyncTimer;

    /// <summary>
    /// Taille et date de chaque fichier au dernier relevé : un changement de
    /// l'une ou de l'autre signale un contenu modifié, à réanalyser.
    /// </summary>
    private Dictionary<string, (long Length, DateTime Written)>? _inputSignature;

    // ═════════════════════════ Actions des boutons ══════════════════════════

    /// <summary>
    /// Copie dans « in » des fichiers choisis par le sélecteur.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (_project is null || _busy)
        {
            return;
        }

        // Les fichiers à traiter sont le plus souvent voisins du projet : on
        // part de son dossier parent plutôt que du dossier de l'exécutable.
        OpenFileDialog dialog = new()
        {
            Title = "Fichiers à copier dans le dossier « in » du projet",
            Multiselect = true,
            Filter = "Tous les fichiers|*.*",
            InitialDirectory = Path.GetDirectoryName(_project.RootPath) ?? _project.RootPath,
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    /// <summary>
    /// Ouvre la source — « in », ou le dossier en clair lié — dans
    /// l'explorateur.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenInputFolder(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            // Un dossier lié n'est jamais créé par l'application : introuvable,
            // il est signalé.
            OpenInExplorer(_project.SourcePath, create: !_project.IsSourceLinked);
        }
    }

    /// <summary>
    /// Ouvre la cible — « out », ou le dossier de l'IA lié — dans
    /// l'explorateur.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            OpenInExplorer(_project.TargetPath, create: !_project.IsTargetLinked);
        }
    }

    /// <summary>
    /// Supprime un fichier du dossier « in », après confirmation. Il part à la
    /// corbeille : une suppression par erreur se rattrape.
    /// </summary>
    /// <param name="sender">Poubelle de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnDeleteInputFile(object sender, RoutedEventArgs e)
    {
        if (_project is null || _busy || (sender as FrameworkElement)?.DataContext is not InputFileRow row)
        {
            return;
        }

        // La poubelle n'existe que pour « in » : dans un dossier en clair lié,
        // l'application ne supprime jamais rien. Et l'on ne supprime jamais
        // sur la foi de l'écran seul.
        ObfuscationProject project = _project;
        if (project.IsSourceLinked || !IsInside(row.FullPath, project.InputPath))
        {
            return;
        }

        if (!ConfirmDialog.Ask(
                this,
                $"Supprimer « {row.Name} » ?",
                $"Le fichier est retiré du dossier « in » du projet et placé dans la corbeille de Windows :\n\n  {row.FullPath}\n\nSes sorties déjà produites dans « out » ne sont pas touchées.",
                "Supprimer"))
        {
            return;
        }

        Run(() =>
        {
            try
            {
                MoveToRecycleBin(row.FullPath);
            }
            catch (OperationCanceledException)
            {
                SetStatus($"Suppression de « {row.Name} » annulée.");
                return;
            }

            RemoveEmptyFolders(Path.GetDirectoryName(row.FullPath)!, project.InputPath);
            SyncInputFiles($"« {row.Name} » supprimé du dossier « in » : il est dans la corbeille.");
        });
    }

    // ═════════════════════════════ Copie dans « in » ═════════════════════════

    /// <summary>
    /// Copie dans « in » les fichiers texte des chemins donnés — un dossier
    /// déposé y garde son nom et son arborescence —, puis met la liste à jour
    /// et ouvre l'aperçu des fichiers ajoutés.
    /// </summary>
    /// <param name="paths">Chemins déposés ou sélectionnés</param>
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_project is null || _busy)
        {
            return;
        }

        // Un dossier en clair lié ne reçoit aucune copie : il est la source
        // telle que l'utilisateur la tient, sous git le plus souvent.
        if (_project.IsSourceLinked)
        {
            AddLinkedPaths(paths);
            return;
        }

        ObfuscationProject project = _project;
        CopyPlan? plan = null;
        if (!Run(() =>
            {
                Directory.CreateDirectory(project.InputPath);
                plan = PlanCopies(paths, project);
            }) || plan is null)
        {
            return;
        }

        // Les questions sont posées hors de l'attente : le curseur de travail
        // ne doit pas couvrir une boîte de dialogue qui attend une réponse.
        int count = plan.Fresh.Count + plan.Conflicts.Count;
        if (count > LargeCopyThreshold && !ConfirmDialog.Ask(
                this,
                string.Create(CultureInfo.CurrentCulture, $"Copier {count} fichiers dans « in » ?"),
                "C'est beaucoup pour un lot à confier à une IA : vérifiez que vous avez déposé le bon dossier. Les fichiers sont copiés, vos originaux restent en place.",
                "Copier"))
        {
            SetStatus("Copie annulée : rien n'a été ajouté au dossier « in ».");
            return;
        }

        bool replace = plan.Conflicts.Count > 0 && ConfirmDialog.Ask(
            this,
            string.Create(CultureInfo.CurrentCulture, $"{plan.Conflicts.Count} fichier(s) de ce nom existent déjà dans « in »"),
            "Leur contenu diffère de ce que vous ajoutez :\n\n" +
            Bullets(plan.Conflicts.Select(conflict => Path.GetRelativePath(project.InputPath, conflict.Target))) +
            "\n\nLes remplacer ? Les versions actuelles de « in » iront à la corbeille.",
            "Remplacer",
            "Garder ceux de « in »");

        CopyPlan accepted = plan;

        // Ce qu'on vient de déposer s'ouvre en aperçu, y compris un fichier qui
        // était déjà dans « in » : c'est lui qu'on veut voir.
        List<string> added = [.. accepted.Fresh.Select(copy => copy.Target)
            .Concat(accepted.Conflicts.Select(conflict => conflict.Target))
            .Concat(accepted.Present)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Run(() =>
        {
            List<(string Source, string Target)> copies = [.. accepted.Fresh];
            if (replace)
            {
                foreach ((string _, string target) in accepted.Conflicts)
                {
                    MoveToRecycleBin(target);
                }

                copies.AddRange(accepted.Conflicts);
            }

            foreach ((string source, string target) in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }

            string message;
            if (copies.Count > 0)
            {
                message = string.Create(CultureInfo.CurrentCulture, $"{copies.Count} fichier(s) copié(s) dans le dossier « in » ; vos originaux restent en place.");
            }
            else if (accepted.Present.Count > 0 || accepted.Conflicts.Count > 0)
            {
                message = "Rien de nouveau à copier : ces fichiers sont déjà dans le dossier « in ».";
            }
            else
            {
                message = "Rien à copier : aucun fichier texte parmi ce qui a été ajouté (les fichiers déjà obfusqués et les légendes sont ignorés).";
            }

            if (added.Count > OpenedOnDrop)
            {
                message += string.Create(
                    CultureInfo.CurrentCulture,
                    $" Aperçu ouvert pour les {OpenedOnDrop} premiers ; double-cliquez sur les autres pour ouvrir le leur.");
            }

            SyncInputFiles(message);
            OpenDroppedPreviews(added);
        });
    }

    /// <summary>
    /// Reçoit des chemins déposés alors que la source est un dossier en clair
    /// lié. Rien n'y est copié : un chemin qui s'y trouve déjà est remis dans
    /// les échanges s'il en était tenu à l'écart, et son aperçu s'ouvre ; un
    /// chemin venu d'ailleurs est refusé, avec la raison.
    /// </summary>
    /// <param name="paths">Chemins déposés ou sélectionnés</param>
    private void AddLinkedPaths(IEnumerable<string> paths)
    {
        ObfuscationProject project = _project!;
        string source = project.SourcePath;
        string label = ExchangeLabel(source, linked: true, "in");

        List<string> outside = [];
        List<string> included = [];
        List<string> stillExcluded = [];
        List<string> shown = [];

        Run(() =>
        {
            foreach (string path in paths)
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!IsInside(full, source))
                {
                    outside.Add(Path.GetFileName(full));
                    continue;
                }

                // Un chemin exclu tel quel est remis ; un chemin couvert par un
                // dossier exclu le reste : on ne défait pas l'exclusion d'un
                // dossier entier sur un simple dépôt.
                string relative = RelativePaths.Of(source, full);
                bool isFolder = Directory.Exists(full);
                string key = isFolder ? relative + "/" : relative;
                if (project.Settings.Exchange.Excluded.Any(item => string.Equals(
                        TextFileCollector.NormalizeExclusion(item), TextFileCollector.NormalizeExclusion(key), StringComparison.OrdinalIgnoreCase)))
                {
                    ExchangePresenter.Include(project, key);
                    included.Add(key);
                }

                List<string> exclusions = [.. project.Settings.Exchange.Excluded.Select(TextFileCollector.NormalizeExclusion)];
                if (TextFileCollector.IsExcluded(exclusions, relative))
                {
                    stillExcluded.Add(relative);
                }
                else if (!isFolder)
                {
                    shown.Add(full);
                }
            }

            List<string> parts = [];
            if (included.Count > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{included.Count} chemin(s) remis dans les échanges."));
            }

            if (stillExcluded.Count > 0)
            {
                parts.Add($"« {stillExcluded[0]} » est dans un dossier tenu à l'écart : remettez ce dossier depuis l'onglet « Échanges ».");
            }

            if (outside.Count > 0)
            {
                parts.Add($"« {outside[0]} »{(outside.Count > 1 ? string.Create(CultureInfo.CurrentCulture, $" et {outside.Count - 1} autre(s)") : string.Empty)} : hors du dossier en clair « {label} ». La source étant un dossier lié, l'application n'y copie rien : placez-y vos fichiers vous-même, ou revenez à « in » dans l'onglet « Échanges ».");
            }

            if (parts.Count == 0)
            {
                parts.Add($"Ces fichiers sont déjà dans le dossier en clair « {label} ».");
            }

            if (included.Count > 0)
            {
                AfterExclusionChanged(string.Join(' ', parts));
            }
            else
            {
                SetStatus(string.Join(' ', parts), success: outside.Count > 0 ? false : null);
            }

            OpenDroppedPreviews(shown);
        });
    }

    /// <summary>
    /// Prépare la copie dans « in » : ce qui est nouveau, ce qui y est déjà à
    /// l'identique, et ce qui porterait le nom d'un fichier différent.
    /// </summary>
    /// <param name="paths">Chemins déposés ou sélectionnés</param>
    /// <param name="project">Projet destinataire</param>
    /// <returns>Le plan de copie</returns>
    private static CopyPlan PlanCopies(IEnumerable<string> paths, ObfuscationProject project)
    {
        TextFileCollector collector = new() { SkipProducedFiles = true };
        List<(string Source, string Target)> fresh = [];
        List<(string Source, string Target)> conflicts = [];
        List<string> present = [];
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            // La racine d'un lecteur garde son séparateur : « D: » seul
            // désignerait le dossier courant de ce lecteur.
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            // Un dossier déposé garde son nom sous « in » ; un fichier y va à la racine.
            string origin = Path.GetDirectoryName(full) ?? full;

            foreach (string file in collector.Collect(full))
            {
                if (IsInside(file, project.InputPath))
                {
                    present.Add(file);
                    continue;
                }

                if (IsInside(file, project.MetadataPath))
                {
                    continue;
                }

                string target = Path.Combine(project.InputPath, Path.GetRelativePath(origin, file));
                if (!targets.Add(target))
                {
                    continue;
                }

                if (!File.Exists(target))
                {
                    fresh.Add((file, target));
                }
                else if (SameContent(file, target))
                {
                    present.Add(target);
                }
                else
                {
                    conflicts.Add((file, target));
                }
            }
        }

        return new CopyPlan(fresh, conflicts, present);
    }

    // ═══════════════════════════ Suivi du dossier « in » ═══════════════════════

    /// <summary>
    /// Surveille la source d'un projet : son dossier « in », créé au besoin,
    /// ou le dossier en clair lié, que l'application ne crée jamais — un
    /// dossier lié introuvable n'est pas surveillé, et la liste le dit.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    private void WatchInputFolder(ObfuscationProject project)
    {
        StopWatchingInputFolder();
        if (!project.IsSourceLinked)
        {
            Directory.CreateDirectory(project.InputPath);
        }
        else if (!Directory.Exists(project.SourcePath))
        {
            return;
        }

        FileSystemWatcher watcher = new(project.SourcePath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        watcher.Created += OnInputFolderChanged;
        watcher.Deleted += OnInputFolderChanged;
        watcher.Changed += OnInputFolderChanged;
        watcher.Renamed += OnInputFolderChanged;
        watcher.Error += OnInputFolderError;
        watcher.EnableRaisingEvents = true;

        _inputWatcher = watcher;
    }

    /// <summary>
    /// Arrête la surveillance du dossier « in ».
    /// </summary>
    private void StopWatchingInputFolder()
    {
        _inputSyncTimer?.Stop();
        _inputSignature = null;

        if (_inputWatcher is not null)
        {
            _inputWatcher.EnableRaisingEvents = false;
            _inputWatcher.Dispose();
            _inputWatcher = null;
        }
    }

    /// <summary>
    /// Reçoit un changement de la source, sur le fil du système de fichiers,
    /// et le repasse à celui de l'écran. Les changements sous <c>.git</c> — un
    /// dossier en clair est souvent un dépôt, et chaque commande git en
    /// produit — et les fichiers temporaires de l'outil sont écartés ici.
    /// </summary>
    /// <param name="sender">Surveillance à l'origine de l'événement</param>
    /// <param name="e">Changement survenu</param>
    private void OnInputFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (IsNoise(e.Name))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            // Une surveillance déjà remplacée — projet changé — ne compte plus.
            if (ReferenceEquals(sender, _inputWatcher))
            {
                RestartInputSync();
            }
        });
    }

    /// <summary>
    /// Reçoit un débordement de la surveillance : des changements ont été
    /// perdus, le dossier est donc relu en entier.
    /// </summary>
    /// <param name="sender">Surveillance à l'origine de l'événement</param>
    /// <param name="e">Erreur survenue</param>
    private void OnInputFolderError(object sender, ErrorEventArgs e)
    {
        OnInputFolderChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.All, string.Empty, null));
    }

    /// <summary>
    /// Relance le délai de regroupement avant de relire « in ».
    /// </summary>
    private void RestartInputSync()
    {
        if (_inputSyncTimer is null)
        {
            DispatcherTimer timer = new(DispatcherPriority.Background, Dispatcher) { Interval = InputSyncDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SyncInputFiles(null);
            };

            _inputSyncTimer = timer;
        }

        _inputSyncTimer.Stop();
        _inputSyncTimer.Start();
    }

    /// <summary>
    /// Relit la source et aligne la liste sur son contenu. Un fichier ajouté,
    /// retiré ou modifié relance l'analyse en arrière-plan, et l'état des
    /// échanges.
    /// </summary>
    /// <param name="message">Compte rendu de l'action qui a changé la source, s'il y en a une</param>
    private void SyncInputFiles(string? message)
    {
        if (_project is null)
        {
            return;
        }

        // Pendant une obfuscation, la liste ne doit pas bouger : on relira
        // le dossier juste après.
        if (_busy)
        {
            RestartInputSync();
            return;
        }

        ObfuscationProject project = _project;
        List<string> files;
        Dictionary<string, (long Length, DateTime Written)> signature;

        try
        {
            files = ScanInputFolder(project);
            signature = files.ToDictionary(file => file, SignatureOf, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Dossier en cours de modification — une copie qui n'est pas
            // terminée : on réessaie un peu plus tard.
            RestartInputSync();
            return;
        }

        bool listChanged = !files.SequenceEqual(_files, StringComparer.OrdinalIgnoreCase);
        bool contentChanged = _inputSignature is null || !SameSignature(signature, _inputSignature);
        _inputSignature = signature;

        if (listChanged)
        {
            _files.Clear();
            _fileRows.Clear();
            foreach (string file in files)
            {
                _files.Add(file);
                _fileRows.Add(InputFileRow.Of(file, project.SourcePath, project.IsSourceLinked));
            }

            UpdateActionState();
        }

        // L'aperçu d'un fichier retiré de la source n'a plus d'objet : son
        // onglet se ferme sans attendre la prochaine analyse. C'est aussi ici,
        // une fois le dossier relu, que les aperçus retenus d'un fichier
        // disparu sont oubliés.
        PrunePreviews();

        if (!contentChanged)
        {
            if (message is not null)
            {
                SetStatus(message);
            }

            return;
        }

        // La source a bougé : ce qu'il reste à obfuscer de nouveau aussi.
        ScheduleExchangeStatus();

        string source = project.IsSourceLinked
            ? $"le dossier en clair « {ExchangeLabel(project.SourcePath, linked: true, "in")} »"
            : "le dossier « in »";

        if (_files.Count == 0)
        {
            _tasks.Cancel(AnalysisKind, "plus aucun fichier dans la source");
            ClearAnalysis();
            RefreshAnalysisState();

            string empty = project.IsSourceLinked && !Directory.Exists(project.SourcePath)
                ? $"Dossier en clair introuvable : « {project.SourcePath} ». Liez-le de nouveau, ou revenez à « in », dans l'onglet « Échanges »."
                : $"{Capitalized(source)} ne contient aucun fichier à traiter.";

            SetStatus(message ?? empty, success: project.IsSourceLinked && !Directory.Exists(project.SourcePath) ? false : null);
            return;
        }

        string reason = message ?? (listChanged
            ? $"{Capitalized(source)} a changé : la liste des fichiers a été mise à jour."
            : $"Un fichier dans {source} a été modifié.");

        SetStatus($"{reason} Analyse en arrière-plan : vous pouvez continuer à travailler.");
        ScheduleAnalysis(FileDropDelay, reason);
    }

    /// <summary>
    /// Met la première lettre d'une phrase en majuscule.
    /// </summary>
    /// <param name="text">Texte</param>
    /// <returns>Le texte, sa première lettre en majuscule</returns>
    private static string Capitalized(string text)
    {
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    // ══════════════════════════════ Utilitaires ═════════════════════════════

    /// <summary>
    /// Recense les fichiers texte de la source, comme l'obfuscation les
    /// verra : sorties de l'outil, fichiers propres au dépôt et chemins tenus
    /// à l'écart exclus, liens jamais suivis.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    /// <returns>Les fichiers, dans un ordre stable ; aucun si la source est introuvable</returns>
    private static List<string> ScanInputFolder(ObfuscationProject project)
    {
        return Directory.Exists(project.SourcePath)
            ? [.. ExchangePresenter.CollectSource(project).Files]
            : [];
    }

    /// <summary>
    /// Donne la taille et la date d'écriture d'un fichier.
    /// </summary>
    /// <param name="file">Fichier examiné</param>
    /// <returns>Sa signature</returns>
    private static (long Length, DateTime Written) SignatureOf(string file)
    {
        FileInfo info = new(file);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Compare deux relevés du dossier « in ».
    /// </summary>
    /// <param name="left">Premier relevé</param>
    /// <param name="right">Second relevé</param>
    /// <returns><c>true</c> s'ils décrivent les mêmes fichiers, inchangés</returns>
    private static bool SameSignature(
        Dictionary<string, (long Length, DateTime Written)> left,
        Dictionary<string, (long Length, DateTime Written)> right)
    {
        return left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out (long Length, DateTime Written) other) && other == entry.Value);
    }

    /// <summary>
    /// Compare le contenu de deux fichiers.
    /// </summary>
    /// <param name="left">Premier fichier</param>
    /// <param name="right">Second fichier</param>
    /// <returns><c>true</c> s'ils sont identiques octet pour octet</returns>
    private static bool SameContent(string left, string right)
    {
        return new FileInfo(left).Length == new FileInfo(right).Length
            && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    /// <summary>
    /// Indique si un chemin se trouve sous un dossier.
    /// </summary>
    /// <param name="path">Chemin examiné</param>
    /// <param name="folder">Dossier de référence</param>
    /// <returns><c>true</c> si le chemin est dans le dossier ou l'un de ses sous-dossiers</returns>
    private static bool IsInside(string path, string folder)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Envoie un fichier à la corbeille de Windows.
    /// </summary>
    /// <param name="file">Fichier à supprimer</param>
    /// <exception cref="OperationCanceledException">Si l'utilisateur interrompt l'opération</exception>
    private static void MoveToRecycleBin(string file)
    {
        VisualBasicIO.FileSystem.DeleteFile(file, VisualBasicIO.UIOption.OnlyErrorDialogs, VisualBasicIO.RecycleOption.SendToRecycleBin);
    }

    /// <summary>
    /// Retire les sous-dossiers de « in » laissés vides par une suppression,
    /// sans jamais toucher à « in » lui-même.
    /// </summary>
    /// <param name="folder">Dossier du fichier supprimé</param>
    /// <param name="inputRoot">Dossier « in » du projet</param>
    private static void RemoveEmptyFolders(string folder, string inputRoot)
    {
        string? current = folder;
        while (current is not null && IsInside(current, inputRoot) && Directory.Exists(current)
               && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    /// Ouvre un dossier dans l'explorateur, en le créant au besoin.
    /// </summary>
    /// <param name="folder">Dossier à ouvrir</param>
    /// <param name="create">Vrai pour créer le dossier s'il manque — « in » ou « out » ; faux pour un dossier lié, qui n'est jamais créé</param>
    private void OpenInExplorer(string folder, bool create = true)
    {
        if (!create && !Directory.Exists(folder))
        {
            SetStatus($"Dossier lié introuvable : « {folder} ». Liez-le de nouveau dans l'onglet « Échanges ».", success: false);
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            using Process? explorer = Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Impossible d'ouvrir « {folder} » : {exception.Message}", success: false);
        }
    }

    /// <summary>
    /// Compose une liste à puces courte pour une boîte de dialogue.
    /// </summary>
    /// <param name="items">Éléments à citer</param>
    /// <returns>Les six premiers, suivis du nombre des autres</returns>
    private static string Bullets(IEnumerable<string> items)
    {
        List<string> all = [.. items];
        string shown = string.Join("\n", all.Take(6).Select(item => "  • " + item));
        return all.Count > 6
            ? shown + string.Create(CultureInfo.CurrentCulture, $"\n  … et {all.Count - 6} autre(s)")
            : shown;
    }

    /// <summary>
    /// Plan d'une copie dans « in ».
    /// </summary>
    /// <param name="Fresh">Fichiers absents de « in », à copier</param>
    /// <param name="Conflicts">Fichiers dont le nom est déjà pris dans « in » par un contenu différent</param>
    /// <param name="Present">Fichiers de « in » qui correspondent déjà, à l'identique, à ce qui a été ajouté</param>
    private sealed record CopyPlan(
        List<(string Source, string Target)> Fresh,
        List<(string Source, string Target)> Conflicts,
        List<string> Present);
}
