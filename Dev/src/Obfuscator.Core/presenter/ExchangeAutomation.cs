using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Codes de retour des échanges pilotés par une autre application. Ils sont
/// les mêmes pour l'état, la publication et le rapatriement.
/// </summary>
public enum AutomationExitCode
{
    /// <summary>Tout est passé ; en simulation, tout passerait.</summary>
    Ok = 0,

    /// <summary>Erreur technique : projet introuvable, coffre illisible, exception.</summary>
    Error = 1,

    /// <summary>Fuite, isolement non sûr, fichier refusé ou bloqué : rien ne peut être écrit.</summary>
    Blocked = 2,

    /// <summary>Publication : des éléments attendent une décision de l'utilisateur.</summary>
    Pending = 3,

    /// <summary>Rapatriement : des conflits attendent d'être tranchés.</summary>
    Conflicts = 4,
}

/// <summary>Contrôle d'isolement, tel que le rapport le donne.</summary>
/// <param name="Safe">Vrai si l'échange peut avoir lieu</param>
/// <param name="Message">Ce qui bloque, sinon les avertissements ; vide si rien n'est à dire</param>
public sealed record AutomationIsolation(bool Safe, string Message);

/// <summary>Alerte d'un fichier, telle que le rapport la donne.</summary>
/// <param name="Kind">Nature de l'alerte</param>
/// <param name="Serious">Vrai si elle demande une action avant de commiter</param>
/// <param name="Line">Ligne concernée, à partir de 1 ; 0 pour le fichier entier</param>
/// <param name="Message">Message, sans valeur réelle ni pseudonyme associé à une valeur</param>
public sealed record AutomationWarning(ExchangeWarningKind Kind, bool Serious, int Line, string Message);

/// <summary>Fichier d'un échange, tel que le rapport le donne.</summary>
/// <param name="Action">Ce que l'échange en fait</param>
/// <param name="Path">Chemin dans la cible, pseudonymisé, relatif au dossier de l'IA ou à « out »</param>
/// <param name="Added">Lignes ajoutées</param>
/// <param name="Removed">Lignes retirées</param>
/// <param name="Conflicts">Conflits à résoudre</param>
/// <param name="Warnings">Alertes</param>
public sealed record AutomationChange(
    ExchangeAction Action,
    string Path,
    int Added,
    int Removed,
    int Conflicts,
    IReadOnlyList<AutomationWarning> Warnings);

/// <summary>Ce qu'un fichier du rapatriement attend de l'utilisateur.</summary>
public enum AutomationConflictReason
{
    /// <summary>Des passages changés des deux côtés, à trancher.</summary>
    Conflict,

    /// <summary>
    /// Un choix que l'échange ne fait pas d'office : accepter une suppression
    /// de l'IA, recréer un fichier supprimé côté clair, remplacer un fichier
    /// sans base connue. Le laisser de côté serait déjà une décision.
    /// </summary>
    Choice,
}

/// <summary>Fichier d'un rapatriement qui attend une décision.</summary>
/// <param name="Path">Chemin dans la cible, pseudonymisé</param>
/// <param name="Count">Nombre de conflits à trancher ; 0 pour un simple choix</param>
/// <param name="Reason">Ce que le fichier attend</param>
public sealed record AutomationConflict(string Path, int Count, AutomationConflictReason Reason);

/// <summary>Compte rendu d'un échange écrit.</summary>
/// <param name="Created">Fichiers créés</param>
/// <param name="Modified">Fichiers modifiés</param>
/// <param name="Deleted">Fichiers retirés</param>
/// <param name="Skipped">Fichiers laissés de côté au dernier moment, sous leur chemin dans la cible</param>
public sealed record AutomationOutcome(int Created, int Modified, int Deleted, IReadOnlyList<string> Skipped);

