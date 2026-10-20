using System.Globalization;
using System.IO;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Aperçu d'un fichier prêt à être affiché : ses lignes, ses portions repérées
/// et les décisions qu'elles proposent.
///
/// Il est construit en arrière-plan, puis vit sur le fil de l'écran : les
/// décisions de l'utilisateur y modifient l'état des repères, et seules les
/// lignes concernées sont recomposées.
/// </summary>
internal sealed class FilePreviewModel
{
    /// <summary>
    /// Nombre de lignes de contexte gardées autour d'une ligne utile. Deux
    /// suffisent à situer une valeur — la section d'une configuration, la clé
    /// d'un fichier JSON — sans noyer l'essentiel.
    /// </summary>
    public const int ContextLines = 2;

    /// <summary>
    /// En deçà de ce nombre, les lignes sans intérêt restent affichées : une
    /// ligne de repli prendrait autant de place que ce qu'elle cache.
    /// </summary>
    public const int MinimumGap = 3;

    /// <summary>
    /// Construit l'aperçu à partir de ses éléments.
    /// </summary>
    /// <param name="preview">Aperçu calculé par le Presenter</param>
    /// <param name="rows">Lignes du fichier</param>
    /// <param name="markers">Portions repérées, triées</param>
    /// <param name="entries">Décisions proposées, une par portion</param>
    public FilePreviewModel(
        FilePreview preview,
        PreviewLineRow[] rows,
        IReadOnlyList<PreviewMarker> markers,
        IReadOnlyList<PreviewEntryRow> entries)
    {
        Preview = preview;
        FileName = Path.GetFileName(preview.Path);
        FileKey = string.IsNullOrEmpty(preview.Key) ? FileName : preview.Key;
        Rows = rows;
        Markers = markers;
        Entries = entries;
    }

    /// <summary>Aperçu calculé par le Presenter.</summary>
    public FilePreview Preview { get; }

    /// <summary>Nom du fichier.</summary>
    public string FileName { get; }

    /// <summary>
    /// Clé du fichier pour ses exceptions : son chemin dans la source
    /// (<c>/zigbee2mqtt/configuration.yaml</c>), et non son seul nom, que deux
    /// fichiers peuvent partager.
    /// </summary>
    public string FileKey { get; }

    /// <summary>Toutes les lignes du fichier, dans l'ordre.</summary>
    public PreviewLineRow[] Rows { get; }

    /// <summary>Portions repérées, triées par position.</summary>
    public IReadOnlyList<PreviewMarker> Markers { get; }

    /// <summary>Décisions proposées, une par portion repérée.</summary>
    public IReadOnlyList<PreviewEntryRow> Entries { get; }

    /// <summary>
    /// Compose la liste affichée : toutes les lignes, ou seulement les lignes
    /// utiles et leur voisinage, le reste étant replié.
    /// </summary>
    /// <param name="showAll">Vrai pour afficher toutes les lignes</param>
    /// <param name="expanded">
    /// Intervalles de lignes que l'utilisateur a dépliés. Ils sont exprimés en
    /// numéros de ligne, et non en replis : ils survivent ainsi à une nouvelle
    /// analyse, qui peut déplacer les bornes des replis.
    /// </param>
    /// <param name="filter">
    /// Ce qui est mis en avant : les lignes qui n'en portent pas sont repliées,
    /// ou estompées quand toutes les lignes sont affichées.
    /// </param>
    /// <returns>Les lignes et intervalles repliés, dans l'ordre du fichier</returns>
    public List<object> Display(bool showAll, IReadOnlyCollection<(int First, int Last)> expanded, PreviewFilter filter = PreviewFilter.All)
    {
        ArgumentNullException.ThrowIfNull(expanded);

        // Sous un filtre, une ligne de contexte ou dépliée reste visible mais
        // estompée : l'œil va droit à ce qu'on a demandé de voir.
        bool[] useful = new bool[Rows.Length];
        for (int index = 0; index < Rows.Length; index++)
        {
            useful[index] = Rows[index].Matches(filter);
            Rows[index].SetDimmed(filter != PreviewFilter.All && !useful[index]);
        }

        if (showAll)
        {
            return [.. Rows];
        }

        bool[] keep = new bool[Rows.Length];
        for (int index = 0; index < Rows.Length; index++)
        {
            if (!useful[index])
            {
                continue;
            }

            int first = Math.Max(0, index - ContextLines);
            int last = Math.Min(Rows.Length - 1, index + ContextLines);
            for (int neighbour = first; neighbour <= last; neighbour++)
            {
                keep[neighbour] = true;
            }
        }

        foreach ((int first, int last) in expanded)
        {
            for (int line = Math.Max(1, first); line <= Math.Min(Rows.Length, last); line++)
            {
                keep[line - 1] = true;
            }
        }

        List<object> items = [];
        int cursor = 0;

        while (cursor < Rows.Length)
        {
            if (keep[cursor])
            {
                items.Add(Rows[cursor]);
                cursor++;
                continue;
            }

            int start = cursor;
            while (cursor < Rows.Length && !keep[cursor])
            {
                cursor++;
            }

            if (cursor - start < MinimumGap)
            {
                for (int index = start; index < cursor; index++)
                {
                    items.Add(Rows[index]);
                }
            }
            else
            {
                items.Add(new PreviewGapRow(start + 1, cursor));
            }
        }

        return items;
    }

