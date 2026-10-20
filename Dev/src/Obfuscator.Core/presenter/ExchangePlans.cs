using Obfuscator.Core.Model.Documents;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Exchange;

namespace Obfuscator.Core.Presenter;

/// <summary>Côté d'un échange.</summary>
public enum ExchangeSide
{
    /// <summary>Le dossier en clair, source de l'obfuscation.</summary>
    Source,

    /// <summary>Le dossier de l'IA, cible de l'obfuscation.</summary>
    Target,
}

/// <summary>Ce qu'un échange fait d'un fichier.</summary>
public enum ExchangeAction
{
    /// <summary>Obfuscation : fichier nouveau dans la cible.</summary>
    Create,

    /// <summary>Obfuscation : remplace la version précédente, que personne n'a touchée depuis.</summary>
    Update,

    /// <summary>Obfuscation : la cible a déjà ce contenu ; rien n'est écrit.</summary>
    Identical,

    /// <summary>
    /// Obfuscation : retiré de la cible, parce que la source l'a perdu, l'a
    /// exclu, ou que son nom pseudonymisé a changé.
    /// </summary>
    Delete,

    /// <summary>
    /// Obfuscation : le fichier de la cible a été modifié depuis le dernier
    /// échange — un travail de l'IA pas encore rapatrié —, ou n'est pas connu
    /// de l'outil. Il n'est écrasé que si on le demande.
    /// </summary>
    Blocked,

    /// <summary>Obfuscation : ancien fichier produit à la main dans le dossier de l'IA (<c>_obfusc</c>, légende).</summary>
    Legacy,

    /// <summary>Fichier que l'outil écrit lui-même : <c>AGENTS.md</c>, <c>CLAUDE.md</c>.</summary>
    Generated,

    /// <summary>Ne peut pas être écrit : la raison est donnée.</summary>
    Refused,

    /// <summary>Rapatriement : l'IA a modifié le fichier ; sa version est fusionnée dans la source.</summary>
    Modified,

    /// <summary>Rapatriement : l'IA a créé le fichier ; il est restauré dans la source.</summary>
    Created,

    /// <summary>Rapatriement : l'IA a supprimé le fichier ; la suppression est proposée.</summary>
    Deleted,
}

/// <summary>Nature d'une alerte d'échange.</summary>
public enum ExchangeWarningKind
{
    /// <summary>Une valeur masquée ailleurs reste en clair : l'écriture est interdite.</summary>
    Leak,

    /// <summary>Une valeur signalée n'a pas été tranchée.</summary>
    Suspect,

    /// <summary>Une valeur connue du coffre reste en clair par les règles actuelles.</summary>
    KnownInClear,

    /// <summary>Un fichier que l'on n'envoie d'ordinaire pas à une IA.</summary>
    Sensitive,

    /// <summary>Un jeton inconnu du coffre, laissé tel quel.</summary>
    UnknownToken,

    /// <summary>Un jeton d'un autre coffre, laissé tel quel.</summary>
    ForeignToken,

    /// <summary>Un repère « À_COMPLÉTER » laissé par l'IA.</summary>
    Placeholder,

    /// <summary>Une adresse déduite de son sous-réseau.</summary>
    Inferred,

    /// <summary>Une coordonnée inventée par l'IA.</summary>
    Coordinate,

    /// <summary>Une syntaxe cassée.</summary>
    Syntax,

    /// <summary>Un changement d'encodage.</summary>
    Encoding,

    /// <summary>Un conflit à résoudre.</summary>
    Conflict,

    /// <summary>Un script ou un réglage d'outil créé par l'IA.</summary>
    Script,

    /// <summary>Une remarque sur le fichier : nom, emplacement, base manquante.</summary>
    Note,
}