/// <summary>État des deux côtés d'un échange, tel que le rapport le donne.</summary>
/// <param name="HasExchanged">Vrai si une obfuscation a déjà eu lieu vers cette cible depuis cette source</param>
/// <param name="TargetPending">Fichiers de la cible à rapatrier</param>
/// <param name="SourcePending">Fichiers de la source à obfusquer de nouveau</param>
/// <param name="TargetModified">Fichiers de la cible modifiés</param>
/// <param name="TargetCreated">Fichiers créés dans la cible</param>
/// <param name="TargetDeleted">Fichiers supprimés de la cible</param>
/// <param name="SourceChanged">Fichiers de la source modifiés</param>
/// <param name="SourceAdded">Fichiers nouveaux dans la source</param>
/// <param name="LastObfuscation">Date de la dernière obfuscation, au format ISO 8601, ou <c>null</c></param>
/// <param name="LastReturn">Date du dernier rapatriement, au format ISO 8601, ou <c>null</c></param>
public sealed record AutomationStatus(
    bool HasExchanged,
    int TargetPending,
    int SourcePending,
    int TargetModified,
    int TargetCreated,
    int TargetDeleted,
    int SourceChanged,
    int SourceAdded,
    string? LastObfuscation,
    string? LastReturn);

/// <summary>
/// Rapport d'un échange piloté par une autre application : l'état, la
/// publication ou le rapatriement, simulé ou écrit.
///
/// Toutes les listes sont présentes, vides plutôt qu'absentes, pour que
/// l'application qui le lit n'ait pas à deviner. Le rapport ne porte jamais de
/// valeur réelle : les valeurs sont masquées, les chemins sont ceux de la
/// cible, pseudonymisés.
/// </summary>
public sealed record AutomationReport
{
    /// <summary>Commande exécutée : <c>status</c>, <c>publish</c> ou <c>return</c>.</summary>
    public required string Command { get; init; }

    /// <summary>Vrai pour une simulation, qui n'écrit rien.</summary>
    public bool DryRun { get; init; }

    /// <summary>Code de retour, le plus grave des cas rencontrés.</summary>
    public int ExitCode { get; init; }

    /// <summary>Vrai si l'échange a été écrit.</summary>
    public bool Applied { get; init; }

    /// <summary>Dossier du projet, ou <c>null</c> s'il est introuvable.</summary>
    public string? Project { get; init; }

    /// <summary>Dossier source : le dossier en clair lié, sinon « in ».</summary>
    public string? SourceFolder { get; init; }

    /// <summary>Dossier cible : le dossier de l'IA lié, sinon « out ».</summary>
    public string? TargetFolder { get; init; }

    /// <summary>Vrai si l'échange pourrait être écrit.</summary>
    public bool CanCommit { get; init; }

    /// <summary>Contrôle d'isolement.</summary>
    public AutomationIsolation Isolation { get; init; } = new(false, string.Empty);

    /// <summary>Nombre de fichiers par action, toutes les actions de la commande présentes.</summary>
    public IReadOnlyDictionary<string, int> Summary { get; init; } = new Dictionary<string, int>();

    /// <summary>Fichiers de l'échange, hors fichiers identiques.</summary>
    public IReadOnlyList<AutomationChange> Changes { get; init; } = [];

    /// <summary>Publication : éléments qui attendent une décision.</summary>
    public IReadOnlyList<PendingDecision> Pending { get; init; } = [];

    /// <summary>Rapatriement : fichiers en conflit.</summary>
    public IReadOnlyList<AutomationConflict> Conflicts { get; init; } = [];

    /// <summary>Compte rendu, si l'échange a été écrit.</summary>
    public AutomationOutcome? Outcome { get; init; }

    /// <summary>Erreurs techniques.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>État des échanges, pour la commande <c>status</c>.</summary>
    public AutomationStatus? Status { get; init; }
}

