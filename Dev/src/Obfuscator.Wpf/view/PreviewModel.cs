using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Wpf.View;

/// <summary>Nature d'un fragment de ligne dans l'aperçu, qui décide de sa couleur.</summary>
public enum PreviewSegmentKind
{
    /// <summary>Texte laissé tel quel.</summary>
    Plain,

    /// <summary>Portion remplacée par un pseudonyme.</summary>
    Masked,

    /// <summary>Portion signalée par le moteur sans être remplacée.</summary>
    Suspect,

    /// <summary>Portion que l'utilisateur a choisi de laisser en clair à cet endroit.</summary>
    Exempted,

    /// <summary>Portion qui sera masquée, mais dont le pseudonyme n'est pas encore connu.</summary>
    Pending,

    /// <summary>Portion que la liste blanche du projet garde en clair partout.</summary>
    Allowed,
}

/// <summary>
/// Fragment de ligne, avec la couleur qu'il doit porter.
/// </summary>
/// <param name="Text">Texte du fragment</param>
/// <param name="Kind">Nature du fragment</param>
/// <param name="Tooltip">Explication affichée au survol, s'il y en a une</param>
public sealed record PreviewSegment(string Text, PreviewSegmentKind Kind, string? Tooltip = null)
{
    /// <summary>Vrai si le fragment fait partie du texte sélectionné.</summary>
    public bool Selected { get; init; }
}

/// <summary>Volet de l'aperçu côte à côte.</summary>
public enum PreviewPane
{
    /// <summary>Texte d'origine, à gauche.</summary>
    Original,

    /// <summary>Texte pseudonymisé, à droite, tel qu'il sera écrit.</summary>
    Masked,
}

/// <summary>
/// État d'une portion repérée, tel que l'écran le montre.
///
/// L'état de départ vient de l'analyse ; les décisions de l'utilisateur le
/// font ensuite évoluer sur place. C'est ce qui permet de refléter une
/// décision immédiatement, sans relancer l'analyse : l'écran sait à quoi
/// ressemble chaque état, il n'a besoin que de savoir lequel s'applique.
/// </summary>
public enum PreviewMarkerState
{
    /// <summary>Sera remplacée par son pseudonyme.</summary>
    Masked,

    /// <summary>Gardée en clair à cet endroit précis : cette occurrence-ci, et elle seule.</summary>
    Exempted,

    /// <summary>Signalée par le moteur, sans décision prise.</summary>
    Suspect,

    /// <summary>Signalée puis retenue : elle sera masquée, son pseudonyme viendra à la prochaine analyse.</summary>
    PendingMask,

    /// <summary>Gardée en clair partout : la valeur a rejoint la liste blanche du projet.</summary>
    Whitelisted,
}

/// <summary>
/// Ce que l'aperçu met en avant : toutes les portions repérées, ou une seule
/// sorte — les valeurs remplacées, les signalements à trancher, ou les
/// valeurs retenues pour être masquées.
///
/// Le filtre décide des lignes utiles. Lignes repliées, seules elles restent
/// visibles, avec leur contexte ; toutes les lignes affichées, les autres sont
/// estompées.
/// </summary>
public enum PreviewFilter
{
    /// <summary>Toutes les portions repérées, quel que soit leur état.</summary>
    All,

    /// <summary>Les valeurs remplacées.</summary>
    Masked,

    /// <summary>Les signalements qui restent à trancher.</summary>
    Suspect,

    /// <summary>Les valeurs retenues pour être masquées, en attente de leur pseudonyme.</summary>
    PendingMask,
}

/// <summary>
/// Portion repérée dans un fichier : une valeur remplacée, un signalement, ou
/// une occurrence exceptée.
///
/// Un repère est partagé par toutes les lignes qu'il couvre — un bloc de clé en
/// occupe plusieurs — pour qu'une décision prise sur la première se voie sur
/// toutes.
/// </summary>
public sealed class PreviewMarker
{
    /// <summary>Position du début dans le texte source.</summary>
    public required int Start { get; init; }

    /// <summary>Position de fin, exclue.</summary>
    public required int End { get; init; }

