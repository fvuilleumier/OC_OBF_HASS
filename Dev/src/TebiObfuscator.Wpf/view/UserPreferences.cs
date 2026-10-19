using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Projet ouvert récemment.
/// </summary>
/// <param name="Path">Dossier du projet</param>
/// <param name="Name">Nom d'affichage au moment de la dernière ouverture</param>
/// <param name="OpenedAt">Date de la dernière ouverture</param>
public sealed record RecentProject(string Path, string Name, DateTimeOffset OpenedAt);

/// <summary>
/// Projet récent tel que l'écran d'accueil et le menu le présentent.
/// </summary>
/// <param name="Project">Projet concerné</param>
/// <param name="Rank">Rang dans la liste, à partir de 1</param>
public sealed record RecentProjectItem(RecentProject Project, int Rank)
{
    /// <summary>Nom du projet.</summary>
    public string Name => Project.Name;

    /// <summary>Dossier du projet.</summary>
    public string Path => Project.Path;

    /// <summary>Vrai si le dossier existe encore.</summary>
    public bool Exists => Directory.Exists(Project.Path);

    /// <summary>Raccourci clavier qui le rouvre.</summary>
    public string Shortcut => string.Create(CultureInfo.InvariantCulture, $"Ctrl+{Rank}");

    /// <summary>Précision affichée sous le nom : dossier et date, ou absence du dossier.</summary>
    public string Detail => Exists
        ? string.Create(CultureInfo.CurrentCulture, $"{Project.Path} · ouvert le {Project.OpenedAt.LocalDateTime:d MMMM à HH:mm}")
        : string.Create(CultureInfo.CurrentCulture, $"introuvable · {Project.Path}");
}

/// <summary>
/// Préférences de l'utilisateur, propres à ce poste : les projets récents et
/// le mode d'analyse.
///
/// Elles vivent hors des projets, dans le profil local de l'utilisateur : les
/// chemins de dossiers n'ont de sens que sur cette machine, et le coffre d'un
/// projet n'est de toute façon lisible que par ce compte Windows. Aucune valeur
/// sensible n'y figure — des chemins et des noms de projet, rien d'autre.
///
/// Un fichier illisible ou absent ne doit jamais empêcher l'application de
/// démarrer : les préférences retombent alors sur leurs valeurs par défaut.
/// </summary>
internal sealed class UserPreferences
{
    /// <summary>Nombre de projets récents retenus, et accessibles par raccourci.</summary>
    public const int RecentLimit = 5;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Projets récents, du plus récent au plus ancien.</summary>
    public List<RecentProject> RecentProjects { get; set; } = [];

    /// <summary>
    /// Relancer l'analyse en arrière-plan après chaque modification — terme,
    /// catégorie, valeur, proposition masquée ou gardée, occurrence exceptée —
    /// plutôt que d'attendre un clic sur « Analyser ». Activé par défaut :
    /// l'analyse ne bloque rien, et l'aperçu reste ainsi toujours exact.
    /// </summary>
    public bool AnalyseAfterEachChange { get; set; } = true;

    /// <summary>
    /// Projet dont le coffre sert au presse-papiers depuis l'accueil : son
    /// dossier, une chaîne vide pour le projet « Presse-papiers » par défaut,
    /// ou <c>null</c> tant que l'utilisateur n'a rien choisi — le projet le
    /// plus récent est alors retenu.
    /// </summary>
    public string? ClipboardProject { get; set; }

    /// <summary>
    /// Aperçus laissés ouverts en onglet, par dossier de projet : les chemins
    /// des fichiers, relatifs au dossier « in ». Un onglet reste ouvert
    /// jusqu'à ce qu'on le ferme, y compris d'une séance à l'autre.
    /// </summary>
    public Dictionary<string, List<string>> OpenPreviews { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chemin du fichier de préférences.</summary>
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TebiObfuscator",
        "preferences.json");