/// <summary>
/// Pilote les échanges d'un projet pour une autre application : répondre à
/// « est-ce que tout passe ? » sans rien écrire, puis publier ou rapatrier
/// seulement si rien n'attend l'utilisateur.
///
/// Aucune règle n'est réécrite ici : tout passe par <see cref="ExchangePresenter"/>,
/// comme depuis l'écran. Une simulation travaille sur un instantané du projet,
/// qu'il est impossible d'enregistrer : elle ne peut rien écrire, ni règles, ni
/// coffre, ni registre.
/// </summary>
public static class ExchangeAutomation
{
    /// <summary>Commande de l'état des échanges.</summary>
    public const string StatusCommand = "status";

    /// <summary>Commande de la publication.</summary>
    public const string PublishCommand = "publish";

    /// <summary>Commande du rapatriement.</summary>
    public const string ReturnCommand = "return";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Actions d'une publication, dans l'ordre du résumé.</summary>
    private static readonly ExchangeAction[] PublicationActions =
    [
        ExchangeAction.Create, ExchangeAction.Update, ExchangeAction.Delete, ExchangeAction.Identical,
        ExchangeAction.Blocked, ExchangeAction.Refused, ExchangeAction.Legacy, ExchangeAction.Generated,
    ];

    /// <summary>Actions d'un rapatriement, dans l'ordre du résumé.</summary>
    private static readonly ExchangeAction[] ReturnActions =
    [
        ExchangeAction.Modified, ExchangeAction.Created, ExchangeAction.Deleted, ExchangeAction.Refused,
    ];

    /// <summary>
    /// Donne l'état des échanges. Lecture seule.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    /// <returns>Le rapport, code 0</returns>
    public static AutomationReport Status(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        ExchangeStatus status = ExchangePresenter.Status(project, CancellationToken.None);
        return new AutomationReport
        {
            Command = StatusCommand,
            ExitCode = (int)AutomationExitCode.Ok,
            Project = project.RootPath,
            SourceFolder = project.SourcePath,
            TargetFolder = project.TargetPath,
            Isolation = IsolationOf(ExchangePresenter.CheckIsolation(project)),
            Summary = SummaryOf([], PublicationActions),
            Status = new AutomationStatus(
                status.HasExchanged,
                status.TargetPending,
                status.SourcePending,
                status.TargetModified,
                status.TargetCreated,
                status.TargetDeleted,
                status.SourceChanged,
                status.SourceAdded,
                IsoDate(status.LastObfuscation),
                IsoDate(status.LastReturn)),
        };
    }

    /// <summary>
    /// Prépare la publication et l'écrit si rien ne s'y oppose : ni fuite, ni
    /// défaut d'isolement, ni fichier refusé ou bloqué, ni élément à trancher.
    /// Sinon, ou en simulation, rien n'est écrit. Aucune opération git.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    /// <param name="dryRun">Vrai pour une simulation</param>
    /// <param name="recycler">Retrait des fichiers, pour une publication écrite</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le rapport</returns>
    public static AutomationReport Publish(ObfuscationProject project, bool dryRun, IFileRecycler recycler, IProgress<BatchProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(recycler);

        // Une simulation travaille sur un instantané : tout enregistrement y
        // est refusé, ce qui garantit qu'elle n'écrit rien.
        ExchangePresenter presenter = new(dryRun ? project.CreateSnapshot() : project);
        PublicationPlan plan = presenter.PreparePublication(progress);
        IReadOnlyList<PendingDecision> pending = presenter.PendingDecisions(plan);

        // Un fichier bloqué ou refusé ne part pas sans décision : la
        // publication n'est pas complète, l'écran doit la montrer.
        bool blocked = !plan.CanCommit
            || plan.Changes.Any(change => change.Action is ExchangeAction.Blocked or ExchangeAction.Refused);

        AutomationExitCode code = Worst(
            blocked ? AutomationExitCode.Blocked : AutomationExitCode.Ok,
            pending.Count > 0 ? AutomationExitCode.Pending : AutomationExitCode.Ok);

        AutomationOutcome? outcome = null;
        if (!dryRun && code == AutomationExitCode.Ok)
        {
            // Les fichiers facultatifs sont pris tels que l'écran les coche d'office.
            HashSet<int> selected = [.. plan.Changes.Where(change => change.Optional && change.Checked).Select(change => change.Id)];
            ExchangeOutcome written = presenter.CommitPublication(plan, selected, recycler);
            outcome = new AutomationOutcome(written.Created, written.Modified, written.Deleted, [.. written.Skipped.Select(TargetOfSkipped)]);
        }

        return new AutomationReport
        {
            Command = PublishCommand,
            DryRun = dryRun,
            ExitCode = (int)code,
            Applied = outcome is not null,
            Project = project.RootPath,
            SourceFolder = plan.SourceRoot,
            TargetFolder = plan.TargetRoot,
            CanCommit = plan.CanCommit,
            Isolation = IsolationOf(plan.Isolation),
            Summary = SummaryOf(plan.Changes, PublicationActions),
            Changes = ChangesOf(plan.Changes),
            Pending = pending,
            Outcome = outcome,
        };
    }