    /// <summary>
    /// Compte les portions repérées dans un état donné.
    /// </summary>
    /// <param name="state">État recherché</param>
    /// <returns>Le nombre de portions dans cet état</returns>
    public int CountOf(PreviewMarkerState state)
    {
        return Markers.Count(marker => marker.State == state);
    }

    /// <summary>
    /// Recompose les lignes couvertes par un repère dont l'état a changé.
    /// </summary>
    /// <param name="marker">Repère modifié</param>
    public void Refresh(PreviewMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        int first = Math.Max(1, marker.FirstLine);
        int last = Math.Min(Rows.Length, marker.LastLine);
        for (int line = first; line <= last; line++)
        {
            Rows[line - 1].Refresh();
        }
    }

    /// <summary>
    /// Donne le rang d'un repère parmi les occurrences de sa valeur sur sa
    /// ligne, tel que le moteur le calcule pour une exception ponctuelle.
    /// </summary>
    /// <param name="marker">Repère concerné</param>
    /// <returns>Le rang, à partir de 1</returns>
    public int OccurrenceOf(PreviewMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return LineExemption.OccurrenceAt(Preview.Text, marker.Start, marker.Value);
    }

    /// <summary>
    /// Énumère les repères qui touchent un intervalle de lignes.
    /// </summary>
    /// <param name="firstLine">Première ligne, à partir de 1</param>
    /// <param name="lastLine">Dernière ligne</param>
    /// <returns>Les repères concernés</returns>
    public IEnumerable<PreviewMarker> MarkersOn(int firstLine, int lastLine)
    {
        return Markers.Where(marker => marker.FirstLine <= lastLine && marker.LastLine >= firstLine);
    }
}

/// <summary>
/// Compose l'aperçu côte à côte d'un fichier : le texte d'origine à gauche, le
/// texte pseudonymisé à droite, ligne par ligne.
///
/// Les deux volets sont construits à partir du même découpage en lignes, de
/// sorte qu'ils se correspondent exactement. Lorsqu'un remplacement s'étend sur
/// plusieurs lignes — un bloc de clé — le pseudonyme est réparti sur le même
/// nombre de lignes que l'original : sans cela, tout le fichier se décalerait à
/// partir de là et la comparaison perdrait son sens.
///
/// La composition ne touche à aucun élément graphique : elle peut s'exécuter en
/// arrière-plan, à la suite de l'analyse.
/// </summary>
internal static class PreviewBuilder
{
    /// <summary>Largeur approximative d'un chiffre de la gouttière, en pixels.</summary>
    private const double DigitWidth = 7.4;

    /// <summary>
    /// Compose l'aperçu d'un fichier.
    /// </summary>
    /// <param name="preview">Aperçu calculé par le Presenter</param>
    /// <param name="exemptions">Occurrences laissées en clair au moment de l'analyse</param>
    /// <returns>L'aperçu prêt à être affiché</returns>
    public static FilePreviewModel Build(FilePreview preview, IReadOnlyList<LineExemption> exemptions)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(exemptions);