    /// <summary>Numéro de la première ligne couverte, à partir de 1.</summary>
    public required int FirstLine { get; init; }

    /// <summary>Numéro de la dernière ligne couverte.</summary>
    public required int LastLine { get; init; }

    /// <summary>Valeur réelle couverte.</summary>
    public required string Value { get; init; }

    /// <summary>Nature de la valeur.</summary>
    public required SpanCategory Category { get; init; }

    /// <summary>Pseudonyme complet qui la remplacera ; vide s'il n'est pas encore attribué.</summary>
    public string Pseudonym { get; init; } = string.Empty;

    /// <summary>
    /// Vrai pour une proposition du moteur — une portion signalée — et faux
    /// pour une occurrence remplacée ou exceptée. La nature ne change pas avec
    /// les décisions ; l'état, si.
    /// </summary>
    public bool IsProposal { get; init; }

    /// <summary>Nature du signalement, pour une portion signalée.</summary>
    public string FindingKind { get; init; } = string.Empty;

    /// <summary>Conseil associé au signalement.</summary>
    public string Advice { get; init; } = string.Empty;

    /// <summary>État actuel, modifié par les décisions de l'utilisateur.</summary>
    public PreviewMarkerState State { get; set; }

    /// <summary>
    /// Retourne la part du pseudonyme qui revient à une ligne donnée. Un
    /// pseudonyme de bloc s'étend sur autant de lignes que l'original, pour
    /// que les deux volets gardent le même découpage.
    /// </summary>
    /// <param name="lineNumber">Numéro de la ligne affichée</param>
    /// <returns>Le fragment de pseudonyme de cette ligne</returns>
    public string PseudonymLine(int lineNumber)
    {
        if (FirstLine == LastLine)
        {
            return Pseudonym;
        }

        string[] parts = Pseudonym.Split('\n');
        int offset = Math.Max(0, lineNumber - FirstLine);
        return offset < parts.Length ? parts[offset].TrimEnd('\r') : string.Empty;
    }
}

/// <summary>
/// Fragment de ligne tel qu'il figure dans le texte source, éventuellement
/// couvert par un repère.
/// </summary>
/// <param name="Text">Texte d'origine du fragment</param>
/// <param name="Marker">Repère qui le couvre, s'il y en a un</param>
public sealed record PreviewPart(string Text, PreviewMarker? Marker);

/// <summary>
/// Décision proposée sur une ligne de l'aperçu, pour une portion repérée.
///
/// Une même ligne peut en porter plusieurs : une configuration réseau met
/// volontiers une adresse, un masque et un nom d'hôte sur la même ligne. Selon
/// l'état de la portion, l'écran propose de garder une valeur masquée — ici
/// seulement, ou partout —, de masquer ou de garder une valeur signalée, ou
/// rappelle la décision prise, avec de quoi l'annuler.
/// </summary>
/// <param name="marker">Portion concernée</param>
/// <param name="file">Clé du fichier pour ses exceptions : son chemin dans la source, précédé d'une barre</param>
public sealed class PreviewEntryRow(PreviewMarker marker, string file) : NotifyingObject
{
    /// <summary>Portion concernée.</summary>
    public PreviewMarker Marker { get; } = marker;

    /// <summary>Clé du fichier, pour situer l'exception.</summary>
    public string File { get; } = file;

    /// <summary>Chemin du fichier tel qu'il s'affiche.</summary>
    public string FileLabel => LineExemption.DisplayFile(File);

    /// <summary>Valeur réelle concernée.</summary>
    public string Value => Marker.Value;

    /// <summary>Numéro de la ligne où la portion commence.</summary>
    public int Line => Marker.FirstLine;

    /// <summary>Nature de la valeur.</summary>
    public SpanCategory Category => Marker.Category;

    /// <summary>Étiquette courte de la famille de la valeur.</summary>
    public string Label => Marker.Category.FamilyCode();

    /// <summary>Vrai si les boutons « Garder ici » et « Partout » d'une valeur masquée s'appliquent.</summary>
    public bool ShowsMaskedActions => Marker.State == PreviewMarkerState.Masked;

