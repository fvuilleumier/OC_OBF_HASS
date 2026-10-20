using System.Globalization;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Message d'état tel qu'il a été affiché.
/// </summary>
/// <param name="Time">Moment de l'affichage</param>
/// <param name="Message">Texte du message</param>
/// <param name="Success">Vrai pour un succès, faux pour un échec, <c>null</c> pour une information</param>
public sealed record StatusEntry(DateTimeOffset Time, string Message, bool? Success);

/// <summary>
/// Historique des messages d'état, que la console de la fenêtre présente en
/// entier.
///
/// Le bandeau d'état n'en montre que le dernier, borné à quelques lignes : un
/// compte rendu d'analyse ou une erreur détaillée peuvent être bien plus longs,
/// et un message chasse le précédent avant qu'on ait pu le lire. Le journal
/// garde les uns et les autres, dans la limite d'une capacité au-delà de
/// laquelle les plus anciens sont oubliés — il vit en mémoire, le temps de la
/// séance, et n'est jamais écrit sur disque : un message peut citer une valeur
/// réelle.
/// </summary>
public sealed class StatusJournal
{
    /// <summary>Nombre de messages conservés par défaut.</summary>
    public const int DefaultCapacity = 500;

    private readonly List<StatusEntry> _entries = [];
    private readonly int _capacity;

    /// <summary>
    /// Construit un journal vide.
    /// </summary>
    /// <param name="capacity">Nombre de messages conservés, au moins 1</param>
    public StatusJournal(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Messages conservés, du plus ancien au plus récent.</summary>
    public IReadOnlyList<StatusEntry> Entries => _entries;

    /// <summary>
    /// Ajoute un message. Le même message répété aussitôt n'est pas recopié :
    /// la console doit raconter ce qui s'est passé, pas chaque rafraîchissement.
    /// </summary>
    /// <param name="message">Texte du message</param>
    /// <param name="success">Vrai pour un succès, faux pour un échec, <c>null</c> pour une information</param>
    /// <param name="time">Moment de l'affichage</param>
    /// <returns>Le message ajouté, ou <c>null</c> s'il répétait le précédent</returns>
    public StatusEntry? Add(string message, bool? success, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_entries.Count > 0 && _entries[^1] is { } last && last.Success == success
            && string.Equals(last.Message, message, StringComparison.Ordinal))
        {
            return null;
        }

        StatusEntry entry = new(time, message, success);
        _entries.Add(entry);

        // Les plus anciens cèdent la place : une longue séance ne doit pas
        // faire grossir la mémoire sans fin.
        if (_entries.Count > _capacity)
        {
            _entries.RemoveRange(0, _entries.Count - _capacity);
        }

        return entry;
    }

    /// <summary>Oublie tous les messages.</summary>
    public void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// Met un message en forme pour la console : l'heure, la nature s'il en a
    /// une, puis le texte.
    /// </summary>
    /// <param name="entry">Message à mettre en forme</param>
    /// <returns>La ligne de console, par exemple <c>[14:03:22] ÉCHEC · Copie refusée…</c></returns>
    public static string Format(StatusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string nature = entry.Success switch
        {
            true => "OK · ",
            false => "ÉCHEC · ",
            null => string.Empty,
        };

        return string.Create(CultureInfo.InvariantCulture, $"[{entry.Time:HH:mm:ss}] {nature}{entry.Message}");
    }

    /// <summary>
    /// Met tout le journal en forme, un message par ligne.
    /// </summary>
    /// <returns>Le texte complet de la console</returns>
    public string FormatAll()
    {
        return string.Join(Environment.NewLine, _entries.Select(Format));
    }
}
