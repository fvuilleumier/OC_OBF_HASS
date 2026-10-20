using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Sélection de texte dans les volets de l'aperçu : à la souris, au clavier,
/// copie, et menu de la sélection — masquer un terme sous une nature choisie,
/// ou le garder en clair partout.
///
/// Les volets ne sont pas des zones de texte : ce sont les lignes d'une liste
/// virtualisée, composées de fragments colorés. La sélection est donc tenue
/// ici, en positions de texte, et chaque ligne concernée se recompose pour la
/// montrer. Elle vaut pour un volet à la fois — on copie l'original ou le
/// pseudonymisé, pas un mélange des deux.
///
/// Copier le volet pseudonymisé demande une précaution que l'original ne
/// demande pas. Ses pseudonymes ont été attribués par l'analyse, dans une copie
/// du coffre : une réponse de l'IA qui les reprendrait ne pourrait pas être
/// restaurée s'ils n'entraient pas au coffre réel. La copie les y fait entrer.
/// Et un aperçu dépassé, ou une valeur retenue dont le pseudonyme n'est pas
/// encore attribué, ferait partir en clair ce que les règles masquent : la
/// copie est alors refusée, avec la raison.
/// </summary>
public partial class MainWindow
{
    /// <summary>Longueur au-delà de laquelle un passage sélectionné n'est plus proposé comme terme.</summary>
    private const int LongestTerm = 200;

    /// <summary>Sélection en cours, dans l'un des aperçus ouverts.</summary>
    private PreviewSelection? _selection;

    /// <summary>Lignes où la sélection est actuellement dessinée, pour les effacer ensuite.</summary>
    private (FilePreviewModel File, PreviewPane Pane, int First, int Last)? _shownSelection;

    /// <summary>Liste dans laquelle la souris étend la sélection, pendant un glisser.</summary>
    private ListBox? _selectionList;

    /// <summary>Dernière position de la souris pendant un glisser, relative à la liste.</summary>
    private Point _dragPoint;

    /// <summary>Défilement automatique quand le glisser sort de la liste par le haut ou le bas.</summary>
    private DispatcherTimer? _autoScroll;

    // ═══════════════════════════════ Souris ═══════════════════════════════

    /// <summary>
    /// Raccorde une liste d'aperçu à la sélection de texte.
    /// </summary>
    /// <param name="list">Liste d'un aperçu</param>
    private void WireSelection(ListBox list)
    {
        list.PreviewMouseLeftButtonDown += OnPreviewTextMouseDown;
        list.PreviewMouseMove += OnPreviewTextMouseMove;
        list.PreviewMouseLeftButtonUp += OnPreviewTextMouseUp;
        list.PreviewMouseRightButtonUp += OnPreviewTextRightClick;
        list.LostMouseCapture += OnPreviewTextLostCapture;
    }

    /// <summary>
    /// Commence une sélection, ou l'étend d'un clic avec Maj ; un double-clic
    /// sélectionne un mot, un triple-clic la ligne.
    /// </summary>
    /// <param name="sender">Liste d'aperçu</param>
    /// <param name="e">Clic</param>
    private void OnPreviewTextMouseDown(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Les boutons — décisions, repli — gardent leur clic.
        if (sender is not ListBox list || list.Tag is not FilePreviewModel file
            || FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null
            || !TryHitPane(list, e.GetPosition(list), null, out PreviewPane pane, out PanePosition position))
        {
            return;
        }

        list.Focus();
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            SelectWordAt(file, pane, position);
            return;
        }

        if (e.ClickCount >= 3)
        {
            SelectLine(file, pane, position.Line);
            return;
        }

        bool extend = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            && _selection is not null
            && ReferenceEquals(_selection.File, file)
            && _selection.Pane == pane;

        PreviewSelection selection = extend ? _selection! : new PreviewSelection(file, pane, position);
        selection.Active = position;
        ShowSelection(selection);