    /// <summary>Vrai si les boutons d'une valeur signalée s'appliquent : « Masquer », « Garder ici », « Partout ».</summary>
    public bool ShowsProposal => Marker.State == PreviewMarkerState.Suspect;

    /// <summary>Vrai si une pastille rappelle une décision prise, avec de quoi l'annuler.</summary>
    public bool ShowsDecision => Marker.State is PreviewMarkerState.PendingMask
        or PreviewMarkerState.Exempted
        or PreviewMarkerState.Whitelisted;

    /// <summary>Vrai si la décision prise est de masquer.</summary>
    public bool IsPendingMask => Marker.State == PreviewMarkerState.PendingMask;

    /// <summary>Libellé de la pastille de décision.</summary>
    public string DecisionLabel => Marker.State switch
    {
        PreviewMarkerState.PendingMask => "à masquer",
        PreviewMarkerState.Exempted => Marker.IsProposal ? "gardé ici" : $"{Label} · gardé ici",
        PreviewMarkerState.Whitelisted => Marker.IsProposal ? "liste blanche" : $"{Label} · liste blanche",
        _ => string.Empty,
    };

    /// <summary>Explication affichée au survol.</summary>
    public string Tooltip => PreviewTexts.Describe(Marker);

    /// <summary>
    /// Réaligne l'affichage sur l'état de la portion, après une décision.
    /// </summary>
    public void NotifyStateChanged()
    {
        NotifyPropertyChanged(nameof(ShowsMaskedActions));
        NotifyPropertyChanged(nameof(ShowsProposal));
        NotifyPropertyChanged(nameof(ShowsDecision));
        NotifyPropertyChanged(nameof(IsPendingMask));
        NotifyPropertyChanged(nameof(DecisionLabel));
        NotifyPropertyChanged(nameof(Tooltip));
    }
}

/// <summary>
/// Ligne de l'aperçu côte à côte : son numéro, le texte d'origine, le texte
/// pseudonymisé, et les décisions qu'elle propose.
///
/// Les deux volets vivent dans la même ligne plutôt que dans deux vues
/// synchronisées : il n'y a qu'un ascenseur, donc aucune dérive possible.
/// Leurs fragments sont composés à la demande, depuis l'état des repères :
/// seules les lignes réellement affichées en paient le prix, et une décision
/// se reflète en recomposant les seules lignes concernées.
///
/// La sélection de texte suit le même principe. Elle n'est pas confiée au
/// contrôle — une liste virtualisée ne garde que les lignes à l'écran, et une
/// sélection qui la traverse ne pourrait pas y vivre —, mais portée par chaque
/// ligne sous la forme d'un intervalle de caractères, dont la composition des
/// fragments tient compte.
/// </summary>
public sealed class PreviewLineRow : INotifyPropertyChanged
{
    /// <summary>Fin d'intervalle signifiant « jusqu'au bout de la ligne, saut de ligne compris ».</summary>
    public const int ThroughLineEnd = int.MaxValue;

    private IReadOnlyList<PreviewSegment>? _originalBase;
    private IReadOnlyList<PreviewSegment>? _maskedBase;
    private IReadOnlyList<PreviewSegment>? _original;
    private IReadOnlyList<PreviewSegment>? _masked;
    private (int Start, int End)? _originalSelection;
    private (int Start, int End)? _maskedSelection;

