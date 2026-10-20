using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Glisser-déposer : sur toute la fenêtre, avec un retour visuel explicite.
///
/// Le dépôt est accepté partout et en toutes circonstances. Refuser un dépôt
/// faute de projet ouvert affichait un sens interdit sans explication — ce qui
/// ressemblait à une panne. Désormais un dossier de projet s'ouvre, et des
/// fichiers déposés avant tout projet sont gardés en attente puis copiés dans
/// son dossier « in » dès qu'un projet est ouvert.
///
/// Les événements sont interceptés au niveau de la fenêtre, en phase
/// descendante : aucun contrôle — une zone de saisie, une grille — ne peut
/// refuser un dépôt de fichiers à sa place. Un glisser de texte, lui, continue
/// d'atteindre les zones de saisie.
/// </summary>
public partial class MainWindow
{
    /// <summary>Rappel du mode de dépôt en administrateur.</summary>
    private const string ElevatedDropNote = "L'application tourne en administrateur : le dépôt fonctionne, mais sans surbrillance pendant le glisser.";

    /// <summary>
    /// Minuterie qui referme la couche de dépôt quand le glisser s'interrompt
    /// au-dessus de la fenêtre.
    /// </summary>
    private DispatcherTimer? _dropWatchdog;

    /// <summary>Vrai tant que la couche de dépôt décrit le glisser en cours.</summary>
    private bool _dropFeedbackShown;

    /// <summary>
    /// Vrai si le dépôt passe par le mode de secours en administrateur : il
    /// fonctionne, mais sans surbrillance pendant le glisser, ce que l'invite
    /// de la liste des fichiers rappelle.
    /// </summary>
    private bool _dropWithoutHighlight;

    /// <summary>
    /// Prépare le glisser-déposer, y compris son mode de secours en
    /// administrateur.
    /// </summary>
    private void InitializeDragDrop()
    {
        // Le système annonce la sortie du glisser à chaque passage d'un
        // contrôle à l'autre, suivie aussitôt d'une nouvelle entrée : on
        // n'efface la couche que si aucune entrée ne suit.
        _dropWatchdog = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };

        _dropWatchdog.Tick += (_, _) => HideDropOverlay();

        _dropWithoutHighlight = ElevatedDropSupport.EnableIfElevated(this, HandleDroppedPaths);
        if (_dropWithoutHighlight)
        {
            DropHint.Text += " " + ElevatedDropNote;
        }
    }

    /// <summary>
    /// Accepte un glisser de fichiers et décrit ce que fera le dépôt.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Données du glisser</param>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        _dropWatchdog?.Stop();
        e.Handled = true;

        if (_busy)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;

        if (!_dropFeedbackShown)
        {
            ShowDropOverlay(e.Data.GetData(DataFormats.FileDrop) as string[] ?? []);
        }
    }

    /// <summary>
    /// Prépare la fermeture de la couche de dépôt, annulée si le glisser
    /// continue au-dessus d'un autre contrôle.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Données du glisser</param>
    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _dropWatchdog?.Stop();
        _dropWatchdog?.Start();
    }

    /// <summary>
    /// Prend en compte les fichiers déposés.
    /// </summary>
    /// <param name="sender">Fenêtre</param>
    /// <param name="e">Données du dépôt</param>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        HideDropOverlay();

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            HandleDroppedPaths(paths);
        }
    }

    /// <summary>
    /// Décide du sort d'un dépôt : ouvrir un projet, ajouter des fichiers, ou
    /// les garder en attente d'un projet.
    /// </summary>
    /// <param name="paths">Chemins déposés</param>
    private void HandleDroppedPaths(string[] paths)
    {
        if (_busy || paths.Length == 0)
        {
            return;
        }

        if (paths.Length == 1 && IsProjectFolder(paths[0])
            && (_project is null || !SameFolder(_project.RootPath, paths[0])))
        {
            CommitProjectName();
            OpenProjectAt(paths[0], create: false);
            return;
        }

        if (_project is null)
        {
            foreach (string path in paths.Where(path => !_pendingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                _pendingPaths.Add(path);
            }

            UpdatePendingNotice();
            SetStatus(string.Create(
                CultureInfo.CurrentCulture,
                $"{paths.Length} élément(s) en attente : ouvrez ou créez un projet, ils seront copiés aussitôt dans son dossier « in »."));
            return;
        }

        AddPaths(paths);
    }

    /// <summary>
    /// Affiche la couche de dépôt, avec ce que le dépôt va faire.
    /// </summary>
    /// <param name="paths">Chemins en cours de glisser</param>
    private void ShowDropOverlay(string[] paths)
    {
        _dropFeedbackShown = true;
        DropOverlay.Visibility = Visibility.Visible;

        if (paths.Length == 1 && IsProjectFolder(paths[0])
            && (_project is null || !SameFolder(_project.RootPath, paths[0])))
        {
            DropTitle.Text = $"Relâchez pour ouvrir le projet « {Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))} »";
            DropDetail.Text = _project is null
                ? "Ce dossier contient un projet Obfuscator."
                : "Le projet actuel sera fermé ; ses décisions sont déjà enregistrées.";
            return;
        }

        if (_project is null)
        {
            DropTitle.Text = "Relâchez pour garder ces fichiers en attente";
            DropDetail.Text = "Ouvrez ou créez ensuite un projet : ils seront copiés dans son dossier « in », puis analysés.";
            return;
        }

        FilesDropZone.BorderBrush = Themed<System.Windows.Media.Brush>("Accent");

        // Un dossier en clair lié ne reçoit aucune copie : le dépôt y désigne
        // seulement des fichiers qui s'y trouvent déjà.
        if (_project.IsSourceLinked)
        {
            DropTitle.Text = $"La source est le dossier en clair « {ExchangeLabel(_project.SourcePath, linked: true, "in")} »";
            DropDetail.Text = "Rien n'y est copié : un fichier qui s'y trouve déjà ouvre son aperçu, et revient dans les échanges s'il en était écarté. Un fichier venu d'ailleurs est refusé : placez-le vous-même dans le dossier, ou revenez à « in » dans l'onglet « Échanges ».";
            return;
        }

        DropTitle.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Relâchez pour copier {paths.Length} élément(s) dans le dossier « in » de « {_project.DisplayName} »");
        DropDetail.Text = "Les dossiers sont parcourus ; seuls les fichiers texte sont copiés, vos originaux restent en place. L'analyse démarre d'elle-même, en arrière-plan.";
    }

    /// <summary>
    /// Referme la couche de dépôt.
    /// </summary>
    private void HideDropOverlay()
    {
        _dropWatchdog?.Stop();
        _dropFeedbackShown = false;
        DropOverlay.Visibility = Visibility.Collapsed;
        FilesDropZone.BorderBrush = Themed<System.Windows.Media.Brush>("BorderBrushSoft");
    }
}