        _selectionList = list;
        _dragPoint = e.GetPosition(list);
        list.CaptureMouse();
    }

    /// <summary>
    /// Étend la sélection pendant que la souris glisse.
    /// </summary>
    /// <param name="sender">Liste d'aperçu</param>
    /// <param name="e">Mouvement</param>
    private void OnPreviewTextMouseMove(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_selectionList is null || !ReferenceEquals(sender, _selectionList))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSelectionDrag();
            return;
        }

        _dragPoint = e.GetPosition(_selectionList);
        ExtendSelectionTo(_selectionList, _dragPoint);
        UpdateAutoScroll(_selectionList, _dragPoint);
    }

    /// <summary>
    /// Termine le glisser.
    /// </summary>
    /// <param name="sender">Liste d'aperçu</param>
    /// <param name="e">Relâchement</param>
    private void OnPreviewTextMouseUp(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_selectionList is not null && ReferenceEquals(sender, _selectionList))
        {
            EndSelectionDrag();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Termine le glisser quand la liste perd la souris — une boîte de dialogue,
    /// un changement de fenêtre.
    /// </summary>
    /// <param name="sender">Liste d'aperçu</param>
    /// <param name="e">Perte de la capture</param>
    private void OnPreviewTextLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _selectionList))
        {
            _selectionList = null;
            _autoScroll?.Stop();
        }
    }

    /// <summary>
    /// Relâche la souris et arrête le défilement automatique.
    /// </summary>
    private void EndSelectionDrag()
    {
        ListBox? list = _selectionList;
        _selectionList = null;
        _autoScroll?.Stop();

        if (list?.IsMouseCaptured == true)
        {
            list.ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// Étend la sélection jusqu'à un point de la liste. Hors de la liste, la
    /// sélection va au début de la première ligne visible ou à la fin de la
    /// dernière ; hors du volet, au bord de la ligne.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="point">Position de la souris, relative à la liste</param>
    private void ExtendSelectionTo(ListBox list, Point point)
    {
        if (_selection is null || list.Tag is not FilePreviewModel file || !ReferenceEquals(file, _selection.File))
        {
            return;
        }

        // Le point est ramené dans la liste pour trouver la ligne ; la colonne,
        // elle, est lue à la vraie position, pour qu'un glisser au-delà du bord
        // droit aille au bout de la ligne.
        Point probe = new(
            Math.Clamp(point.X, 1, Math.Max(1, list.ActualWidth - 2)),
            Math.Clamp(point.Y, 1, Math.Max(1, list.ActualHeight - 2)));

        if (!TryHitPane(list, probe, _selection.Pane, out _, out PanePosition position, point))
        {
            return;
        }

        if (point.Y < 0)
        {
            position = position with { Offset = 0 };
        }
        else if (point.Y > list.ActualHeight)
        {
            position = position with { Offset = LineLength(file, position.Line, _selection.Pane) };
        }

        if (position != _selection.Active)
        {
            _selection.Active = position;
            ShowSelection(_selection);
        }
    }

    /// <summary>
    /// Lance ou arrête le défilement automatique selon que le glisser est sorti
    /// de la liste ou non.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="point">Position de la souris, relative à la liste</param>
    private void UpdateAutoScroll(ListBox list, Point point)
    {
        if (point.Y >= 0 && point.Y <= list.ActualHeight)
        {
            _autoScroll?.Stop();
            return;
        }

        _autoScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, OnAutoScrollTick, Dispatcher);
        _autoScroll.Start();
    }

    /// <summary>
    /// Fait défiler la liste pendant un glisser sorti par le haut ou le bas, et
    /// étend la sélection aux lignes qui apparaissent. Plus la souris s'éloigne
    /// du bord, plus le défilement est rapide.
    /// </summary>
    /// <param name="sender">Minuterie</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        ListBox? list = _selectionList;
        if (list is null || FindDescendant<ScrollViewer>(list) is not ScrollViewer viewer)
        {
            _autoScroll?.Stop();
            return;
        }

        double distance = _dragPoint.Y < 0 ? -_dragPoint.Y : _dragPoint.Y - list.ActualHeight;
        if (distance <= 0)
        {
            _autoScroll?.Stop();
            return;
        }

        double step = Math.Clamp(distance, 8, 160);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (_dragPoint.Y < 0 ? -step : step));
        list.UpdateLayout();
        ExtendSelectionTo(list, _dragPoint);
    }

    // ═══════════════════════════════ Repérage ═══════════════════════════════

    /// <summary>
    /// Retrouve la position de texte sous un point d'une liste d'aperçu.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="point">Point à examiner, relatif à la liste</param>
    /// <param name="pane">Volet imposé — celui d'une sélection en cours — ou <c>null</c> pour celui sous le point</param>
    /// <param name="hitPane">Volet trouvé</param>
    /// <param name="position">Position de texte trouvée</param>
    /// <param name="columnPoint">Point qui donne la colonne, s'il diffère de celui qui donne la ligne</param>
    /// <returns><c>true</c> si le point désigne du texte d'un volet</returns>
    private bool TryHitPane(
        ListBox list,
        Point point,
        PreviewPane? pane,
        out PreviewPane hitPane,
        out PanePosition position,
        Point? columnPoint = null)
    {
        hitPane = pane ?? PreviewPane.Original;
        position = default;

        if (list.Tag is not FilePreviewModel file)
        {
            return false;
        }

        DependencyObject? hit = list.InputHitTest(point) as DependencyObject;
        ListBoxItem? container = FindAncestor<ListBoxItem>(hit);

        switch (container?.DataContext)
        {
            case PreviewLineRow row:
                TextBlock? block;
                if (pane is null)
                {
                    block = FindAncestor<TextBlock>(hit);
                    if (block?.Tag is not string tag || !Enum.TryParse(tag, out PreviewPane found))
                    {
                        return false;
                    }

                    hitPane = found;
                }
                else
                {
                    block = FindPaneBlock(container, pane.Value);
                    if (block is null)
                    {
                        return false;
                    }
                }

                Point local = list.TranslatePoint(columnPoint ?? point, block);
                position = new PanePosition(row.Number, Math.Min(OffsetAt(block, local), row.TextOf(hitPane).Length));
                return true;

            // Un repli traversé pendant un glisser est pris en entier, dans le
            // sens du glisser.
            case PreviewGapRow gap when pane is not null && _selection is not null:
                position = _selection.Anchor.Line < gap.FirstLine
                    ? new PanePosition(gap.LastLine, LineLength(file, gap.LastLine, pane.Value))
                    : new PanePosition(gap.FirstLine, 0);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Donne le rang du caractère sous un point d'un bloc de texte.
    /// </summary>
    /// <param name="block">Bloc de texte d'un volet</param>
    /// <param name="point">Point relatif au bloc</param>
    /// <returns>Le rang, à partir de 0</returns>
    private static int OffsetAt(TextBlock block, Point point)
    {
        if (point.Y < 0)
        {
            return 0;
        }

        if (point.Y >= block.ActualHeight)
        {
            return int.MaxValue;
        }

        Point inside = new(Math.Clamp(point.X, 0, Math.Max(0, block.ActualWidth - 1)), point.Y);
        TextPointer? pointer = block.GetPositionFromPoint(inside, snapToText: true);
        if (pointer is null)
        {
            return 0;
        }

        // Au-delà du dernier caractère d'une ligne affichée, on vise sa fin.
        int offset = 0;
        foreach (Inline inline in block.Inlines)
        {
            if (inline is not Run run)
            {
                continue;
            }

            if (pointer.CompareTo(run.ContentEnd) <= 0)
            {
                return offset + Math.Max(0, run.ContentStart.GetOffsetToPosition(pointer));
            }

            offset += run.Text.Length;
        }

        return offset;
    }

    /// <summary>
    /// Retrouve le bloc de texte d'un volet dans la ligne affichée.
    /// </summary>
    /// <param name="node">Conteneur de la ligne</param>
    /// <param name="pane">Volet voulu</param>
    /// <returns>Le bloc, ou <c>null</c> si la ligne n'est pas encore composée</returns>
    private static TextBlock? FindPaneBlock(DependencyObject node, PreviewPane pane)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, index);
            if (child is TextBlock { Tag: string tag } block && tag == pane.ToString())
            {
                return block;
            }

            if (FindPaneBlock(child, pane) is TextBlock nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Longueur d'une ligne dans un volet.
    /// </summary>
    /// <param name="file">Aperçu du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="pane">Volet</param>
    /// <returns>Le nombre de caractères affichés</returns>
    private static int LineLength(FilePreviewModel file, int line, PreviewPane pane)
    {
        return line >= 1 && line <= file.Rows.Length ? file.Rows[line - 1].TextOf(pane).Length : 0;
    }

    // ═══════════════════════════════ Sélection ═══════════════════════════════

    /// <summary>
    /// Dessine une sélection, en effaçant la précédente là où elle n'est plus.
    /// </summary>
    /// <param name="selection">Sélection à montrer, ou <c>null</c> pour n'en montrer aucune</param>
    private void ShowSelection(PreviewSelection? selection)
    {
        if (_shownSelection is { } shown
            && (selection is null || !ReferenceEquals(shown.File, selection.File) || shown.Pane != selection.Pane))
        {
            for (int line = Math.Max(1, shown.First); line <= Math.Min(shown.File.Rows.Length, shown.Last); line++)
            {
                shown.File.Rows[line - 1].SetSelection(shown.Pane, null);
            }

            _shownSelection = null;
        }

        _selection = selection;
        if (selection is null)
        {
            return;
        }

        // Seules les lignes entre l'ancienne et la nouvelle étendue peuvent
        // changer : chacune ne se recompose que si sa portion a changé.
        int first = selection.Start.Line;
        int last = selection.End.Line;
        int from = Math.Min(first, _shownSelection?.First ?? first);
        int to = Math.Max(last, _shownSelection?.Last ?? last);

        for (int line = Math.Max(1, from); line <= Math.Min(selection.File.Rows.Length, to); line++)
        {
            selection.File.Rows[line - 1].SetSelection(selection.Pane, selection.RangeOn(line));
        }

        _shownSelection = (selection.File, selection.Pane, first, last);
    }

    /// <summary>
    /// Oublie la sélection d'un aperçu qui disparaît, ou de tous.
    /// </summary>
    /// <param name="file">Aperçu concerné, ou <c>null</c> pour tous</param>
    private void ForgetSelection(FilePreviewModel? file = null)
    {
        if (_selection is null || file is null || ReferenceEquals(_selection.File, file))
        {
            EndSelectionDrag();
            ShowSelection(null);
        }
    }

    /// <summary>
    /// Sélectionne le mot sous une position : lettres, chiffres, et les
    /// caractères qui tiennent ensemble une adresse, un nom de machine ou un
    /// courriel — point, trait d'union, souligné, arobase.
    /// </summary>
    /// <param name="file">Aperçu du fichier</param>
    /// <param name="pane">Volet</param>
    /// <param name="position">Position cliquée</param>
    private void SelectWordAt(FilePreviewModel file, PreviewPane pane, PanePosition position)
    {
        string text = file.Rows[position.Line - 1].TextOf(pane);
        int offset = Math.Clamp(position.Offset, 0, text.Length);

        // Juste après un mot, c'est lui qu'on vise.
        int probe = offset < text.Length && IsWordCharacter(text[offset]) ? offset : offset - 1;
        if (probe < 0 || probe >= text.Length || !IsWordCharacter(text[probe]))
        {
            ShowSelection(new PreviewSelection(file, pane, position with { Offset = offset }));
            return;
        }

        int start = probe;
        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }

        int end = probe + 1;
        while (end < text.Length && IsWordCharacter(text[end]))
        {
            end++;
        }

        // Un point ou un tiret au bord n'appartient pas au mot : c'est la fin
        // d'une phrase, ou une puce.
        while (end > start && text[end - 1] is '.' or '-')
        {
            end--;
        }

        while (start < end && text[start] is '.' or '-')
        {
            start++;
        }

        ShowSelection(new PreviewSelection(file, pane, new PanePosition(position.Line, start))
        {
            Active = new PanePosition(position.Line, end),
        });
    }

    /// <summary>
    /// Indique si un caractère fait partie d'un mot pour le double-clic.
    /// </summary>
    /// <param name="character">Caractère examiné</param>
    /// <returns><c>true</c> s'il prolonge le mot</returns>
    private static bool IsWordCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '@';
    }

    /// <summary>
    /// Sélectionne une ligne entière d'un volet.
    /// </summary>
    /// <param name="file">Aperçu du fichier</param>
    /// <param name="pane">Volet</param>
    /// <param name="line">Numéro de ligne</param>
    private void SelectLine(FilePreviewModel file, PreviewPane pane, int line)
    {
        ShowSelection(new PreviewSelection(file, pane, new PanePosition(line, 0))
        {
            Active = new PanePosition(line, LineLength(file, line, pane)),
        });
    }

    /// <summary>
    /// Sélectionne tout le texte d'un volet de l'aperçu affiché : le volet de
    /// la sélection en cours, l'original à défaut.
    /// </summary>
    private void SelectAllInPreview()
    {
        if ((PreviewTabs.SelectedItem as TabItem)?.Tag is not string path
            || !_previewLists.TryGetValue(path, out ListBox? list)
            || list.Tag is not FilePreviewModel file
            || file.Rows.Length == 0)
        {
            return;
        }

        PreviewPane pane = _selection is not null && ReferenceEquals(_selection.File, file) ? _selection.Pane : PreviewPane.Original;
        int last = file.Rows.Length;

        ShowSelection(new PreviewSelection(file, pane, new PanePosition(1, 0))
        {
            Active = new PanePosition(last, LineLength(file, last, pane)),
        });
    }

    // ═══════════════════════════════ Copie ═══════════════════════════════

    /// <summary>
    /// Copie le texte sélectionné dans le presse-papiers.
    /// </summary>
    private void CopySelection()
    {
        if (_selection is not { IsEmpty: false } selection || _project is null || _busy)
        {
            return;
        }

        string text = selection.Text();
        if (text.Length == 0)
        {
            return;
        }

        string note = "Il contient les valeurs réelles : c'est le volet de droite qu'on colle dans l'IA.";
        if (selection.Pane == PreviewPane.Masked && !PrepareMaskedCopy(selection, out note))
        {
            return;
        }

        try
        {
            ClipboardBridge.Write(new ClipboardContent(text, null), ClipboardOwner);
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, success: false);
            return;
        }

        int lines = selection.End.Line - selection.Start.Line + 1;
        string origin = selection.Pane == PreviewPane.Masked ? "pseudonymisé" : "d'origine";
        SetStatus(
            string.Create(CultureInfo.CurrentCulture, $"Copié : {lines} ligne(s) du texte {origin}. {note}"),
            selection.Pane == PreviewPane.Masked ? true : null);
    }

    /// <summary>
    /// Vérifie qu'un passage du volet pseudonymisé peut partir tel qu'il est
    /// affiché, et fait entrer ses pseudonymes au coffre.
    /// </summary>
    /// <param name="selection">Sélection à copier</param>
    /// <param name="note">Compte rendu à ajouter au message de copie</param>
    /// <returns><c>true</c> si la copie peut avoir lieu</returns>
    private bool PrepareMaskedCopy(PreviewSelection selection, out string note)
    {
        note = string.Empty;

        // Un aperçu repris par une analyse limitée aux fichiers ouverts montre
        // des pseudonymes nouveaux qui ne figurent pas parmi les attributions
        // de l'analyse affichée : ils ne pourraient pas être restitués.
        if (!IsStale && IsPreviewStale(selection.File.Preview.Path))
        {
            SetStatus(
                "Copie refusée : cet aperçu n'a pas été relu par la dernière analyse, limitée aux fichiers ouverts. Attendez sa mise à jour (ou F5), puis copiez de nouveau.",
                success: false);
            return false;
        }

        if (IsStale || _analysis is null)
        {
            SetStatus(
                "Copie refusée : les règles ont changé depuis l'analyse affichée, et le texte pseudonymisé ne les reflète pas encore. Attendez la nouvelle analyse (F5), puis copiez de nouveau.",
                success: false);
            return false;
        }

        bool pending = selection.File
            .MarkersOn(selection.Start.Line, selection.End.Line)
            .Any(marker => marker.State == PreviewMarkerState.PendingMask
                || (marker.State == PreviewMarkerState.Masked && marker.Pseudonym.Length == 0));

        if (pending)
        {
            SetStatus(
                "Copie refusée : la sélection contient des valeurs retenues pour être masquées, dont le pseudonyme n'est pas encore attribué — elles partiraient en clair. Attendez la fin de l'analyse, puis copiez de nouveau.",
                success: false);
            return false;
        }

        // Les attributions de l'analyse entrent au coffre, graphies nouvelles
        // d'une valeur déjà connue comprises : la réponse de l'IA pourra être
        // restaurée sous la forme écrite dans la sélection.
        ObfuscationProject project = _project!;
        VaultDelta delta = _analysis.Result.Delta;
        (int Adopted, int Conflicts) adoption = (0, 0);

        if (!Run(() => adoption = new VaultPresenter(project).Adopt(delta)))
        {
            return false;
        }

        if (adoption.Conflicts > 0)
        {
            SetStatus(
                "Copie refusée : le coffre a changé depuis l'analyse affichée, et ces pseudonymes n'y correspondent plus. Relancez l'analyse (F5), puis copiez de nouveau.",
                success: false);
            return false;
        }

        note = adoption.Adopted > 0
            ? "Prêt à coller dans l'IA : ses pseudonymes sont enregistrés au coffre, sa réponse pourra être restaurée."
            : "Prêt à coller dans l'IA.";
        return true;
    }

    // ═══════════════════════════════ Menu ═══════════════════════════════

    /// <summary>
    /// Ouvre le menu de la sélection. Un clic droit hors de la sélection
    /// sélectionne d'abord le mot sous la souris.
    /// </summary>
    /// <param name="sender">Liste d'aperçu</param>
    /// <param name="e">Clic droit</param>
    private void OnPreviewTextRightClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (sender is not ListBox list || list.Tag is not FilePreviewModel file || _project is null || _busy
            || FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null
            || !TryHitPane(list, e.GetPosition(list), null, out PreviewPane pane, out PanePosition position))
        {
            return;
        }

        list.Focus();
        if (_selection is null || !ReferenceEquals(_selection.File, file) || _selection.Pane != pane || !_selection.Contains(position))
        {
            SelectWordAt(file, pane, position);
        }

        ContextMenu menu = BuildSelectionMenu(list, file);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Compose le menu de la sélection : copier, masquer sous une nature,
    /// garder en clair partout, tout sélectionner.
    /// </summary>
    /// <param name="list">Liste d'aperçu</param>
    /// <param name="file">Aperçu du fichier</param>
    /// <returns>Le menu, prêt à s'ouvrir sous la souris</returns>
    private ContextMenu BuildSelectionMenu(ListBox list, FilePreviewModel file)
    {
        ContextMenu menu = new() { PlacementTarget = list, Placement = PlacementMode.MousePoint };
        PreviewSelection? selection = _selection;

        MenuItem copy = new()
        {
            Header = selection?.Pane == PreviewPane.Masked ? "Copier le texte pseudonymisé" : "Copier",
            InputGestureText = "Ctrl+C",
            IsEnabled = selection is { IsEmpty: false },
        };
        copy.Click += (_, _) => CopySelection();
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());

        string? term = MaskableTerm(selection, out string reason);
        if (term is null)
        {
            menu.Items.Add(new MenuItem { Header = MenuText(reason), IsEnabled = false });
        }
        else if (_project!.Policy.IsKeptEverywhere(term))
        {
            menu.Items.Add(new MenuItem { Header = MenuText($"« {Shorten(term)} » est dans la liste blanche : en clair partout"), IsEnabled = false });
            MenuItem release = new() { Header = "Retirer de la liste blanche" };
            release.Click += (_, _) => ReleaseWhitelistValue(term);
            menu.Items.Add(release);
        }
        else
        {
            AddMaskChoices(menu, term);
            menu.Items.Add(new Separator());

            MenuItem keep = new() { Header = MenuText($"Garder « {Shorten(term)} » en clair partout (liste blanche)") };
            keep.Click += (_, _) => KeepEverywhere(term);
            menu.Items.Add(keep);
        }

        menu.Items.Add(new Separator());
        MenuItem all = new() { Header = "Tout sélectionner", InputGestureText = "Ctrl+A" };
        all.Click += (_, _) =>
        {
            PreviewPane pane = selection?.Pane ?? PreviewPane.Original;
            int last = file.Rows.Length;
            ShowSelection(new PreviewSelection(file, pane, new PanePosition(1, 0))
            {
                Active = new PanePosition(last, LineLength(file, last, pane)),
            });
        };
        menu.Items.Add(all);

        return menu;
    }

    /// <summary>
    /// Ajoute au menu les natures sous lesquelles masquer un terme. Un terme
    /// qui a la forme d'une adresse ne peut être qu'une adresse ; un autre ne
    /// peut pas en être une.
    /// </summary>
    /// <param name="menu">Menu en cours de composition</param>
    /// <param name="term">Terme sélectionné</param>
    private void AddMaskChoices(ContextMenu menu, string term)
    {
        bool address = ReviewPresenter.IsAddressLike(term);
        menu.Items.Add(new MenuItem
        {
            Header = MenuText(address ? $"Masquer « {Shorten(term)} », une adresse :" : $"Masquer « {Shorten(term)} » comme…"),
            IsEnabled = false,
        });

        foreach (CategoryChoice choice in CategoryChoices().Where(choice => (choice.Value == SpanCategory.IpV4) == address))
        {
            MenuItem item = new() { Header = choice.Label };
            SpanCategory category = choice.Value;
            item.Click += (_, _) => MaskTerm(term, category);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Donne le terme qu'une sélection permet de masquer ou de garder, ou la
    /// raison pour laquelle elle ne le permet pas.
    /// </summary>
    /// <param name="selection">Sélection en cours</param>
    /// <param name="reason">Raison du refus, à afficher dans le menu</param>
    /// <returns>Le terme, débarrassé de ses blancs, ou <c>null</c></returns>
    private static string? MaskableTerm(PreviewSelection? selection, out string reason)
    {
        reason = string.Empty;
        if (selection is null || selection.IsEmpty)
        {
            reason = "Sélectionnez un passage pour le masquer ou le garder";
            return null;
        }

        if (!selection.IsSingleLine)
        {
            reason = "Pour masquer ou garder un terme, sélectionnez-le sur une seule ligne";
            return null;
        }

        string term = selection.Text().Trim();
        if (term.Length < Vocabulary.MinimumTermLength)
        {
            reason = string.Create(CultureInfo.CurrentCulture, $"Un terme compte au moins {Vocabulary.MinimumTermLength} caractères");
            return null;
        }

        if (term.Length > LongestTerm)
        {
            reason = "Passage trop long pour en faire un terme";
            return null;
        }

        if (selection.Pane == PreviewPane.Masked && TouchesPseudonym(selection))
        {
            reason = "Ce passage contient un pseudonyme : sélectionnez le texte d'origine, à gauche";
            return null;
        }

        return term;
    }

    /// <summary>
    /// Indique si une sélection du volet pseudonymisé touche un pseudonyme.
    /// </summary>
    /// <param name="selection">Sélection sur une ligne</param>
    /// <returns><c>true</c> si un pseudonyme est en partie sélectionné</returns>
    private static bool TouchesPseudonym(PreviewSelection selection)
    {
        int position = 0;
        foreach (PreviewSegment segment in selection.File.Rows[selection.Start.Line - 1].BaseOf(PreviewPane.Masked))
        {
            int end = position + segment.Text.Length;
            if (segment.Kind == PreviewSegmentKind.Masked && position < selection.End.Offset && selection.Start.Offset < end)
            {
                return true;
            }

            position = end;
        }

        return false;
    }

    /// <summary>
    /// Masque un terme choisi dans l'aperçu, sous une nature donnée.
    /// </summary>
    /// <param name="term">Terme à masquer</param>
    /// <param name="category">Nature choisie</param>
    private void MaskTerm(string term, SpanCategory category)
    {
        SpanCategory nature = ReviewPresenter.IsAddressLike(term) ? SpanCategory.IpV4 : category;

        if (Decide(
                presenter =>
                {
                    presenter.RestoreSuggestion(term);
                    presenter.AddMaskedTerm(term, nature);
                },
                $"« {Shorten(term)} » sera masqué partout où il apparaît, comme : {CategoryLabels.Label(nature)}."))
        {
            ApplyLiveStates(term);
            ApplyProposalState(term, PreviewMarkerState.PendingMask, SuggestionState.Accepted);
            DeferRebuildMaskTargets();
        }
    }
}