    /// <summary>
    /// Construit une ligne.
    /// </summary>
    /// <param name="number">Numéro de ligne, à partir de 1</param>
    /// <param name="numberText">Numéro aligné pour la gouttière</param>
    /// <param name="gutterWidth">Largeur de la gouttière, commune au fichier</param>
    /// <param name="parts">Fragments de la ligne, dans l'ordre</param>
    /// <param name="entries">Décisions proposées sur cette ligne</param>
    public PreviewLineRow(
        int number,
        string numberText,
        double gutterWidth,
        IReadOnlyList<PreviewPart> parts,
        IReadOnlyList<PreviewEntryRow> entries)
    {
        Number = number;
        NumberText = numberText;
        GutterWidth = gutterWidth;
        Parts = parts;
        Entries = entries;
        IsRelevant = parts.Any(part => part.Marker is not null);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Numéro de ligne, à partir de 1.</summary>
    public int Number { get; }

    /// <summary>Numéro aligné à droite.</summary>
    public string NumberText { get; }

    /// <summary>Largeur de la gouttière, identique pour toutes les lignes du fichier.</summary>
    public double GutterWidth { get; }

    /// <summary>Fragments de la ligne.</summary>
    public IReadOnlyList<PreviewPart> Parts { get; }

    /// <summary>Décisions proposées sur cette ligne.</summary>
    public IReadOnlyList<PreviewEntryRow> Entries { get; }

    /// <summary>Vrai si la ligne porte au moins une portion repérée.</summary>
    public bool IsRelevant { get; }

    /// <summary>
    /// Opacité de la ligne : estompée quand un filtre est choisi et qu'elle
    /// ne porte rien de ce qu'il met en avant.
    /// </summary>
    public double Emphasis { get; private set; } = 1.0;

    /// <summary>
    /// Indique si la ligne porte une portion que le filtre met en avant.
    /// </summary>
    /// <param name="filter">Filtre de l'aperçu</param>
    /// <returns><c>true</c> si la ligne est utile sous ce filtre</returns>
    public bool Matches(PreviewFilter filter)
    {
        PreviewMarkerState? wanted = filter switch
        {
            PreviewFilter.Masked => PreviewMarkerState.Masked,
            PreviewFilter.Suspect => PreviewMarkerState.Suspect,
            PreviewFilter.PendingMask => PreviewMarkerState.PendingMask,
            _ => null,
        };

        return wanted is null
            ? IsRelevant
            : Parts.Any(part => part.Marker?.State == wanted);
    }

    /// <summary>
    /// Estompe la ligne, ou lui rend son éclat.
    /// </summary>
    /// <param name="dimmed">Vrai pour l'estomper</param>
    public void SetDimmed(bool dimmed)
    {
        double emphasis = dimmed ? 0.38 : 1.0;
        if (Math.Abs(Emphasis - emphasis) < 0.001)
        {
            return;
        }

        Emphasis = emphasis;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Emphasis)));
    }

    /// <summary>Fragments du texte d'origine, sélection comprise.</summary>
    public IReadOnlyList<PreviewSegment> Original => _original ??= ApplySelection(BaseOf(PreviewPane.Original), _originalSelection);

    /// <summary>Fragments du texte pseudonymisé, sélection comprise.</summary>
    public IReadOnlyList<PreviewSegment> Masked => _masked ??= ApplySelection(BaseOf(PreviewPane.Masked), _maskedSelection);

    /// <summary>
    /// Donne le texte affiché par l'un des volets, celui qu'on copie.
    /// </summary>
    /// <param name="pane">Volet concerné</param>
    /// <returns>Le texte de la ligne dans ce volet, sans saut de ligne</returns>
    public string TextOf(PreviewPane pane)
    {
        return string.Concat(BaseOf(pane).Select(segment => segment.Text));
    }

    /// <summary>
    /// Donne les fragments d'un volet, sans la sélection.
    /// </summary>
    /// <param name="pane">Volet concerné</param>
    /// <returns>Les fragments colorés</returns>
    public IReadOnlyList<PreviewSegment> BaseOf(PreviewPane pane)
    {
        return pane == PreviewPane.Original
            ? _originalBase ??= Compose(pseudonymized: false)
            : _maskedBase ??= Compose(pseudonymized: true);
    }

    /// <summary>
    /// Fixe la portion sélectionnée de l'un des volets.
    /// </summary>
    /// <param name="pane">Volet concerné</param>
    /// <param name="range">Intervalle de caractères sélectionné, ou <c>null</c> pour aucun ; une fin à <see cref="ThroughLineEnd"/> comprend le saut de ligne</param>
    public void SetSelection(PreviewPane pane, (int Start, int End)? range)
    {
        if (pane == PreviewPane.Original)
        {
            if (_originalSelection == range)
            {
                return;
            }

            _originalSelection = range;
            _original = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Original)));
            return;
        }

        if (_maskedSelection == range)
        {
            return;
        }

        _maskedSelection = range;
        _masked = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Masked)));
    }

    /// <summary>
    /// Recompose la ligne après un changement d'état de l'un de ses repères.
    /// </summary>
    public void Refresh()
    {
        _originalBase = null;
        _maskedBase = null;
        _original = null;
        _masked = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Original)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Masked)));

        foreach (PreviewEntryRow entry in Entries)
        {
            entry.NotifyStateChanged();
        }
    }

    /// <summary>
    /// Découpe les fragments d'un volet aux bornes de la sélection, et marque
    /// ceux qui en font partie. Une sélection qui se poursuit sur la ligne
    /// suivante se voit au-delà du dernier caractère, y compris sur une ligne
    /// vide, par un blanc sélectionné.
    /// </summary>
    /// <param name="segments">Fragments sans sélection</param>
    /// <param name="range">Intervalle sélectionné, ou <c>null</c></param>
    /// <returns>Les fragments à afficher</returns>
    private static IReadOnlyList<PreviewSegment> ApplySelection(IReadOnlyList<PreviewSegment> segments, (int Start, int End)? range)
    {
        if (range is not (int start, int end) || end <= start)
        {
            return segments;
        }

        List<PreviewSegment> result = new(segments.Count + 2);
        int position = 0;

        foreach (PreviewSegment segment in segments)
        {
            int segmentStart = position;
            int segmentEnd = position + segment.Text.Length;
            position = segmentEnd;

            int from = Math.Clamp(start, segmentStart, segmentEnd);
            int to = Math.Clamp(end, segmentStart, segmentEnd);

            if (to <= from)
            {
                result.Add(segment);
                continue;
            }

            if (from > segmentStart)
            {
                result.Add(segment with { Text = segment.Text[..(from - segmentStart)] });
            }

            result.Add(segment with { Text = segment.Text[(from - segmentStart)..(to - segmentStart)], Selected = true });

            if (to < segmentEnd)
            {
                result.Add(segment with { Text = segment.Text[(to - segmentStart)..] });
            }
        }

        if (end == ThroughLineEnd)
        {
            result.Add(new PreviewSegment(" ", PreviewSegmentKind.Plain) { Selected = true });
        }

        return result;
    }

    /// <summary>
    /// Compose les fragments de l'un des deux volets.
    /// </summary>
    /// <param name="pseudonymized">Vrai pour le volet de droite</param>
    /// <returns>Les fragments colorés</returns>
    private List<PreviewSegment> Compose(bool pseudonymized)
    {
        List<PreviewSegment> segments = new(Parts.Count);

        foreach (PreviewPart part in Parts)
        {
            PreviewMarker? marker = part.Marker;
            if (marker is null)
            {
                segments.Add(new PreviewSegment(part.Text, PreviewSegmentKind.Plain));
                continue;
            }

            string tooltip = PreviewTexts.Describe(marker);
            segments.Add(marker.State switch
            {
                PreviewMarkerState.Masked when !pseudonymized =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Masked, tooltip),
                PreviewMarkerState.Masked when marker.Pseudonym.Length > 0 =>
                    new PreviewSegment(marker.PseudonymLine(Number), PreviewSegmentKind.Masked, tooltip),
                PreviewMarkerState.Masked =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Pending, tooltip),
                PreviewMarkerState.Exempted =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Exempted, tooltip),
                PreviewMarkerState.Suspect =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Suspect, tooltip),
                PreviewMarkerState.PendingMask =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Pending, tooltip),
                PreviewMarkerState.Whitelisted =>
                    new PreviewSegment(part.Text, PreviewSegmentKind.Allowed, tooltip),
                _ => new PreviewSegment(part.Text, PreviewSegmentKind.Plain),
            });
        }

        return segments;
    }
}

