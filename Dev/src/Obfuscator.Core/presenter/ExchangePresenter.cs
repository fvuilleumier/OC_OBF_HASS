using System.Collections.Concurrent;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Orchestre les échanges entre la source — le dossier en clair lié, sinon
/// « in » — et la cible — le dossier de l'IA lié, sinon « out » : l'obfuscation
/// de l'une vers l'autre, et le rapatriement du travail de l'IA.
///
/// Chaque geste se fait en deux temps : un plan, calculé sans rien écrire, que
/// l'écran montre fichier par fichier ; puis, sur confirmation, l'écriture de
/// ce plan et rien d'autre. Le registre des échanges garde, pour chaque
/// fichier, l'empreinte de ce que chaque côté avait au dernier échange : c'est
/// ce qui permet de ne jamais écraser un travail que personne n'a encore vu,
/// sans lancer git pour le savoir.
///
/// L'obfuscation est calculée sur un instantané du projet pris à la
/// construction — sur le fil de l'écran — : ses attributions n'entrent au
/// coffre qu'à la confirmation, tout ou rien.
/// </summary>
public sealed partial class ExchangePresenter
{
    /// <summary>
    /// Fichiers propres à chaque dépôt, qui ne voyagent dans aucun sens : ils
    /// règlent git, pas la configuration.
    /// </summary>
    private static readonly string[] RepositoryFiles = [".gitignore", ".gitattributes", ".gitmodules"];

    /// <summary>
    /// Chemins de la cible que le rapatriement ignore toujours : les réglages
    /// des outils de l'IA et de l'éditeur, et les fichiers de l'outil.
    /// </summary>
    private static readonly string[] TargetIgnored =
    [
        .. RepositoryFiles, ".claude", ".vscode", ".idea", ".cursor", ".github",
        AgentGuideWriter.AgentsFileName, AgentGuideWriter.ClaudeFileName,
    ];

    /// <summary>Empreintes déjà calculées, par chemin, taille et date d'écriture.</summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> Hashes = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObfuscationProject _project;
    private readonly ObfuscationProject _snapshot;
    private int _prepared;

    /// <summary>
    /// Prépare les échanges d'un projet, en prenant un instantané. À appeler
    /// sur le fil qui modifie le projet.
    /// </summary>
    /// <param name="project">Projet vivant</param>
    public ExchangePresenter(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _project = project;
        _snapshot = project.CreateSnapshot();
    }

    /// <summary>
    /// Recense les fichiers de la source : les fichiers texte, hors dossiers de
    /// travail, exclusions, sorties de l'outil et fichiers propres au dépôt.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <returns>Les fichiers, triés, et les liens écartés</returns>
    /// <exception cref="DirectoryNotFoundException">Si la source est introuvable</exception>
    public static CollectionResult CollectSource(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string source = project.SourcePath;
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Dossier source introuvable : « {source} ».");
        }

        // Un guide propre au dossier en clair ne part pas : la cible a le sien,
        // que l'outil écrit.
        List<string> excluded = [.. project.Settings.Exchange.Excluded, .. RepositoryFiles];
        if (project.IsTargetLinked)
        {
            excluded.Add(AgentGuideWriter.AgentsFileName);
            excluded.Add(AgentGuideWriter.ClaudeFileName);
        }