/// <summary>
/// Alerte attachée à un fichier d'un échange.
/// </summary>
/// <param name="Kind">Nature de l'alerte</param>
/// <param name="Line">Ligne concernée, à partir de 1 ; 0 pour le fichier entier</param>
/// <param name="Text">Message, sans aucune valeur réelle</param>
public sealed record ExchangeWarning(ExchangeWarningKind Kind, int Line, string Text)
{
    /// <summary>
    /// Vrai pour une alerte qui demande une action avant de commiter : une
    /// fuite, un conflit, une syntaxe cassée, une valeur à compléter.
    /// </summary>
    public bool IsSerious => Kind is ExchangeWarningKind.Leak
        or ExchangeWarningKind.Conflict
        or ExchangeWarningKind.Syntax
        or ExchangeWarningKind.UnknownToken
        or ExchangeWarningKind.ForeignToken
        or ExchangeWarningKind.Placeholder;
}

/// <summary>
/// Fichier d'un échange, tel que la liste à confirmer le montre.
/// </summary>
/// <param name="Id">Identifiant du fichier dans le plan, pour la sélection</param>
/// <param name="Action">Ce que l'échange en fait</param>
/// <param name="TargetPath">Chemin dans la cible, nom pseudonymisé, avec des barres obliques</param>
/// <param name="SourcePath">Chemin dans la source, avec des barres obliques ; vide pour un fichier propre à la cible</param>
/// <param name="Checked">Vrai si l'échange le fait d'office</param>
/// <param name="Optional">Vrai si l'utilisateur peut cocher ou décocher</param>
/// <param name="Warnings">Alertes attachées</param>
public sealed record ExchangeChange(
    int Id,
    ExchangeAction Action,
    string TargetPath,
    string SourcePath,
    bool Checked,
    bool Optional,
    IReadOnlyList<ExchangeWarning> Warnings)
{
    /// <summary>Lignes ajoutées au fichier écrit.</summary>
    public int Added { get; init; }

    /// <summary>Lignes retirées du fichier écrit.</summary>
    public int Removed { get; init; }

    /// <summary>Conflits écrits dans le fichier, à résoudre.</summary>
    public int Conflicts { get; init; }

    /// <summary>Explication courte de l'état du fichier, ou <c>null</c>.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Compte rendu d'un échange écrit.
/// </summary>
/// <param name="Created">Fichiers créés</param>
/// <param name="Modified">Fichiers modifiés</param>
/// <param name="Deleted">Fichiers retirés</param>
/// <param name="Conflicts">Conflits écrits, à résoudre</param>
/// <param name="Warnings">Alertes des fichiers écrits</param>
/// <param name="Skipped">Fichiers laissés de côté au dernier moment, avec la raison</param>
public sealed record ExchangeOutcome(
    int Created,
    int Modified,
    int Deleted,
    int Conflicts,
    int Warnings,
    IReadOnlyList<string> Skipped);

/// <summary>
/// État des deux côtés d'un échange, pour l'écran : ce qui a bougé depuis le
/// dernier échange.
/// </summary>
/// <param name="HasExchanged">Vrai si au moins une obfuscation a eu lieu vers cette cible depuis cette source</param>
/// <param name="TargetModified">Fichiers de la cible modifiés depuis</param>
/// <param name="TargetCreated">Fichiers créés dans la cible</param>
/// <param name="TargetDeleted">Fichiers supprimés de la cible</param>
/// <param name="SourceChanged">Fichiers de la source modifiés depuis</param>
/// <param name="SourceAdded">Fichiers nouveaux dans la source</param>
/// <param name="History">Derniers gestes, du plus ancien au plus récent</param>
public sealed record ExchangeStatus(
    bool HasExchanged,
    int TargetModified,
    int TargetCreated,
    int TargetDeleted,
    int SourceChanged,
    int SourceAdded,
    IReadOnlyList<ExchangeEvent> History)
{
    /// <summary>Fichiers de la cible à rapatrier.</summary>
    public int TargetPending => TargetModified + TargetCreated + TargetDeleted;

    /// <summary>Fichiers de la source à obfusquer de nouveau.</summary>
    public int SourcePending => SourceChanged + SourceAdded;

    /// <summary>Dernière obfuscation vers cette cible, ou <c>null</c>.</summary>
    public ExchangeEvent? LastObfuscation => History.LastOrDefault(item => item.Gesture == ExchangeGesture.Obfuscation);

    /// <summary>Dernier rapatriement depuis cette cible, ou <c>null</c>.</summary>
    public ExchangeEvent? LastReturn => History.LastOrDefault(item => item.Gesture == ExchangeGesture.Return);
}

/// <summary>
/// Obfuscation préparée : ce qu'elle écrira dans la cible, fichier par fichier,
/// avec ses alertes. Rien n'est écrit tant qu'elle n'est pas confirmée.
/// </summary>
public sealed class PublicationPlan
{
    /// <summary>
    /// Construit le plan.
    /// </summary>
    /// <param name="sourceRoot">Dossier source</param>
    /// <param name="targetRoot">Dossier cible</param>
    /// <param name="targetIsLinked">Vrai si la cible est le dossier de l'IA</param>
    /// <param name="isolation">Contrôle d'isolement</param>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <param name="writes">Écritures prévues, par identifiant</param>
    /// <param name="batch">Lot obfusqué, ou <c>null</c> si rien n'a pu être préparé</param>
    internal PublicationPlan(
        string sourceRoot,
        string targetRoot,
        bool targetIsLinked,
        IsolationReport isolation,
        IReadOnlyList<ExchangeChange> changes,
        IReadOnlyDictionary<int, PlannedWrite> writes,
        PreparedBatch? batch)
    {
        SourceRoot = sourceRoot;
        TargetRoot = targetRoot;
        TargetIsLinked = targetIsLinked;
        Isolation = isolation;
        Changes = changes;
        Writes = writes;
        Batch = batch;
    }

    /// <summary>Dossier source.</summary>
    public string SourceRoot { get; }

    /// <summary>Dossier cible.</summary>
    public string TargetRoot { get; }

    /// <summary>Vrai si la cible est le dossier de l'IA, faux pour « out ».</summary>
    public bool TargetIsLinked { get; }

    /// <summary>Contrôle d'isolement.</summary>
    public IsolationReport Isolation { get; }

    /// <summary>Fichiers de l'échange, dans l'ordre d'affichage.</summary>
    public IReadOnlyList<ExchangeChange> Changes { get; }

    /// <summary>Vrai si aucun fichier ne fuit.</summary>
    public bool IsClean => Batch?.IsClean ?? true;

    /// <summary>Vrai si l'obfuscation peut être écrite.</summary>
    public bool CanCommit => Batch is not null && IsClean && Isolation.IsSafe;

    /// <summary>Écritures prévues, par identifiant de fichier.</summary>
    internal IReadOnlyDictionary<int, PlannedWrite> Writes { get; }

    /// <summary>
    /// Lot obfusqué en mémoire, ou <c>null</c> si rien n'a pu être préparé :
    /// le détail du moteur par fichier, pour le rapport technique. Chaque
    /// fichier y porte son document en clair : seuls les comptes, les règles et
    /// les chemins de la cible sont faits pour être affichés.
    /// </summary>
    public PreparedBatch? Batch { get; }
}

/// <summary>
/// Rapatriement préparé : ce qu'il écrira dans la source, fichier par fichier,
/// fusions comprises. Rien n'est écrit tant qu'il n'est pas confirmé.
/// </summary>
public sealed class ReturnPlan
{
    /// <summary>
    /// Construit le plan.
    /// </summary>
    /// <param name="sourceRoot">Dossier source</param>
    /// <param name="sourceIsLinked">Vrai si la source est le dossier en clair, faux pour « in »</param>
    /// <param name="targetRoot">Dossier cible</param>
    /// <param name="hasExchanged">Vrai si une obfuscation a déjà eu lieu vers cette cible</param>
    /// <param name="isolation">Contrôle d'isolement</param>
    /// <param name="changes">Fichiers du rapatriement</param>
    /// <param name="writes">Écritures prévues, par identifiant</param>
    internal ReturnPlan(
        string sourceRoot,
        bool sourceIsLinked,
        string targetRoot,
        bool hasExchanged,
        IsolationReport isolation,
        IReadOnlyList<ExchangeChange> changes,
        IReadOnlyDictionary<int, PlannedReturn> writes)
    {
        SourceRoot = sourceRoot;
        SourceIsLinked = sourceIsLinked;
        TargetRoot = targetRoot;
        HasExchanged = hasExchanged;
        Isolation = isolation;

        // Copies propres au plan : la résolution d'un conflit remplace un
        // fichier et son écriture prévue.
        _changes = [.. changes];
        _writes = new Dictionary<int, PlannedReturn>(writes);
    }

    private readonly List<ExchangeChange> _changes;
    private readonly Dictionary<int, PlannedReturn> _writes;

    /// <summary>Dossier source, où le rapatriement écrit.</summary>
    public string SourceRoot { get; }

    /// <summary>
    /// Vrai si la source est le dossier en clair lié : git y garde l'historique.
    /// Faux pour « in », où la version remplacée part à la corbeille.
    /// </summary>
    public bool SourceIsLinked { get; }

    /// <summary>Dossier cible, d'où le rapatriement lit.</summary>
    public string TargetRoot { get; }

    /// <summary>Vrai si une obfuscation a déjà eu lieu vers cette cible depuis cette source.</summary>
    public bool HasExchanged { get; }

    /// <summary>Contrôle d'isolement.</summary>
    public IsolationReport Isolation { get; }

    /// <summary>Fichiers du rapatriement, dans l'ordre d'affichage.</summary>
    public IReadOnlyList<ExchangeChange> Changes => _changes;

    /// <summary>Vrai si le rapatriement peut être écrit.</summary>
    public bool CanCommit => Isolation.IsSafe && (Changes.Count > 0 || StaleEntries.Count > 0);

    /// <summary>Écritures prévues, par identifiant de fichier.</summary>
    internal IReadOnlyDictionary<int, PlannedReturn> Writes => _writes;

    /// <summary>Fichiers du registre disparus des deux côtés, à oublier.</summary>
    internal IReadOnlyList<string> StaleEntries { get; init; } = [];

    /// <summary>
    /// Donne les conflits d'un fichier, pour les trancher : chaque passage
    /// changé des deux côtés, avec les deux versions. Ils portent des valeurs
    /// réelles : ils sont faits pour l'écran.
    /// </summary>
    /// <param name="changeId">Identifiant du fichier</param>
    /// <returns>Les conflits, dans l'ordre du fichier ; aucun s'il ne se fusionne pas</returns>
    public IReadOnlyList<ConflictBlock> ConflictsOf(int changeId)
    {
        return _writes.TryGetValue(changeId, out PlannedReturn? write) && write.Merge is not null ? write.Blocks : [];
    }

    /// <summary>
    /// Donne les choix déjà faits pour les conflits d'un fichier.
    /// </summary>
    /// <param name="changeId">Identifiant du fichier</param>
    /// <returns>Un choix par conflit, « non tranché » par défaut</returns>
    public IReadOnlyList<ConflictChoice> ChoicesOf(int changeId)
    {
        IReadOnlyList<ConflictBlock> blocks = ConflictsOf(changeId);
        IReadOnlyList<ConflictChoice> made = _writes.TryGetValue(changeId, out PlannedReturn? write) ? write.Choices : [];
        return [.. blocks.Select(block => block.Index < made.Count ? made[block.Index] : ConflictChoice.Unresolved)];
    }

    /// <summary>
    /// Remplace un fichier du plan et son écriture prévue, après la résolution
    /// de ses conflits.
    /// </summary>
    /// <param name="change">Fichier mis à jour</param>
    /// <param name="write">Écriture prévue mise à jour</param>
    internal void Replace(ExchangeChange change, PlannedReturn write)
    {
        _changes[_changes.FindIndex(item => item.Id == change.Id)] = change;
        _writes[change.Id] = write;
    }
}

/// <summary>
/// Écriture prévue par une obfuscation.
/// </summary>
/// <param name="Action">Ce que l'échange fait du fichier</param>
/// <param name="TargetRelative">Chemin dans la cible</param>
/// <param name="Content">Octets à écrire, ou <c>null</c> pour un retrait</param>
/// <param name="ObservedHash">Empreinte du fichier de la cible au moment du plan, <c>null</c> s'il n'existait pas</param>
/// <param name="SourceKey">Clé du fichier dans la source, vide pour un fichier propre à la cible</param>
/// <param name="SourceHash">Empreinte du fichier en clair publié</param>
internal sealed record PlannedWrite(
    ExchangeAction Action,
    string TargetRelative,
    byte[]? Content,
    string? ObservedHash,
    string SourceKey,
    string SourceHash)
{
    /// <summary>Vrai pour le retrait de l'ancien nom d'un fichier renommé : son entrée suit le nouveau nom.</summary>
    public bool IsRename { get; init; }

    /// <summary>Légende à écrire à côté du fichier, dans « out ».</summary>
    public string? Legend { get; init; }
}

/// <summary>
/// Écriture prévue par un rapatriement.
/// </summary>
/// <param name="Action">Ce que l'échange fait du fichier</param>
/// <param name="SourceKey">Clé du fichier dans la source</param>
/// <param name="TargetRelative">Chemin dans la cible</param>
/// <param name="Content">Octets à écrire dans la source, ou <c>null</c> pour un retrait</param>
/// <param name="ObservedSourceHash">Empreinte du fichier en clair au moment du plan, <c>null</c> s'il n'existait pas</param>
/// <param name="TargetBytes">Contenu de la cible rapatrié, qui devient la base du prochain échange</param>
internal sealed record PlannedReturn(
    ExchangeAction Action,
    string SourceKey,
    string TargetRelative,
    byte[]? Content,
    string? ObservedSourceHash,
    byte[] TargetBytes)
{
    /// <summary>Pour chaque ligne du fichier en clair, son rang dans le fichier écrit, pour recaler les exceptions.</summary>
    public IReadOnlyList<int>? LineMap { get; init; }

    /// <summary>
    /// Ce qu'il faut pour refaire la fusion d'un fichier modifié des deux
    /// côtés, conflits tranchés ; <c>null</c> quand il n'y a pas de fusion à
    /// refaire.
    /// </summary>
    public MergeInputs? Merge { get; init; }

    /// <summary>Conflits de la fusion, dans l'ordre du fichier.</summary>
    public IReadOnlyList<ConflictBlock> Blocks { get; init; } = [];

    /// <summary>Choix faits pour les conflits, dans le même ordre.</summary>
    public IReadOnlyList<ConflictChoice> Choices { get; init; } = [];
}

/// <summary>
/// Éléments d'une fusion, gardés pour la refaire une fois les conflits
/// tranchés.
/// </summary>
/// <param name="Base">Base, restaurée</param>
/// <param name="Current">Fichier en clair actuel</param>
/// <param name="Theirs">Version de l'IA, restaurée</param>
/// <param name="Document">Fichier en clair actuel, avec son encodage</param>
/// <param name="Restored">Restauration de la version de l'IA, pour ses alertes</param>
/// <param name="Kind">Format du fichier</param>
internal sealed record MergeInputs(
    TextLayout Base,
    TextLayout Current,
    TextLayout Theirs,
    SourceDocument Document,
    DeobfuscationResult Restored,
    FileKind Kind);
