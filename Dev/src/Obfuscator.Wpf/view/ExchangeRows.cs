using System.Globalization;
using System.Windows;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>Teinte de la pastille d'un fichier dans la liste d'un échange.</summary>
public enum ExchangeTone
{
    /// <summary>Fichier ajouté.</summary>
    Add,

    /// <summary>Fichier modifié ou régénéré.</summary>
    Change,

    /// <summary>Fichier retiré.</summary>
    Remove,

    /// <summary>Fichier bloqué : écrit seulement sur demande.</summary>
    Block,

    /// <summary>Fichier qui ne peut pas être écrit.</summary>
    Refused,
}

/// <summary>
/// Alerte d'un fichier, telle que la liste l'affiche.
/// </summary>
/// <param name="Text">Texte, ligne comprise</param>
/// <param name="IsSerious">Vrai pour une alerte à traiter avant de commiter</param>
public sealed record ExchangeWarningLine(string Text, bool IsSerious);

/// <summary>
/// Fichier d'un échange, tel que la liste à confirmer l'affiche : sa case, ce
/// que l'échange en fait, où il est écrit, et ses alertes.
/// </summary>
public sealed class ExchangeRowModel : NotifyingObject
{
    private bool _isChecked;
    private bool _ignoreLater;

    /// <summary>
    /// Décrit un fichier d'un échange.
    /// </summary>
    /// <param name="change">Fichier du plan</param>
    /// <param name="isReturn">Vrai pour un rapatriement, faux pour une obfuscation</param>
    public ExchangeRowModel(ExchangeChange change, bool isReturn)
    {
        ArgumentNullException.ThrowIfNull(change);

        _isReturn = isReturn;
        _isChecked = change.Checked;
        CanIgnore = isReturn && change.Action == ExchangeAction.Created && change.Optional;
        Load(change);
    }

    private readonly bool _isReturn;

    /// <summary>
    /// Reprend le fichier après la résolution de ses conflits : comptes et
    /// alertes changent, la case reste telle que l'utilisateur l'a laissée.
    /// </summary>
    /// <param name="change">Fichier mis à jour</param>
    public void Update(ExchangeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Load(change);
        NotifyPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Compose ce que la ligne affiche d'un fichier.
    /// </summary>
    /// <param name="change">Fichier du plan</param>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Change), nameof(ActionLabel), nameof(Path), nameof(OtherPath), nameof(Stats), nameof(Warnings))]
    private void Load(ExchangeChange change)
    {
        bool isReturn = _isReturn;
        Change = change;

        // Le chemin affiché d'abord est celui du fichier écrit : la cible pour
        // une obfuscation, la source pour un rapatriement.
        string written = isReturn ? change.SourcePath : change.TargetPath;
        string other = isReturn ? change.TargetPath : change.SourcePath;
        string origin = isReturn ? "depuis le dossier de l'IA : " : "depuis la source : ";
        Path = written.Length > 0 ? written : other;
        OtherPath = other.Length > 0 && !string.Equals(other, written, StringComparison.Ordinal)
            ? origin + other
            : string.Empty;

        (ActionLabel, Tone) = change.Action switch
        {
            ExchangeAction.Create => ("créé", ExchangeTone.Add),
            ExchangeAction.Update => ("modifié", ExchangeTone.Change),
            ExchangeAction.Identical => ("identique", ExchangeTone.Change),
            ExchangeAction.Delete => ("retiré", ExchangeTone.Remove),
            ExchangeAction.Blocked => ("bloqué", ExchangeTone.Block),
            ExchangeAction.Legacy => ("ancien produit", ExchangeTone.Remove),
            ExchangeAction.Generated => ("généré", ExchangeTone.Change),
            ExchangeAction.Refused => ("refusé", ExchangeTone.Refused),
            ExchangeAction.Modified => ("modifié par l'IA", ExchangeTone.Change),
            ExchangeAction.Created => ("créé par l'IA", ExchangeTone.Add),
            _ => ("supprimé par l'IA", ExchangeTone.Remove),
        };

        List<string> stats = [];
        if (change.Added > 0 || change.Removed > 0)
        {
            stats.Add(string.Create(CultureInfo.CurrentCulture, $"+{change.Added} −{change.Removed}"));
        }

        if (change.Conflicts > 0)
        {
            stats.Add(string.Create(CultureInfo.CurrentCulture, $"{change.Conflicts} conflit(s)"));
        }

        Stats = string.Join(" · ", stats);
        Warnings = [.. change.Warnings.Select(warning => new ExchangeWarningLine(
            warning.Line > 0 ? string.Create(CultureInfo.CurrentCulture, $"ligne {warning.Line} · {warning.Text}") : warning.Text,
            warning.IsSerious))];
    }

    /// <summary>Fichier du plan.</summary>
    public ExchangeChange Change { get; private set; }

    /// <summary>
    /// Vrai si le fichier a des conflits que l'on peut trancher passage par
    /// passage, ou dont on peut revoir les choix.
    /// </summary>
    public bool CanResolve { get; init; }

    /// <summary>Identifiant du fichier dans le plan.</summary>
    public int Id => Change.Id;

    /// <summary>Vrai si le fichier sera écrit ou retiré.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value && CanToggle)
            {
                _isChecked = value;
                NotifyPropertyChanged();
            }
        }
    }

    /// <summary>Vrai si l'utilisateur peut cocher ou décocher le fichier.</summary>
    public bool CanToggle => Change.Optional && !_ignoreLater;

    /// <summary>
    /// Vrai pour un fichier créé dans la cible que l'utilisateur peut faire
    /// ignorer une fois pour toutes : une note, une consigne, qui est à lui et
    /// n'a pas à rejoindre la source.
    /// </summary>
    public bool CanIgnore { get; }

    /// <summary>
    /// Vrai si le fichier doit être ignoré par ce rapatriement et les
    /// suivants : il n'est pas écrit.
    /// </summary>
    public bool IgnoreLater
    {
        get => _ignoreLater;
        set
        {
            if (_ignoreLater == value || !CanIgnore)
            {
                return;
            }

            _ignoreLater = value;
            if (value)
            {
                _isChecked = false;
                NotifyPropertyChanged(nameof(IsChecked));
            }

            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(CanToggle));
        }
    }

    /// <summary>Ce que l'échange fait du fichier, en un mot.</summary>
    public string ActionLabel { get; private set; }

    /// <summary>Teinte de la pastille.</summary>
    public ExchangeTone Tone { get; private set; }

    /// <summary>Chemin du fichier écrit, avec des barres obliques.</summary>
    public string Path { get; private set; }

    /// <summary>Chemin de l'autre côté, s'il diffère.</summary>
    public string OtherPath { get; private set; }

    /// <summary>Vrai s'il y a un chemin de l'autre côté à afficher.</summary>
    public bool HasOtherPath => OtherPath.Length > 0;

    /// <summary>Explication courte de l'état du fichier.</summary>
    public string Note => Change.Note ?? string.Empty;

    /// <summary>Vrai s'il y a une explication.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>Lignes ajoutées et retirées, conflits.</summary>
    public string Stats { get; private set; }

    /// <summary>Alertes du fichier.</summary>
    public IReadOnlyList<ExchangeWarningLine> Warnings { get; private set; }
}