    /// <summary>
    /// Prépare le rapatriement et l'écrit s'il ne demande aucun choix : ni
    /// conflit, ni défaut d'isolement, ni fichier refusé. Sinon, ou en
    /// simulation, rien n'est écrit. Aucune opération git.
    /// </summary>
    /// <param name="project">Projet ouvert</param>
    /// <param name="dryRun">Vrai pour une simulation</param>
    /// <param name="recycler">Retrait des fichiers, pour un rapatriement écrit</param>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le rapport</returns>
    public static AutomationReport Return(ObfuscationProject project, bool dryRun, IFileRecycler recycler, IProgress<BatchProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(recycler);

        ExchangePresenter presenter = new(dryRun ? project.CreateSnapshot() : project);
        ReturnPlan plan = presenter.PrepareReturn(progress);

        List<AutomationConflict> conflicts = [.. plan.Changes.Select(change => ConflictOf(plan, change)).OfType<AutomationConflict>()];

        bool blocked = !plan.Isolation.IsSafe || plan.Changes.Any(change => change.Action == ExchangeAction.Refused);
        AutomationExitCode code = Worst(
            blocked ? AutomationExitCode.Blocked : AutomationExitCode.Ok,
            conflicts.Count > 0 ? AutomationExitCode.Conflicts : AutomationExitCode.Ok);

        AutomationOutcome? outcome = null;
        if (!dryRun && code == AutomationExitCode.Ok && plan.CanCommit)
        {
            HashSet<int> selected = [.. plan.Changes.Where(change => change.Optional && change.Checked).Select(change => change.Id)];
            ExchangeOutcome written = presenter.CommitReturn(plan, selected, recycler);

            // Un fichier laissé de côté est nommé par son chemin en clair :
            // le rapport le donne sous son chemin dans la cible.
            outcome = new AutomationOutcome(written.Created, written.Modified, written.Deleted, [.. written.Skipped.Select(skipped => TargetOfSkipped(plan, skipped))]);
        }

        return new AutomationReport
        {
            Command = ReturnCommand,
            DryRun = dryRun,
            ExitCode = (int)code,
            Applied = outcome is not null,
            Project = project.RootPath,
            SourceFolder = plan.SourceRoot,
            TargetFolder = plan.TargetRoot,
            CanCommit = plan.Isolation.IsSafe && !blocked,
            Isolation = IsolationOf(plan.Isolation),
            Summary = SummaryOf(plan.Changes, ReturnActions),
            Changes = ChangesOf(plan.Changes),
            Conflicts = conflicts,
            Outcome = outcome,
        };
    }