/// <summary>
/// En-tête d'un onglet d'aperçu : le nom du fichier et le nombre de valeurs
/// signalées qui y restent à trancher, affiché entre parenthèses.
///
/// C'est un objet de données, mis en forme par un gabarit, et non un élément
/// construit en code : seul un en-tête issu d'un gabarit reçoit la couleur et
/// la graisse que l'onglet donne à son titre selon qu'il est choisi ou non.
/// </summary>
/// <param name="path">Fichier dont l'onglet montre l'aperçu</param>
public sealed class PreviewTabHeader(string path) : NotifyingObject
{
    private int _suspects;

    /// <summary>Fichier dont l'onglet montre l'aperçu.</summary>
    public string Path { get; } = path;

    /// <summary>Nom du fichier.</summary>
    public string Name { get; } = System.IO.Path.GetFileName(path);

    /// <summary>Nombre de valeurs signalées qui restent à trancher.</summary>
    public int Suspects
    {
        get => _suspects;
        set
        {
            if (_suspects == value)
            {
                return;
            }

            _suspects = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(CountText));
        }
    }

    /// <summary>Compte affiché après le nom, vide quand il ne reste rien à trancher.</summary>
    public string CountText => Suspects == 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $" ({Suspects})");
}

/// <summary>
/// Intervalle de lignes replié dans l'aperçu.
///
/// Un journal de vingt mille lignes où seules quelques centaines portent une
/// valeur à masquer ne se relit pas en entier : l'aperçu montre les lignes
/// utiles et leur voisinage, et résume le reste en une ligne cliquable. La
/// numérotation reste celle du fichier, pour qu'un numéro lu ici se retrouve
/// dans l'original.
/// </summary>
/// <param name="firstLine">Première ligne repliée, à partir de 1</param>
/// <param name="lastLine">Dernière ligne repliée</param>
public sealed class PreviewGapRow(int firstLine, int lastLine)
{
    /// <summary>Première ligne repliée.</summary>
    public int FirstLine { get; } = firstLine;