        // Les exceptions désignent le fichier par son chemin dans la source :
        // deux fichiers de même nom ne partagent pas les leurs.
        string fileKey = string.IsNullOrEmpty(preview.Key) ? Path.GetFileName(preview.Path) : preview.Key;
        Bounds[] lines = SplitLines(preview.Text);
        List<PreviewMarker> markers = CollectMarkers(preview, exemptions, lines, fileKey);

        int digits = lines.Length.ToString(CultureInfo.InvariantCulture).Length;
        double gutterWidth = (digits * DigitWidth) + 4;

        Dictionary<PreviewMarker, PreviewEntryRow> entries = new(markers.Count);
        foreach (PreviewMarker marker in markers)
        {
            entries[marker] = new PreviewEntryRow(marker, fileKey);
        }

        PreviewLineRow[] rows = new PreviewLineRow[lines.Length];
        int next = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            Bounds line = lines[index];
            List<PreviewPart> parts = [];
            List<PreviewEntryRow> lineEntries = [];
            int cursor = line.Start;

            // Les repères sont triés et disjoints, et les lignes parcourues
            // dans l'ordre : on reprend à chaque ligne là où la précédente
            // s'est arrêtée, ce qui garde la composition linéaire.
            while (next < markers.Count && markers[next].End <= line.Start)
            {
                next++;
            }

            // La borne est incluse : un repère qui commence sur le saut de
            // ligne lui-même appartient encore à cette ligne, qui doit porter
            // sa décision.
            int scan = next;
            while (scan < markers.Count && markers[scan].Start <= line.End)
            {
                PreviewMarker marker = markers[scan];
                int start = Math.Max(marker.Start, line.Start);
                int end = Math.Min(marker.End, line.End);

                if (end > start)
                {
                    if (start > cursor)
                    {
                        parts.Add(new PreviewPart(preview.Text[cursor..start], null));
                    }

                    parts.Add(new PreviewPart(preview.Text[start..end], marker));
                    cursor = end;
                }

                if (marker.Start >= line.Start && marker.Start <= line.End)
                {
                    lineEntries.Add(entries[marker]);
                }

                // Un repère qui se poursuit sur la ligne suivante y sera repris.
                if (marker.End > line.End)
                {
                    break;
                }

                scan++;
            }

            next = scan;

            if (cursor < line.End)
            {
                parts.Add(new PreviewPart(preview.Text[cursor..line.End], null));
            }

