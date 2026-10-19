namespace TebiObfuscator.Core.Model.Engine;

/// <summary>
/// Index des débuts de ligne d'un texte, pour convertir une position en numéro
/// de ligne sans reparcourir le texte à chaque appel.
///
/// L'aller-retour entre offsets et numéros de ligne intervient dès qu'une
/// décision est prise à l'écran : une exception posée sur une ligne doit se
/// retrouver côté moteur, qui ne raisonne qu'en positions absolues.
/// </summary>
public sealed class TextLines
{
    private readonly int[] _starts;

    /// <summary>
    /// Construit l'index des lignes d'un texte.
    /// </summary>
    /// <param name="text">Texte à indexer</param>
    public TextLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<int> starts = [0];
        for (int position = 0; position < text.Length; position++)
        {
            if (text[position] == '\n')
            {
                starts.Add(position + 1);
            }
        }

        _starts = [.. starts];
    }

    /// <summary>Nombre de lignes du texte.</summary>
    public int Count => _starts.Length;

    /// <summary>
    /// Retrouve le numéro de ligne d'une position.
    /// </summary>
    /// <param name="position">Position dans le texte</param>
    /// <returns>Le numéro de ligne, à partir de 1</returns>
    public int LineOf(int position)
    {
        int index = Array.BinarySearch(_starts, position);
        return index >= 0 ? index + 1 : ~index;
    }
}