    /// <summary>Dernière ligne repliée.</summary>
    public int LastLine { get; } = lastLine;

    /// <summary>Nombre de lignes repliées.</summary>
    public int Count => LastLine - FirstLine + 1;

    /// <summary>Libellé affiché à la place des lignes.</summary>
    public string Label => string.Create(
        CultureInfo.CurrentCulture,
        $"───  {Count} lignes cachées ({FirstLine}–{LastLine}) · cliquer pour les afficher  ───");
}

/// <summary>
/// Textes d'explication de l'aperçu, rédigés une fois pour les volets et les
/// décisions.
/// </summary>
internal static class PreviewTexts
{
    /// <summary>
    /// Explique l'état d'une portion repérée.
    /// </summary>
    /// <param name="marker">Portion concernée</param>
    /// <returns>Le texte de l'infobulle</returns>
    public static string Describe(PreviewMarker marker)
    {
        string label = CategoryLabels.Label(marker.Category);

        return marker.State switch
        {
            PreviewMarkerState.Masked when marker.Pseudonym.Length > 0 => string.Create(
                CultureInfo.CurrentCulture,
                $"{label}\n{Shorten(marker.Value)}  →  {Shorten(marker.Pseudonym)}\n\n« Garder ici » laisse cette occurrence-ci en clair, les autres restent masquées ; « Partout » ajoute la valeur à la liste blanche du projet."),
            PreviewMarkerState.Masked => string.Create(
                CultureInfo.CurrentCulture,
                $"{label}\nSera masqué ; le pseudonyme apparaîtra à la prochaine analyse."),
            PreviewMarkerState.Exempted =>
                "Gardé en clair à votre demande, à cet endroit seulement : les autres occurrences de la valeur ne sont pas concernées.\n« annuler » revient sur cette décision.",
            PreviewMarkerState.Suspect => string.Create(
                CultureInfo.CurrentCulture,
                $"Signalé, non masqué · {marker.FindingKind}\n{marker.Advice}\n\n« Masquer » ajoute cette valeur aux règles du projet ; « Garder ici » la laisse en clair à cet endroit ; « Partout » l'ajoute à la liste blanche : le moteur cessera de la signaler."),
            PreviewMarkerState.PendingMask =>
                "Retenu pour être masqué : la valeur a rejoint les règles du projet.\nSon pseudonyme apparaîtra à la prochaine analyse.",
            _ => "Liste blanche du projet : cette valeur reste en clair partout, dans tous les fichiers.\n« annuler », ou l'onglet « Liste blanche », l'en retire.",
        };
    }