            rows[index] = new PreviewLineRow(
                index + 1,
                (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits),
                gutterWidth,
                parts,
                lineEntries);
        }

        return new FilePreviewModel(preview, rows, markers, [.. entries.Values]);
    }

    /// <summary>
    /// Rassemble les portions à signaler : remplacements, éléments suspects et
    /// occurrences exceptées, en écartant les chevauchements.
    /// </summary>
    /// <param name="preview">Aperçu du fichier</param>
    /// <param name="exemptions">Occurrences laissées en clair</param>
    /// <param name="lines">Bornes des lignes</param>
    /// <param name="fileKey">Clé du fichier pour ses exceptions</param>
    /// <returns>Les repères, triés et deux à deux disjoints</returns>
    private static List<PreviewMarker> CollectMarkers(
        FilePreview preview,
        IReadOnlyList<LineExemption> exemptions,
        Bounds[] lines,
        string fileKey)
    {
        List<(PreviewMarker Marker, int Priority)> candidates = [];

        foreach (PreviewSpan span in preview.Spans)
        {
            candidates.Add((new PreviewMarker
            {
                Start = span.Start,
                End = span.Start + span.Length,
                FirstLine = LineOf(lines, span.Start),
                LastLine = LineOf(lines, span.Start + Math.Max(0, span.Length - 1)),
                Value = span.Value,
                Category = span.Category,
                Pseudonym = span.Pseudonym,
                State = PreviewMarkerState.Masked,
            }, 0));
        }

        foreach (LineExemption exemption in exemptions)
        {
            if (!exemption.AppliesTo(fileKey))
            {
                continue;
            }

            foreach ((int start, int end) in LocateExemption(preview.Text, lines, exemption))
            {
                candidates.Add((new PreviewMarker
                {
                    Start = start,
                    End = end,
                    FirstLine = exemption.Line,
                    LastLine = LineOf(lines, Math.Max(start, end - 1)),
                    Value = exemption.Value,
                    Category = SpanCategory.Other,
                    State = PreviewMarkerState.Exempted,
                }, 2));
            }
        }

        // Un signalement gardé en clair à cet endroit reste un signalement :
        // annuler la décision doit le rendre à trancher, et non masqué.
        foreach (ResidualFinding finding in preview.Residuals)
        {
            candidates.Add((new PreviewMarker
            {
                Start = finding.Start,
                End = finding.Start + finding.Length,
                FirstLine = LineOf(lines, finding.Start),
                LastLine = LineOf(lines, finding.Start + Math.Max(0, finding.Length - 1)),
                Value = finding.Value,
                Category = finding.SuggestedCategory,
                IsProposal = true,
                FindingKind = finding.Kind,
                Advice = finding.Advice,
                State = finding.IsExempted ? PreviewMarkerState.Exempted : PreviewMarkerState.Suspect,
            }, finding.IsExempted ? 1 : 3));
        }

        // À position égale, un remplacement l'emporte sur une exception, qui
        // l'emporte sur un signalement : une occurrence exceptée peut aussi
        // être relevée comme suspecte, et c'est la décision de l'utilisateur
        // qui doit se voir — portée, si elle en vient, par le signalement.
        candidates.Sort((left, right) =>
        {
            int byStart = left.Marker.Start.CompareTo(right.Marker.Start);
            return byStart != 0 ? byStart : left.Priority.CompareTo(right.Priority);
        });

        List<PreviewMarker> disjoint = [];
        int cursor = 0;
        foreach ((PreviewMarker marker, _) in candidates)
        {
            if (marker.Start >= cursor && marker.End > marker.Start)
            {
                disjoint.Add(marker);
                cursor = marker.End;
            }
        }

        return disjoint;
    }

    /// <summary>
    /// Retrouve dans le texte l'occurrence visée par une exception.
    ///
    /// La recherche se limite à la ligne enregistrée, et au rang retenu sur
    /// cette ligne : c'est le point de l'exception, et cela évite de barrer par
    /// erreur une autre occurrence de la même valeur. Une exception sans rang,
    /// posée par une version antérieure, vise toutes celles de la ligne.
    /// </summary>
    /// <param name="text">Texte d'origine</param>
    /// <param name="lines">Bornes des lignes</param>
    /// <param name="exemption">Exception à situer</param>
    /// <returns>Les intervalles correspondants, vides si la ligne n'existe plus</returns>
    private static IEnumerable<(int Start, int End)> LocateExemption(string text, Bounds[] lines, LineExemption exemption)
    {
        if (exemption.Line < 1 || exemption.Line > lines.Length || exemption.Value.Length == 0)
        {
            return [];
        }

        Bounds line = lines[exemption.Line - 1];
        return LineExemption
            .Locate(text, line.Start, line.End, exemption.Value, exemption.Occurrence)
            .Select(position => (position, position + exemption.Value.Length));
    }

    /// <summary>
    /// Retrouve le numéro de ligne d'une position.
    /// </summary>
    /// <param name="lines">Bornes des lignes</param>
    /// <param name="position">Position dans le texte</param>
    /// <returns>Le numéro de ligne, à partir de 1</returns>
    private static int LineOf(Bounds[] lines, int position)
    {
        int low = 0;
        int high = lines.Length - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (position < lines[middle].Start)
            {
                high = middle - 1;
            }
            else if (middle + 1 < lines.Length && position >= lines[middle + 1].Start)
            {
                low = middle + 1;
            }
            else
            {
                return middle + 1;
            }
        }

        return Math.Clamp(low + 1, 1, Math.Max(1, lines.Length));
    }

    /// <summary>
    /// Découpe le texte en lignes, en retenant leurs bornes plutôt que leur
    /// contenu : les portions à signaler sont exprimées en positions absolues.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <returns>Les bornes de chaque ligne</returns>
    private static Bounds[] SplitLines(string text)
    {
        List<Bounds> lines = [];
        int start = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            int end = index > start && text[index - 1] == '\r' ? index - 1 : index;
            lines.Add(new Bounds(start, end));
            start = index + 1;
        }

        lines.Add(new Bounds(start, text.Length));
        return [.. lines];
    }

    /// <summary>Bornes d'une ligne dans le texte source.</summary>
    /// <param name="Start">Première position de la ligne</param>
    /// <param name="End">Position suivant le dernier caractère affichable</param>
    private readonly record struct Bounds(int Start, int End);
}