    /// <summary>
    /// Lit les préférences, ou retourne les valeurs par défaut si elles sont
    /// absentes ou illisibles.
    /// </summary>
    /// <returns>Les préférences</returns>
    public static UserPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UserPreferences();
            }

            UserPreferences? loaded = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath), SerializerOptions);
            if (loaded is null)
            {
                return new UserPreferences();
            }

            loaded.RecentProjects = [.. loaded.RecentProjects
                .Where(project => !string.IsNullOrWhiteSpace(project.Path))
                .Take(RecentLimit)];

            // La lecture crée un dictionnaire sensible à la casse : on le
            // recompose, puisqu'un même dossier s'écrit de plusieurs façons.
            Dictionary<string, List<string>> previews = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string project, List<string> files) in loaded.OpenPreviews ?? [])
            {
                if (!string.IsNullOrWhiteSpace(project) && files is not null)
                {
                    previews[Normalize(project)] = files;
                }
            }

            loaded.OpenPreviews = previews;
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserPreferences();
        }
    }

    /// <summary>
    /// Enregistre les préférences. Un échec est sans conséquence sur la session
    /// et n'est donc pas signalé comme une erreur.
    /// </summary>
    /// <returns><c>true</c> si l'enregistrement a réussi</returns>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Place un projet en tête des projets récents.
    /// </summary>
    /// <param name="path">Dossier du projet</param>
    /// <param name="name">Nom d'affichage</param>
    public void Remember(string path, string name)
    {
        string normalized = Normalize(path);
        RecentProjects.RemoveAll(project => SamePath(project.Path, normalized));
        RecentProjects.Insert(0, new RecentProject(normalized, name, DateTimeOffset.Now));

        if (RecentProjects.Count > RecentLimit)
        {
            RecentProjects.RemoveRange(RecentLimit, RecentProjects.Count - RecentLimit);
        }
    }

    /// <summary>
    /// Met à jour le nom retenu pour un projet récent, après un renommage.
    /// </summary>
    /// <param name="path">Dossier du projet</param>
    /// <param name="name">Nouveau nom d'affichage</param>
    public void Rename(string path, string name)
    {
        int index = RecentProjects.FindIndex(project => SamePath(project.Path, path));
        if (index >= 0)
        {
            RecentProjects[index] = RecentProjects[index] with { Name = name };
        }
    }

    /// <summary>
    /// Retire un projet des projets récents.
    /// </summary>
    /// <param name="path">Dossier du projet</param>
    public void Forget(string path)
    {
        RecentProjects.RemoveAll(project => SamePath(project.Path, path));
    }

    /// <summary>
    /// Donne les aperçus laissés ouverts dans un projet.
    /// </summary>
    /// <param name="path">Dossier du projet</param>
    /// <returns>Les chemins des fichiers, relatifs au dossier « in »</returns>
    public List<string> OpenPreviewsOf(string path)
    {
        return OpenPreviews.TryGetValue(Normalize(path), out List<string>? files) ? [.. files] : [];
    }

    /// <summary>
    /// Retient les aperçus ouverts d'un projet. Seuls ceux des projets récents
    /// sont gardés : un projet sorti de la liste n'a plus à encombrer le
    /// fichier.
    /// </summary>
    /// <param name="path">Dossier du projet</param>
    /// <param name="files">Chemins des fichiers, relatifs au dossier « in »</param>
    public void SetOpenPreviews(string path, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        string normalized = Normalize(path);
        List<string> kept = [.. files];
        if (kept.Count == 0)
        {
            OpenPreviews.Remove(normalized);
        }
        else
        {
            OpenPreviews[normalized] = kept;
        }

        foreach (string project in OpenPreviews.Keys.Where(project => !SamePath(project, normalized)
            && !RecentProjects.Exists(recent => SamePath(recent.Path, project))).ToList())
        {
            OpenPreviews.Remove(project);
        }
    }

    /// <summary>
    /// Présente les projets récents, numérotés pour leurs raccourcis.
    /// </summary>
    /// <returns>Les projets récents, du plus récent au plus ancien</returns>
    public List<RecentProjectItem> RecentItems()
    {
        return [.. RecentProjects.Take(RecentLimit).Select((project, index) => new RecentProjectItem(project, index + 1))];
    }

    /// <summary>
    /// Normalise un chemin de dossier, pour qu'un même projet ne figure pas
    /// deux fois sous deux écritures.
    /// </summary>
    /// <param name="path">Chemin à normaliser</param>
    /// <returns>Le chemin complet, sans séparateur final</returns>
    private static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Compare deux chemins de dossier comme le fait le système de fichiers.
    /// </summary>
    /// <param name="left">Premier chemin</param>
    /// <param name="right">Second chemin</param>
    /// <returns><c>true</c> s'ils désignent le même dossier</returns>
    private static bool SamePath(string left, string right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