        return new TextFileCollector { SkipProducedFiles = true, ExcludedPaths = excluded }.CollectDetailed(source);
    }

    /// <summary>
    /// Recense les fichiers de la cible que le rapatriement examine : hors
    /// réglages d'outils, fichiers de l'outil, fichiers ignorés par
    /// l'utilisateur et, dans « out », légendes.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <returns>Les fichiers, triés, et les liens écartés ; rien si la cible n'existe pas</returns>
    public static CollectionResult CollectTarget(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string target = project.TargetPath;
        if (!Directory.Exists(target))
        {
            return new CollectionResult([], []);
        }

        CollectionResult collected = new TextFileCollector
        {
            ExcludedPaths = [.. TargetIgnored, .. project.Settings.Exchange.Ignored],
        }.CollectDetailed(target);

        return project.IsTargetLinked
            ? collected
            : collected with { Files = [.. collected.Files.Where(file => !OutputNaming.IsLegend(Path.GetFileName(file)))] };
    }

    /// <summary>
    /// Vérifie l'isolement des dossiers du projet.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <returns>Le verdict</returns>
    public static IsolationReport CheckIsolation(ObfuscationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        IReadOnlyList<SkippedLink> links = project.IsTargetLinked ? CollectTarget(project).Links : [];
        return IsolationGuard.Check(
            project.RootPath,
            project.IsSourceLinked ? project.SourcePath : null,
            project.IsTargetLinked ? project.TargetPath : null,
            links);
    }

    /// <summary>
    /// Lie un dossier au projet, ou le délie. Le lien n'est posé que si
    /// l'isolement le permet ; sinon les réglages restent tels quels.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="side">Côté à lier</param>
    /// <param name="folder">Dossier à lier, ou <c>null</c> pour revenir à « in » ou « out »</param>
    /// <returns>Le verdict de l'isolement avec le nouveau lien</returns>
    public static IsolationReport Link(ObfuscationProject project, ExchangeSide side, string? folder)
    {
        ArgumentNullException.ThrowIfNull(project);

        string value = string.IsNullOrWhiteSpace(folder) ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        ExchangeSettings exchange = project.Settings.Exchange;
        string clear = side == ExchangeSide.Source ? value : exchange.ClearFolder;
        string ai = side == ExchangeSide.Target ? value : exchange.AiFolder;

        IReadOnlyList<SkippedLink> links = ai.Length > 0 && Directory.Exists(ai)
            ? new TextFileCollector { ExcludedPaths = TargetIgnored }.CollectDetailed(ai).Links
            : [];

        IsolationReport report = IsolationGuard.Check(project.RootPath, clear.Length > 0 ? clear : null, ai.Length > 0 ? ai : null, links);
        if (!report.IsSafe)
        {
            return report;
        }

        if (side == ExchangeSide.Source)
        {
            exchange.ClearFolder = value;
        }
        else
        {
            exchange.AiFolder = value;
        }

        project.SaveRules();
        return report;
    }

    /// <summary>
    /// Tient un fichier ou un dossier de la source à l'écart des échanges.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="relative">Chemin relatif à la source ; un dossier se termine par une barre</param>
    public static void Exclude(ObfuscationProject project, string relative)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(relative);
        AddPath(project, project.Settings.Exchange.Excluded, relative);
    }

    /// <summary>
    /// Remet dans les échanges un chemin exclu.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="relative">Chemin relatif à la source</param>
    public static void Include(ObfuscationProject project, string relative)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(relative);
        RemovePath(project, project.Settings.Exchange.Excluded, relative);
    }

    /// <summary>
    /// Fait ignorer au rapatriement un fichier de la cible, une fois pour toutes.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="relative">Chemin relatif à la cible</param>
    public static void Ignore(ObfuscationProject project, string relative)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(relative);
        AddPath(project, project.Settings.Exchange.Ignored, relative);
    }

    /// <summary>
    /// Fait de nouveau examiner au rapatriement un fichier ignoré.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="relative">Chemin relatif à la cible</param>
    public static void Unignore(ObfuscationProject project, string relative)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(relative);
        RemovePath(project, project.Settings.Exchange.Ignored, relative);
    }

    /// <summary>
    /// Donne l'état des deux côtés depuis le dernier échange : ce que l'IA a
    /// modifié, créé ou supprimé, et ce qui a changé côté source. Se calcule
    /// sur n'importe quel fil ; les empreintes déjà calculées sont reprises tant
    /// que la taille et la date d'un fichier n'ont pas changé.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>L'état</returns>
    public static ExchangeStatus Status(ObfuscationProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        ExchangeRegister register = ExchangeRegister.Load(project.ExchangeRegisterPath, project.ExchangeProtector);
        ExchangeGroup? group = register.FindGroup(project.TargetPath, project.SourcePath);
        List<ExchangeEvent> history = [.. register.History];

        if (group is null)
        {
            return new ExchangeStatus(false, 0, 0, 0, 0, 0, history);
        }

        // Côté cible : ce que l'IA a fait depuis le dernier échange.
        int modified = 0;
        int deleted = 0;
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExchangeEntry entry in group.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            known.Add(entry.Target);

            string full = RelativePaths.Resolve(project.TargetPath, entry.Target);
            if (!File.Exists(full))
            {
                deleted++;
            }
            else if (!string.Equals(HashOf(full), entry.TargetHash, StringComparison.OrdinalIgnoreCase))
            {
                modified++;
            }
        }

        int created = CollectTarget(project).Files
            .Select(file => RelativePaths.Of(project.TargetPath, file))
            .Count(relative => !known.Contains(relative) && !group.IsGenerated(relative) && !IsProducedLegacy(project, relative));

        // Côté source : ce qui a changé depuis le dernier échange.
        int changed = 0;
        int added = 0;
        if (Directory.Exists(project.SourcePath))
        {
            foreach (string file in CollectSource(project).Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ExchangeEntry? entry = group.BySource(LineExemption.KeyFor(file, project.SourcePath));
                if (entry is null)
                {
                    added++;
                }
                else if (!string.Equals(HashOf(file), entry.SourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    changed++;
                }
            }
        }

        return new ExchangeStatus(true, modified, created, deleted, changed, added, history);
    }

    /// <summary>
    /// Calcule l'empreinte d'un fichier, ou la reprend si le fichier n'a pas
    /// changé de taille ni de date depuis le dernier calcul.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <returns>L'empreinte</returns>
    internal static string HashOf(string path)
    {
        FileInfo info = new(path);
        if (Hashes.TryGetValue(path, out (long Length, DateTime Written, string Hash) cached)
            && cached.Length == info.Length
            && cached.Written == info.LastWriteTimeUtc)
        {
            return cached.Hash;
        }

        string hash = ContentStore.HashOfFile(path);
        Hashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>
    /// Indique si deux contenus ne diffèrent que par leurs fins de ligne : un
    /// outil git qui convertit les fins de ligne n'a rien changé au fichier.
    /// </summary>
    /// <param name="left">Premier contenu</param>
    /// <param name="right">Second contenu</param>
    /// <returns><c>true</c> si les textes sont les mêmes, fins de ligne mises à part</returns>
    internal static bool SameIgnoringLineEnds(byte[] left, byte[] right)
    {
        string first = SourceDocument.Load(left).Text.ReplaceLineEndings("\n");
        string second = SourceDocument.Load(right).Text.ReplaceLineEndings("\n");
        return string.Equals(first, second, StringComparison.Ordinal);
    }

    /// <summary>
    /// Indique si un fichier de la cible est un ancien produit de l'outil copié
    /// à la main dans le dossier de l'IA : un fichier <c>_obfusc</c> ou une
    /// légende. Dans « out », ce sont au contraire les fichiers de l'échange.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="relative">Chemin relatif à la cible</param>
    /// <returns><c>true</c> pour un ancien produit</returns>
    private static bool IsProducedLegacy(ObfuscationProject project, string relative)
    {
        return project.IsTargetLinked && OutputNaming.IsProduced(Path.GetFileName(relative));
    }

    /// <summary>
    /// Donne le nom court d'un dossier, pour l'historique et les messages.
    /// </summary>
    /// <param name="folder">Chemin du dossier</param>
    /// <returns>Le nom du dossier</returns>
    internal static string FolderLabel(string folder)
    {
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    }

    /// <summary>
    /// Ajoute un chemin à une liste des réglages, s'il n'y est pas déjà, et
    /// enregistre.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="list">Liste à compléter</param>
    /// <param name="relative">Chemin relatif</param>
    private static void AddPath(ObfuscationProject project, IList<string> list, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        string normalized = TextFileCollector.NormalizeExclusion(relative);
        if (normalized.Length == 0)
        {
            return;
        }

        if (!list.Any(item => string.Equals(TextFileCollector.NormalizeExclusion(item), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(relative.EndsWith('/') ? normalized + "/" : normalized);
            project.SaveRules();
        }
    }

    /// <summary>
    /// Retire un chemin d'une liste des réglages, et enregistre.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="list">Liste à réduire</param>
    /// <param name="relative">Chemin relatif</param>
    private static void RemovePath(ObfuscationProject project, IList<string> list, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        string normalized = TextFileCollector.NormalizeExclusion(relative);
        int removed = 0;
        for (int index = list.Count - 1; index >= 0; index--)
        {
            if (string.Equals(TextFileCollector.NormalizeExclusion(list[index]), normalized, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(index);
                removed++;
            }
        }

        if (removed > 0)
        {
            project.SaveRules();
        }
    }
}