/// <summary>
/// Contenu de la liste à confirmer d'un échange.
/// </summary>
/// <param name="Heading">Titre : ce qui va être écrit, et où</param>
/// <param name="Folder">Chemin du dossier écrit</param>
/// <param name="Summary">Comptes de l'échange</param>
/// <param name="Blocking">Ce qui interdit l'écriture</param>
/// <param name="Warnings">Ce qui mérite d'être su avant d'écrire</param>
/// <param name="Rows">Fichiers affichés</param>
/// <param name="ConfirmLabel">Libellé du bouton d'écriture</param>
/// <param name="CanConfirm">Vrai si l'écriture est possible</param>
/// <param name="Footer">Rappel de ce que fait l'écriture, et de ce qui suit</param>
public sealed record ExchangeDialogContent(
    string Heading,
    string Folder,
    string Summary,
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ExchangeRowModel> Rows,
    string ConfirmLabel,
    bool CanConfirm,
    string Footer)
{
    /// <summary>Ce que la liste dit quand elle n'a aucun fichier à montrer.</summary>
    public string EmptyMessage { get; init; } = "Aucun fichier à écrire.";

    /// <summary>
    /// Fait trancher les conflits d'un fichier, depuis la liste ouverte : rend
    /// le fichier mis à jour, ou <c>null</c> si l'on a renoncé.
    /// </summary>
    public Func<Window, ExchangeRowModel, ExchangeChange?>? Resolve { get; init; }

    /// <summary>Recompose les comptes de l'échange après une résolution.</summary>
    public Func<string>? Summarize { get; init; }

    /// <summary>
    /// Vrai pour ouvrir d'emblée la résolution du premier fichier en conflit :
    /// le rapatriement demandé par une autre application, qui n'a pas pu
    /// l'écrire seule, arrive ainsi directement sur ce qu'il faut trancher.
    /// </summary>
    public bool ResolveFirstOnOpen { get; init; }
}

/// <summary>
/// Décision prise dans la liste à confirmer.
/// </summary>
/// <param name="Selected">Identifiants des fichiers cochés</param>
/// <param name="Ignored">Chemins de la cible à ignorer désormais</param>
public sealed record ExchangeDialogResult(IReadOnlySet<int> Selected, IReadOnlyList<string> Ignored);

/// <summary>
/// Conflit d'un fichier, tel que la fenêtre de résolution le montre : les deux
/// versions côte à côte, leur contexte, et ce que l'on garde.
/// </summary>
public sealed class ConflictRowModel : NotifyingObject
{
    private ConflictChoice _choice;