    /// <summary>
    /// Compose le rapport d'une erreur technique : projet introuvable, coffre
    /// illisible, exception.
    /// </summary>
    /// <param name="command">Commande exécutée</param>
    /// <param name="dryRun">Vrai pour une simulation</param>
    /// <param name="project">Projet, s'il a pu être ouvert</param>
    /// <param name="error">Erreur rencontrée</param>
    /// <returns>Le rapport, code 1</returns>
    public static AutomationReport Failure(string command, bool dryRun, ObfuscationProject? project, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new AutomationReport
        {
            Command = command,
            DryRun = dryRun,
            ExitCode = (int)AutomationExitCode.Error,
            Project = project?.RootPath,
            SourceFolder = project?.SourcePath,
            TargetFolder = project?.TargetPath,
            Summary = SummaryOf([], string.Equals(command, ReturnCommand, StringComparison.Ordinal) ? ReturnActions : PublicationActions),
            Errors = [error.Message],
        };
    }

    /// <summary>
    /// Met un rapport en JSON : un seul objet, clés en camelCase, natures en
    /// toutes lettres.
    /// </summary>
    /// <param name="report">Rapport</param>
    /// <returns>Le texte JSON</returns>
    public static string ToJson(AutomationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    /// <summary>
    /// Retient le plus grave de deux codes, dans l'ordre 1 &gt; 2 &gt; 4 &gt; 3 &gt; 0.
    /// </summary>
    /// <param name="first">Premier code</param>
    /// <param name="second">Second code</param>
    /// <returns>Le plus grave</returns>
    public static AutomationExitCode Worst(AutomationExitCode first, AutomationExitCode second)
    {
        return Severity(first) >= Severity(second) ? first : second;
    }

    /// <summary>
    /// Donne le rang de gravité d'un code.
    /// </summary>
    /// <param name="code">Code de retour</param>
    /// <returns>Un rang, d'autant plus élevé que le code est grave</returns>
    private static int Severity(AutomationExitCode code)
    {
        return code switch
        {
            AutomationExitCode.Error => 4,
            AutomationExitCode.Blocked => 3,
            AutomationExitCode.Conflicts => 2,
            AutomationExitCode.Pending => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Dit ce qu'un fichier du rapatriement attend de l'utilisateur, s'il
    /// attend quelque chose.
    /// </summary>
    /// <param name="plan">Plan du rapatriement</param>
    /// <param name="change">Fichier du plan</param>
    /// <returns>Le conflit ou le choix à faire, ou <c>null</c> si le fichier peut être écrit tel quel</returns>
    private static AutomationConflict? ConflictOf(ReturnPlan plan, ExchangeChange change)
    {
        // Un fichier a des conflits à trancher s'il en porte encore, ou si sa
        // fusion attend des choix que personne n'a faits.
        int unresolved = Math.Max(change.Conflicts, plan.ChoicesOf(change.Id).Count(choice => choice == ConflictChoice.Unresolved));
        if (unresolved > 0)
        {
            return new AutomationConflict(change.TargetPath, unresolved, AutomationConflictReason.Conflict);
        }

        // Un fichier facultatif laissé décoché par défaut demande un choix :
        // l'écrire sans lui déciderait à la place de l'utilisateur — une
        // suppression de l'IA déclinée, par exemple, est oubliée du registre.
        return change.Optional && !change.Checked && change.Action != ExchangeAction.Refused
            ? new AutomationConflict(change.TargetPath, 0, AutomationConflictReason.Choice)
            : null;
    }

    /// <summary>
    /// Résume le contrôle d'isolement.
    /// </summary>
    /// <param name="report">Contrôle</param>
    /// <returns>Le verdict et ce qu'il y a à en dire</returns>
    private static AutomationIsolation IsolationOf(IsolationReport report)
    {
        IEnumerable<string> messages = report.IsSafe ? report.Warnings : report.Blocking;
        return new AutomationIsolation(report.IsSafe, string.Join(" ", messages));
    }

    /// <summary>
    /// Compte les fichiers par action : toutes les actions de la commande, à
    /// zéro quand aucun fichier ne s'y range.
    /// </summary>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <param name="actions">Actions de la commande</param>
    /// <returns>Les comptes, par action en camelCase</returns>
    private static Dictionary<string, int> SummaryOf(IReadOnlyList<ExchangeChange> changes, ExchangeAction[] actions)
    {
        Dictionary<string, int> summary = [];
        foreach (ExchangeAction action in actions)
        {
            summary[JsonNamingPolicy.CamelCase.ConvertName(action.ToString())] = changes.Count(change => change.Action == action);
        }

        return summary;
    }

    /// <summary>
    /// Décrit les fichiers de l'échange, hors fichiers identiques.
    /// </summary>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <returns>Les fichiers, sous leur chemin dans la cible</returns>
    private static List<AutomationChange> ChangesOf(IReadOnlyList<ExchangeChange> changes)
    {
        return [.. changes
            .Where(change => change.Action != ExchangeAction.Identical)
            .Select(change => new AutomationChange(
                change.Action,
                change.TargetPath,
                change.Added,
                change.Removed,
                change.Conflicts,
                [.. change.Warnings.Select(WarningOf)]))];
    }

    /// <summary>
    /// Décrit une alerte pour le rapport. Les messages qui associent un extrait
    /// de valeur à son pseudonyme — fuite, valeur connue restée en clair — ou
    /// qui reprennent un passage du fichier — syntaxe illisible — sont
    /// remplacés par un message neutre : seule leur nature et leur ligne sortent.
    /// </summary>
    /// <param name="warning">Alerte d'échange</param>
    /// <returns>L'alerte du rapport</returns>
    private static AutomationWarning WarningOf(ExchangeWarning warning)
    {
        string message = warning.Kind switch
        {
            ExchangeWarningKind.Leak => "fuite : une valeur masquée ailleurs reste en clair dans ce fichier ; rien ne part tant qu'elle n'est pas masquée.",
            ExchangeWarningKind.KnownInClear => "une valeur masquée autrefois reste en clair selon les règles actuelles.",
            ExchangeWarningKind.Syntax => "syntaxe illisible : le fichier ne se relit plus dans son format.",
            _ => warning.Text,
        };

        return new AutomationWarning(warning.Kind, warning.IsSerious, warning.Line, message);
    }

    /// <summary>
    /// Ramène un fichier laissé de côté par une publication à son chemin : le
    /// compte rendu le nomme par son chemin dans la cible, suivi de la raison.
    /// </summary>
    /// <param name="skipped">Mention du compte rendu</param>
    /// <returns>Le chemin dans la cible</returns>
    private static string TargetOfSkipped(string skipped)
    {
        int separator = skipped.IndexOf(" : ", StringComparison.Ordinal);
        return separator < 0 ? skipped : skipped[..separator];
    }

    /// <summary>
    /// Ramène un fichier laissé de côté par un rapatriement à son chemin dans
    /// la cible : le compte rendu le nomme par son chemin en clair, qui ne doit
    /// pas sortir du poste.
    /// </summary>
    /// <param name="plan">Plan du rapatriement</param>
    /// <param name="skipped">Mention du compte rendu</param>
    /// <returns>Le chemin dans la cible, ou une mention neutre si le fichier n'est pas retrouvé</returns>
    private static string TargetOfSkipped(ReturnPlan plan, string skipped)
    {
        string source = TargetOfSkipped(skipped);
        ExchangeChange? change = plan.Changes.FirstOrDefault(item => string.Equals(item.SourcePath, source, StringComparison.OrdinalIgnoreCase));
        return change?.TargetPath ?? "(fichier modifié pendant la confirmation)";
    }

    /// <summary>
    /// Donne la date d'un geste au format ISO 8601.
    /// </summary>
    /// <param name="exchange">Geste, ou <c>null</c></param>
    /// <returns>La date, ou <c>null</c></returns>
    private static string? IsoDate(ExchangeEvent? exchange)
    {
        return exchange?.At.ToString("o", CultureInfo.InvariantCulture);
    }
}