    /// <summary>
    /// Raccourcit une valeur longue pour l'infobulle, un bloc de clé pouvant
    /// compter des dizaines de lignes.
    /// </summary>
    /// <param name="value">Valeur à afficher</param>
    /// <returns>La valeur, tronquée au besoin</returns>
    private static string Shorten(string value)
    {
        const int Limit = 90;
        string singleLine = value.ReplaceLineEndings(" ⏎ ");
        return singleLine.Length <= Limit ? singleLine : string.Concat(singleLine.AsSpan(0, Limit), "…");
    }
}

/// <summary>
/// Choisit le gabarit d'un élément de l'aperçu : ligne de texte ou intervalle
/// replié.
/// </summary>
public sealed class PreviewItemSelector : DataTemplateSelector
{
    /// <summary>Gabarit d'une ligne de texte.</summary>
    public DataTemplate? LineTemplate { get; set; }

    /// <summary>Gabarit d'un intervalle replié.</summary>
    public DataTemplate? GapTemplate { get; set; }

    /// <inheritdoc />
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item is PreviewGapRow ? GapTemplate : LineTemplate;
    }
}

/// <summary>
/// Propriété attachée qui peuple les fragments d'un <see cref="TextBlock"/>.
///
/// Un gabarit XAML ne sait pas construire une suite de <c>Run</c> colorés à
/// partir d'une liaison : <c>TextBlock.Inlines</c> n'est pas une propriété de
/// dépendance. Sans ce relais, il faudrait composer chaque ligne en code et
/// renoncer à la virtualisation de la liste, donc à l'affichage fluide des gros
/// fichiers.
/// </summary>
public static class InlineBinder
{
    /// <summary>Fragments à afficher dans le bloc de texte.</summary>
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(IReadOnlyList<PreviewSegment>),
        typeof(InlineBinder),
        new PropertyMetadata(null, OnSegmentsChanged));

    /// <summary>
    /// Lit les fragments associés à un élément.
    /// </summary>
    /// <param name="element">Élément porteur</param>
    /// <returns>Les fragments, ou <c>null</c></returns>
    public static IReadOnlyList<PreviewSegment>? GetSegments(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IReadOnlyList<PreviewSegment>?)element.GetValue(SegmentsProperty);
    }

    /// <summary>
    /// Associe des fragments à un élément.
    /// </summary>
    /// <param name="element">Élément porteur</param>
    /// <param name="value">Fragments à afficher</param>
    public static void SetSegments(DependencyObject element, IReadOnlyList<PreviewSegment>? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SegmentsProperty, value);
    }

    /// <summary>
    /// Reconstruit le contenu du bloc de texte lorsque ses fragments changent.
    /// </summary>
    /// <param name="element">Bloc de texte concerné</param>
    /// <param name="e">Ancienne et nouvelle valeur</param>
    private static void OnSegmentsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<PreviewSegment> segments)
        {
            return;
        }

        foreach (PreviewSegment segment in segments)
        {
            Run run = new(segment.Text);

            switch (segment.Kind)
            {
                case PreviewSegmentKind.Masked:
                    run.Background = Brush(block, "MaskedBackground");
                    run.Foreground = Brush(block, "MaskedForeground");
                    break;

                case PreviewSegmentKind.Suspect:
                    run.Background = Brush(block, "SuspectBackground");
                    run.Foreground = Brush(block, "SuspectForeground");
                    break;

                case PreviewSegmentKind.Pending:
                    // Vert éteint et souligné en tirets : la décision est
                    // prise, mais le pseudonyme n'est pas encore attribué.
                    run.Background = Brush(block, "PendingBackground");
                    run.Foreground = Brush(block, "PendingForeground");
                    run.TextDecorations = Underline(block, "Accent", DashStyles.Dash);
                    break;

                case PreviewSegmentKind.Exempted:
                    // Barré et discret : la valeur est bien là, et c'est une
                    // décision, pas un oubli.
                    run.Foreground = Brush(block, "TextMuted");
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;

                case PreviewSegmentKind.Allowed:
                    // Souligné de points : en clair partout, par la liste
                    // blanche du projet.
                    run.Foreground = Brush(block, "TextSecondary");
                    run.TextDecorations = Underline(block, "TextMuted", DashStyles.Dot);
                    break;

                default:
                    break;
            }

            // La sélection se lit par-dessus la nature du fragment : le fond
            // change, la décoration — barré, souligné — reste.
            if (segment.Selected)
            {
                run.Background = Brush(block, "SelectionBackground");
                run.Foreground = Brush(block, "TextPrimary");
            }

            if (segment.Tooltip is not null)
            {
                run.ToolTip = segment.Tooltip;
            }

            block.Inlines.Add(run);
        }
    }

    /// <summary>
    /// Compose un soulignement discontinu.
    /// </summary>
    /// <param name="element">Élément servant à résoudre la couleur du thème</param>
    /// <param name="brushKey">Clé de la couleur du trait</param>
    /// <param name="dashes">Motif du trait</param>
    /// <returns>La décoration à appliquer</returns>
    private static TextDecorationCollection Underline(FrameworkElement element, string brushKey, DashStyle dashes)
    {
        Pen pen = new(Brush(element, brushKey), 1) { DashStyle = dashes };
        pen.Freeze();

        TextDecorationCollection decorations = [new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel)];
        decorations.Freeze();
        return decorations;
    }

    /// <summary>
    /// Résout un pinceau du thème depuis l'élément affiché.
    /// </summary>
    /// <param name="element">Élément servant de point de départ</param>
    /// <param name="key">Clé de la ressource</param>
    /// <returns>Le pinceau, ou un pinceau transparent si la clé est absente</returns>
    private static Brush Brush(FrameworkElement element, string key)
    {
        return element.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }
}