    /// <summary>
    /// Décrit un conflit.
    /// </summary>
    /// <param name="block">Conflit de la fusion</param>
    /// <param name="count">Nombre de conflits du fichier</param>
    /// <param name="choice">Choix déjà fait</param>
    public ConflictRowModel(ConflictBlock block, int count, ConflictChoice choice)
    {
        ArgumentNullException.ThrowIfNull(block);

        Block = block;
        _choice = choice;
        Title = string.Create(CultureInfo.CurrentCulture, $"Conflit {block.Index + 1} sur {count} · ligne {block.Line}");
        ClearText = Lines(block.Clear, "(rien : supprimé côté clair)");
        AiText = Lines(block.Ai, "(rien : supprimé par l'IA)");
        BeforeText = string.Join(Environment.NewLine, block.Before);
        AfterText = string.Join(Environment.NewLine, block.After);
    }

    /// <summary>Conflit de la fusion.</summary>
    public ConflictBlock Block { get; }

    /// <summary>Rang et ligne du conflit.</summary>
    public string Title { get; }

    /// <summary>Version en clair actuelle.</summary>
    public string ClearText { get; }

    /// <summary>Version de l'IA, restaurée.</summary>
    public string AiText { get; }

    /// <summary>Lignes qui précèdent le passage.</summary>
    public string BeforeText { get; }

    /// <summary>Lignes qui le suivent.</summary>
    public string AfterText { get; }

    /// <summary>Vrai s'il y a des lignes avant le passage.</summary>
    public bool HasBefore => BeforeText.Length > 0;

    /// <summary>Vrai s'il y a des lignes après le passage.</summary>
    public bool HasAfter => AfterText.Length > 0;

    /// <summary>Ce que l'on garde du passage.</summary>
    public ConflictChoice Choice
    {
        get => _choice;
        set
        {
            if (_choice == value)
            {
                return;
            }

            _choice = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(KeepsClear));
            NotifyPropertyChanged(nameof(KeepsAi));
            NotifyPropertyChanged(nameof(ChoiceLabel));
            NotifyPropertyChanged(nameof(IsUnresolved));
        }
    }

    /// <summary>Vrai si la version en clair est gardée.</summary>
    public bool KeepsClear => Choice is ConflictChoice.Clear or ConflictChoice.Both;

    /// <summary>Vrai si la version de l'IA est gardée.</summary>
    public bool KeepsAi => Choice is ConflictChoice.Ai or ConflictChoice.Both;

    /// <summary>Vrai si rien n'est tranché.</summary>
    public bool IsUnresolved => Choice == ConflictChoice.Unresolved;

    /// <summary>Le choix, en quelques mots.</summary>
    public string ChoiceLabel => Choice switch
    {
        ConflictChoice.Clear => "on garde le clair",
        ConflictChoice.Ai => "on garde l'IA",
        ConflictChoice.Both => "on garde les deux, le clair d'abord",
        _ => "non tranché : marqueurs écrits",
    };

    /// <summary>
    /// Joint les lignes d'une version, ou dit qu'il n'y en a pas.
    /// </summary>
    /// <param name="lines">Lignes</param>
    /// <param name="empty">Texte d'une version vide</param>
    /// <returns>Le texte à afficher</returns>
    private static string Lines(IReadOnlyList<string> lines, string empty)
    {
        return lines.Count == 0 ? empty : string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Chemin d'une liste de l'onglet « Échanges » : une exclusion de la source ou
/// un fichier ignoré de la cible.
/// </summary>
/// <param name="Path">Chemin relatif</param>
public sealed record ExchangePathRow(string Path);

/// <summary>
/// Geste de l'historique, tel que l'onglet « Échanges » l'affiche.
/// </summary>
/// <param name="exchangeEvent">Geste enregistré</param>
public sealed class ExchangeEventRow(ExchangeEvent exchangeEvent)
{
    /// <summary>Date du geste, à la minute.</summary>
    public string When { get; } = exchangeEvent.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Geste et dossier écrit.</summary>
    public string What { get; } = exchangeEvent.Gesture == ExchangeGesture.Obfuscation
        ? $"Obfuscation vers « {exchangeEvent.Folder} »"
        : $"Rapatriement vers « {exchangeEvent.Folder} »";

    /// <summary>Comptes du geste.</summary>
    public string Counts { get; } = Describe(exchangeEvent);

    /// <summary>
    /// Résume les comptes d'un geste.
    /// </summary>
    /// <param name="exchangeEvent">Geste enregistré</param>
    /// <returns>Les comptes non nuls, ou « rien de changé »</returns>
    private static string Describe(ExchangeEvent exchangeEvent)
    {
        List<string> parts = [];
        if (exchangeEvent.Created > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{exchangeEvent.Created} créé(s)"));
        }

        if (exchangeEvent.Modified > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{exchangeEvent.Modified} modifié(s)"));
        }

        if (exchangeEvent.Deleted > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{exchangeEvent.Deleted} retiré(s)"));
        }

        if (exchangeEvent.Conflicts > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{exchangeEvent.Conflicts} conflit(s)"));
        }

        if (exchangeEvent.Warnings > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{exchangeEvent.Warnings} alerte(s)"));
        }

        return parts.Count == 0 ? "rien de changé" : string.Join(" · ", parts);
    }
}
