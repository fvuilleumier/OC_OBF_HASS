using System.Text;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Position dans un volet de l'aperçu : un numéro de ligne et le rang d'un
/// caractère dans le texte que ce volet affiche pour la ligne.
/// </summary>
/// <param name="Line">Numéro de ligne, à partir de 1</param>
/// <param name="Offset">Rang du caractère dans la ligne, à partir de 0</param>
internal readonly record struct PanePosition(int Line, int Offset) : IComparable<PanePosition>
{
    /// <inheritdoc />
    public int CompareTo(PanePosition other)
    {
        return Line != other.Line ? Line.CompareTo(other.Line) : Offset.CompareTo(other.Offset);
    }

    /// <summary>Vrai si la position gauche précède la droite.</summary>
    public static bool operator <(PanePosition left, PanePosition right) => left.CompareTo(right) < 0;

    /// <summary>Vrai si la position gauche suit la droite.</summary>
    public static bool operator >(PanePosition left, PanePosition right) => left.CompareTo(right) > 0;

    /// <summary>Vrai si la position gauche précède la droite ou la vaut.</summary>
    public static bool operator <=(PanePosition left, PanePosition right) => left.CompareTo(right) <= 0;

    /// <summary>Vrai si la position gauche suit la droite ou la vaut.</summary>
    public static bool operator >=(PanePosition left, PanePosition right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Texte sélectionné dans l'un des volets d'un aperçu, d'une position à une
/// autre.
///
/// La sélection est tenue en numéros de ligne et en rangs de caractère, et non
/// par les éléments affichés : l'aperçu est une liste virtualisée, dont seules
/// les lignes à l'écran existent, et une sélection de cent lignes doit
/// survivre au défilement. Les lignes repliées entre ses bornes en font partie
/// — c'est le texte du fichier qu'on copie, comme un éditeur copie le contenu
/// d'une région repliée.
/// </summary>
/// <param name="file">Aperçu du fichier</param>
/// <param name="pane">Volet où la sélection a commencé</param>
/// <param name="anchor">Point de départ de la sélection</param>
internal sealed class PreviewSelection(FilePreviewModel file, PreviewPane pane, PanePosition anchor)
{
    /// <summary>Aperçu du fichier.</summary>
    public FilePreviewModel File { get; } = file;

    /// <summary>Volet sélectionné.</summary>
    public PreviewPane Pane { get; } = pane;

    /// <summary>Point de départ, fixe pendant que la sélection s'étend.</summary>
    public PanePosition Anchor { get; set; } = anchor;

    /// <summary>Point d'arrivée, qui suit la souris ou le clavier.</summary>
    public PanePosition Active { get; set; } = anchor;

    /// <summary>Début de la sélection, quel que soit le sens où elle a été faite.</summary>
    public PanePosition Start => Anchor <= Active ? Anchor : Active;

    /// <summary>Fin de la sélection, exclue.</summary>
    public PanePosition End => Anchor <= Active ? Active : Anchor;

    /// <summary>Vrai si rien n'est sélectionné.</summary>
    public bool IsEmpty => Anchor == Active;

    /// <summary>Vrai si la sélection tient sur une seule ligne.</summary>
    public bool IsSingleLine => Start.Line == End.Line;

    /// <summary>
    /// Donne la portion sélectionnée d'une ligne.
    /// </summary>
    /// <param name="line">Numéro de ligne</param>
    /// <returns>L'intervalle de caractères sélectionné, ou <c>null</c> si la ligne n'en a pas</returns>
    public (int Start, int End)? RangeOn(int line)
    {
        if (IsEmpty || line < Start.Line || line > End.Line)
        {
            return null;
        }

        int from = line == Start.Line ? Start.Offset : 0;
        int to = line == End.Line ? End.Offset : PreviewLineRow.ThroughLineEnd;
        return to > from ? (from, to) : null;
    }

    /// <summary>
    /// Indique si une position tombe dans la sélection.
    /// </summary>
    /// <param name="position">Position examinée</param>
    /// <returns><c>true</c> si la position est entre les bornes</returns>
    public bool Contains(PanePosition position)
    {
        return !IsEmpty && position >= Start && position <= End;
    }

    /// <summary>
    /// Compose le texte sélectionné, tel que le volet l'affiche, lignes
    /// séparées par un saut de ligne Windows.
    /// </summary>
    /// <returns>Le texte sélectionné</returns>
    public string Text()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        for (int line = Start.Line; line <= End.Line && line <= File.Rows.Length; line++)
        {
            string content = File.Rows[line - 1].TextOf(Pane);
            int from = line == Start.Line ? Math.Clamp(Start.Offset, 0, content.Length) : 0;
            int to = line == End.Line ? Math.Clamp(End.Offset, from, content.Length) : content.Length;

            text.Append(content, from, to - from);
            if (line < End.Line)
            {
                text.Append("\r\n");
            }
        }

        return text.ToString();
    }
}
