using System.Text.Json;
using System.Text.Json.Serialization;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Exchange;

/// <summary>
/// Fichier échangé entre la source et la cible, tel qu'il était au dernier
/// échange — obfuscation ou rapatriement.
///
/// Les deux empreintes sont la mémoire de l'échange : celle de la cible dit si
/// l'IA a touché au fichier depuis, celle de la source si le fichier en clair a
/// changé de son côté. C'est ce qui permet de ne jamais écraser un travail que
/// personne n'a encore vu, sans rien demander à git.
/// </summary>
public sealed class ExchangeEntry
{
    /// <summary>Clé du fichier dans la source : son chemin précédé d'une barre (<c>/packages/garage.yaml</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Chemin du fichier dans la cible, relatif, avec des barres obliques, nom pseudonymisé.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Empreinte du fichier en clair au dernier échange.</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    /// Empreinte du contenu que la cible avait au dernier échange. Ce contenu,
    /// pseudonymisé, est gardé sous cette empreinte : c'est la base de la
    /// fusion au rapatriement suivant.
    /// </summary>
    public string TargetHash { get; set; } = string.Empty;

    /// <summary>Date du dernier échange de ce fichier.</summary>
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>
/// Fichier que l'outil écrit lui-même dans la cible — <c>AGENTS.md</c>,
/// <c>CLAUDE.md</c>, une légende — et qui ne revient donc jamais vers la source.
/// </summary>
public sealed class GeneratedFile
{
    /// <summary>Chemin du fichier dans la cible, relatif, avec des barres obliques.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Empreinte du contenu écrit.</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>
/// Fichiers échangés entre une source et une cible données. Changer de source
/// pour une même cible ouvre un groupe vide : les fichiers déjà présents dans
/// la cible y sont inconnus, donc jamais écrasés sans le demander.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ExchangeGroup
{
    /// <summary>Dossier cible, en chemin absolu.</summary>
    public string TargetRoot { get; set; } = string.Empty;

    /// <summary>Dossier source, en chemin absolu.</summary>
    public string SourceRoot { get; set; } = string.Empty;

    /// <summary>Fichiers échangés.</summary>
    public IList<ExchangeEntry> Entries { get; } = [];

    /// <summary>Fichiers écrits par l'outil lui-même dans la cible.</summary>
    public IList<GeneratedFile> Generated { get; } = [];

    /// <summary>
    /// Retrouve un fichier par sa clé dans la source.
    /// </summary>
    /// <param name="source">Clé du fichier dans la source</param>
    /// <returns>L'entrée, ou <c>null</c></returns>
    public ExchangeEntry? BySource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Entries.FirstOrDefault(entry => string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Retrouve un fichier par son chemin dans la cible.
    /// </summary>
    /// <param name="target">Chemin relatif dans la cible</param>
    /// <returns>L'entrée, ou <c>null</c></returns>
    public ExchangeEntry? ByTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Entries.FirstOrDefault(entry => string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Indique si un chemin de la cible est un fichier que l'outil y a écrit
    /// lui-même.
    /// </summary>
    /// <param name="target">Chemin relatif dans la cible</param>
    /// <returns><c>true</c> pour un fichier généré</returns>
    public bool IsGenerated(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Generated.Any(file => string.Equals(file.Target, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Inscrit ou met à jour un fichier généré.
    /// </summary>
    /// <param name="target">Chemin relatif dans la cible</param>
    /// <param name="hash">Empreinte du contenu écrit</param>
    public void RecordGenerated(string target, string hash)
    {
        GeneratedFile? existing = Generated.FirstOrDefault(file => string.Equals(file.Target, target, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Generated.Add(new GeneratedFile { Target = target, Hash = hash });
            return;
        }

        existing.Hash = hash;
    }
}

/// <summary>
/// Trace d'un geste d'échange, pour l'historique affiché.
/// </summary>
public sealed class ExchangeEvent
{
    /// <summary>Date du geste.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Nature du geste : obfuscation ou rapatriement.</summary>
    public ExchangeGesture Gesture { get; set; }

    /// <summary>Nom du dossier écrit, pour l'affichage.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Fichiers créés.</summary>
    public int Created { get; set; }

    /// <summary>Fichiers modifiés.</summary>
    public int Modified { get; set; }

    /// <summary>Fichiers supprimés.</summary>
    public int Deleted { get; set; }

    /// <summary>Fichiers écrits avec des conflits à résoudre.</summary>
    public int Conflicts { get; set; }

    /// <summary>Alertes remontées.</summary>
    public int Warnings { get; set; }
}

/// <summary>Geste d'échange.</summary>
public enum ExchangeGesture
{
    /// <summary>Obfuscation de la source vers la cible.</summary>
    Obfuscation,

    /// <summary>Rapatriement de la cible vers la source.</summary>
    Return,
}

/// <summary>
/// Registre des échanges d'un projet : pour chaque couple source et cible, les
/// fichiers échangés et leurs empreintes au dernier échange, et l'historique
/// des gestes.
///
/// Il est chiffré comme le coffre : il associe des chemins en clair — un
/// fichier peut s'appeler <c>chambre_emma.yaml</c> — à leurs noms
/// pseudonymisés, ce qui est une correspondance au même titre que celles du
/// coffre.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ExchangeRegister
{
    /// <summary>Nombre de gestes gardés dans l'historique.</summary>
    private const int HistoryLength = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Version du format du registre.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Groupes de fichiers échangés, un par couple source et cible.</summary>
    public IList<ExchangeGroup> Groups { get; } = [];

    /// <summary>Derniers gestes, du plus ancien au plus récent.</summary>
    public IList<ExchangeEvent> History { get; } = [];

    /// <summary>
    /// Relit le registre ; un registre absent est vide.
    /// </summary>
    /// <param name="path">Chemin du registre</param>
    /// <param name="protector">Protection, qui relit le registre d'après son en-tête</param>
    /// <returns>Le registre</returns>
    /// <exception cref="InvalidDataException">Si le registre est illisible</exception>
    public static ExchangeRegister Load(string path, IMappingProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        if (!File.Exists(path))
        {
            return new ExchangeRegister();
        }

        byte[] plaintext = protector.Unprotect(File.ReadAllBytes(path));
        return JsonSerializer.Deserialize<ExchangeRegister>(plaintext, SerializerOptions)
            ?? throw new InvalidDataException($"Registre des échanges illisible : {path}");
    }

    /// <summary>
    /// Chiffre le registre et l'écrit d'un seul geste.
    /// </summary>
    /// <param name="path">Chemin du registre</param>
    /// <param name="protector">Protection à appliquer</param>
    public void Save(string path, IMappingProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        AtomicFile.WriteAllBytes(path, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions)));
    }

    /// <summary>
    /// Retrouve le groupe d'un couple source et cible.
    /// </summary>
    /// <param name="targetRoot">Dossier cible</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <returns>Le groupe, ou <c>null</c> s'il n'y a encore eu aucun échange</returns>
    public ExchangeGroup? FindGroup(string targetRoot, string sourceRoot)
    {
        string target = Normalize(targetRoot);
        string source = Normalize(sourceRoot);
        return Groups.FirstOrDefault(group =>
            string.Equals(Normalize(group.TargetRoot), target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize(group.SourceRoot), source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Donne le groupe d'un couple source et cible, en le créant au besoin.
    /// </summary>
    /// <param name="targetRoot">Dossier cible</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <returns>Le groupe</returns>
    public ExchangeGroup GroupFor(string targetRoot, string sourceRoot)
    {
        ExchangeGroup? group = FindGroup(targetRoot, sourceRoot);
        if (group is not null)
        {
            return group;
        }

        group = new ExchangeGroup { TargetRoot = Normalize(targetRoot), SourceRoot = Normalize(sourceRoot) };
        Groups.Add(group);
        return group;
    }

    /// <summary>
    /// Ajoute un geste à l'historique, en n'en gardant que les derniers.
    /// </summary>
    /// <param name="exchangeEvent">Geste accompli</param>
    public void Record(ExchangeEvent exchangeEvent)
    {
        ArgumentNullException.ThrowIfNull(exchangeEvent);

        History.Add(exchangeEvent);
        while (History.Count > HistoryLength)
        {
            History.RemoveAt(0);
        }
    }

    /// <summary>
    /// Donne les empreintes des contenus encore nécessaires : la base de
    /// chaque fichier échangé.
    /// </summary>
    /// <returns>Les empreintes</returns>
    public IEnumerable<string> ReferencedContents()
    {
        return Groups.SelectMany(group => group.Entries).Select(entry => entry.TargetHash).Where(hash => hash.Length > 0);
    }

    /// <summary>
    /// Met un chemin de dossier sous sa forme de comparaison : absolu, sans
    /// séparateur final.
    /// </summary>
    /// <param name="folder">Chemin du dossier</param>
    /// <returns>Le chemin normalisé</returns>
    private static string Normalize(string folder)
    {
        return string.IsNullOrWhiteSpace(folder)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }
}