/// <summary>
/// Emplacements supplémentaires du panneau d'onglets de droite : un bandeau
/// sous leur contenu, au-dessus du pied de la fenêtre, et une couche posée
/// par-dessus ce contenu.
///
/// Ils servent à annoncer l'état de l'analyse — en cours, nécessaire — au même
/// endroit quel que soit l'onglet, sans dupliquer ces annonces dans chacun.
/// </summary>
public static class DetailTabSlots
{
    /// <summary>Bandeau affiché entre les onglets et leur contenu.</summary>
    public static readonly DependencyProperty BannerProperty = DependencyProperty.RegisterAttached(
        "Banner",
        typeof(UIElement),
        typeof(DetailTabSlots),
        new PropertyMetadata(null));

    /// <summary>Couche affichée par-dessus le contenu de l'onglet.</summary>
    public static readonly DependencyProperty OverlayProperty = DependencyProperty.RegisterAttached(
        "Overlay",
        typeof(UIElement),
        typeof(DetailTabSlots),
        new PropertyMetadata(null));

    /// <summary>
    /// Lit le bandeau d'un panneau.
    /// </summary>
    /// <param name="element">Panneau porteur</param>
    /// <returns>Le bandeau, ou <c>null</c></returns>
    public static UIElement? GetBanner(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (UIElement?)element.GetValue(BannerProperty);
    }

    /// <summary>
    /// Associe un bandeau à un panneau.
    /// </summary>
    /// <param name="element">Panneau porteur</param>
    /// <param name="value">Bandeau à afficher</param>
    public static void SetBanner(DependencyObject element, UIElement? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BannerProperty, value);
    }

    /// <summary>
    /// Lit la couche d'un panneau.
    /// </summary>
    /// <param name="element">Panneau porteur</param>
    /// <returns>La couche, ou <c>null</c></returns>
    public static UIElement? GetOverlay(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (UIElement?)element.GetValue(OverlayProperty);
    }

    /// <summary>
    /// Associe une couche à un panneau.
    /// </summary>
    /// <param name="element">Panneau porteur</param>
    /// <param name="value">Couche à afficher</param>
    public static void SetOverlay(DependencyObject element, UIElement? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(OverlayProperty, value);
    }
}
