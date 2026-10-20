using System.Text.Json;
using System.Text.Json.Serialization;
using Obfuscator.Core.Model.Mapping;

namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Réglages persistés d'un projet d'obfuscation.
///
/// C'est le fichier que pilote la grille à cocher de l'interface, et qu'on peut
/// aussi modifier à la main : il est écrit avec des commentaires d'en-tête et
/// relu en tolérant les commentaires, précisément pour rester lisible sans
/// l'application.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ProjectSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>En-tête de commentaires écrit en tête du fichier.</summary>
    private const string FileHeader = """
        // Réglages du projet Obfuscator.
        // Ce fichier est modifiable à la main : les commentaires sont conservés
        // à la lecture, mais réécrits par l'application lorsqu'elle enregistre.
        //
        //   profile            : network | appconfig | notes | custom
        //   privateIp          : Pseudonymize (défaut : une adresse privée devient une autre adresse
        //                        privée de la même plage) | Keep (conservées telles quelles)
        //   disabledCategories : catégories à ne plus masquer
        //   allowKeep          : liste blanche du projet, valeurs gardées en clair partout,
        //                        en plus de la liste livrée
        //   alwaysObfuscate    : valeurs à toujours masquer ; l'emporte sur allowKeep
        //   lineExceptions     : « fichier|ligne#rang|valeur » gardés en clair à un endroit précis ;
        //                        sans rang, toutes les occurrences de la valeur sur la ligne
        //   ignoredSuggestions : ancienne forme de la liste blanche, reprise dans allowKeep
        //   keptEntityDomains  : domaines Home Assistant dont les identifiants d'entités restent en clair
        //                        (« sensor.salon_temperature »), sauf les termes à masquer qu'ils contiennent
        //   vaultProtection    : Low (défaut, chiffrement bas : l'application ouvre le coffre sur tout poste)
        //                        | High (chiffrement élevé : seul ce compte Windows, sur ce poste)
        //   exchange           : dossiers liés au projet, chacun facultatif
        //     clearFolder      :   dossier en clair, source de l'obfuscation ; vide pour « in »
        //     aiFolder         :   dossier où travaille l'IA, cible de l'obfuscation ; vide pour « out »
        //     excluded         :   chemins de la source tenus à l'écart (« dossier/ » pour un dossier)
        //     ignored          :   chemins de la cible que le rapatriement ignore
        //     writeClaudeMd    :   écrire aussi un CLAUDE.md qui renvoie à AGENTS.md
        //   settingsVersion    : version du format, tenue par l'application

        """;

    /// <summary>
    /// Version actuelle du format des réglages. La version 2 masque les GUID
    /// d'office : jusque-là, ils étaient désactivés à la création du projet. La
    /// version 3 remplace les adresses privées au lieu de les conserver, et
    /// réunit les propositions écartées dans la liste blanche du projet. La
    /// version 4 garde en clair les identifiants d'entités Home Assistant.
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>
    /// Nom d'affichage du projet. Vide, le nom du dossier fait office.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Version du format des réglages, pour les mettre à jour une seule fois.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>Profil de départ, qui préétablit les réglages.</summary>
    public string Profile { get; set; } = "network";

    /// <summary>Préfixe des jetons du projet.</summary>
    public string TokenPrefix { get; set; } = "TBX";

    /// <summary>Traitement des adresses IPv4 privées.</summary>
    public PrivateIpPolicy PrivateIp { get; set; } = PrivateIpPolicy.Pseudonymize;

    /// <summary>Catégories désactivées, sous forme de libellés.</summary>
    public IList<string> DisabledCategories { get; } = [];

    /// <summary>
    /// Liste blanche du projet : valeurs gardées en clair dans toutes leurs
    /// occurrences, dans tous les fichiers. Elle s'ajoute à la liste livrée.
    /// </summary>
    public IList<string> AllowKeep { get; } = [];

    /// <summary>Valeurs toujours masquées, quelle que soit la liste blanche.</summary>
    public IList<string> AlwaysObfuscate { get; } = [];

    /// <summary>
    /// Occurrences laissées en clair à un endroit précis, sous la forme
    /// « fichier|ligne#rang|valeur » (voir <see cref="LineExemption"/>).
    /// </summary>
    public IList<string> LineExceptions { get; } = [];

    /// <summary>
    /// Suggestions écartées, ancienne forme de la liste blanche : la mise à
    /// jour des réglages les reprend dans <see cref="AllowKeep"/>. La liste
    /// reste lue pour qu'un fichier modifié à la main ne perde rien.
    /// </summary>
    public IList<string> IgnoredSuggestions { get; } = [];

    /// <summary>
    /// Domaines Home Assistant dont les identifiants d'entités restent en clair,
    /// sauf les termes explicitement masqués qu'ils contiennent (voir
    /// <see cref="EntityIdentifiers"/>). Vide, aucun n'est gardé à ce titre.
    /// </summary>
    public IList<string> KeptEntityDomains { get; } = [];

    /// <summary>
    /// Ce que la mise à jour des réglages a changé à l'ouverture, en clair,
    /// pour le dire une fois à l'utilisateur. Rien n'en est enregistré.
    /// </summary>
    [JsonIgnore]
    public IList<string> UpgradeNotes { get; } = [];

    /// <summary>
    /// Chiffrement du coffre. Bas par défaut : c'est ce qui permet d'emporter
    /// un projet sur un autre poste où l'application est installée. Un projet
    /// écrit avant ce réglage n'en porte pas : il passe donc au chiffrement bas,
    /// et son coffre, jusque-là en chiffrement élevé, est rechiffré en
    /// conséquence.
    /// </summary>
    public VaultProtection VaultProtection { get; set; } = VaultProtection.Low;

    /// <summary>Produire un fichier de légende à côté de chaque sortie.</summary>
    public bool EmitLegend { get; set; } = true;

    /// <summary>Produire un rapport de résidus après chaque obfuscation.</summary>
    public bool EmitResidualReport { get; set; } = true;

    /// <summary>
    /// Dossiers liés au projet : le dossier en clair d'où partent les fichiers,
    /// et le dossier où l'IA les reçoit. Les chemins ne sont écrits qu'ici.
    /// </summary>
    public ExchangeSettings Exchange { get; } = new();

    /// <summary>
    /// Lit les réglages depuis un fichier, en tolérant commentaires et virgules
    /// finales.
    /// </summary>
    /// <param name="path">Chemin du fichier de réglages</param>
    /// <returns>Les réglages lus, ou les valeurs par défaut si le fichier est absent</returns>
    public static ProjectSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            ProjectSettings defaults = new() { SettingsVersion = CurrentVersion };
            defaults.ApplyEntityDefaults();
            return defaults;
        }

        string content = File.ReadAllText(path);
        ProjectSettings settings = JsonSerializer.Deserialize<ProjectSettings>(content, SerializerOptions) ?? new ProjectSettings();
        settings.Upgrade();
        return settings;
    }

    /// <summary>
    /// Met à jour des réglages écrits par une version antérieure. La mise à
    /// jour n'a lieu qu'une fois : un choix fait ensuite par l'utilisateur —
    /// décocher de nouveau les GUID, revenir à des adresses privées conservées
    /// — est respecté.
    /// </summary>
    private void Upgrade()
    {
        if (SettingsVersion < 2)
        {
            // La désactivation des GUID était un réglage posé d'office à la
            // création du projet, pas un choix de l'utilisateur.
            for (int index = DisabledCategories.Count - 1; index >= 0; index--)
            {
                if (string.Equals(DisabledCategories[index], "GlobalIdentifier", StringComparison.OrdinalIgnoreCase))
                {
                    DisabledCategories.RemoveAt(index);
                }
            }
        }

        if (SettingsVersion < 3)
        {
            UpgradeToVersion3();
        }

        // Une liste vidée à la main après la mise à jour est respectée : seuls
        // les réglages antérieurs reçoivent les domaines par défaut.
        if (SettingsVersion < 4 && KeptEntityDomains.Count == 0)
        {
            ApplyEntityDefaults();
            UpgradeNotes.Add("Les identifiants d'entités Home Assistant (sensor., binary_sensor., update., number., switch., button.) restent désormais en clair, sauf les termes à masquer qu'ils contiennent ; « keptEntityDomains » dans project.jsonc règle la liste.");
        }

        SettingsVersion = CurrentVersion;
    }

    /// <summary>
    /// Pose les domaines d'entités gardés d'office, s'il n'y en a aucun.
    /// </summary>
    public void ApplyEntityDefaults()
    {
        if (KeptEntityDomains.Count == 0)
        {
            CopyInto([.. EntityIdentifiers.DefaultDomains], KeptEntityDomains);
        }
    }

    /// <summary>
    /// Passage à la version 3. Les adresses privées conservées l'étaient par le
    /// profil choisi à la création, jamais par un choix exprimé : elles sont
    /// désormais remplacées par d'autres adresses privées. Et les propositions
    /// écartées — « gardées » — rejoignent la liste blanche, qui dit la même
    /// chose : cette valeur reste en clair partout.
    /// </summary>
    private void UpgradeToVersion3()
    {
        if (PrivateIp == PrivateIpPolicy.Keep)
        {
            PrivateIp = PrivateIpPolicy.Pseudonymize;
            UpgradeNotes.Add("Les adresses IPv4 privées sont désormais remplacées par d'autres adresses privées de la même plage ; « privateIp »: « Keep » dans project.jsonc les conserve.");
        }

        int moved = 0;
        foreach (string term in IgnoredSuggestions)
        {
            string trimmed = term.Trim();
            if (trimmed.Length > 0 && !AllowKeep.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                AllowKeep.Add(trimmed);
                moved++;
            }
        }

        IgnoredSuggestions.Clear();
        if (moved > 0)
        {
            UpgradeNotes.Add(string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"{moved} proposition(s) gardée(s) en clair rejoignent la liste blanche du projet."));
        }
    }

    /// <summary>
    /// Produit une copie indépendante des réglages.
    /// </summary>
    /// <returns>Des réglages identiques, qu'aucune modification ultérieure de l'original n'atteint</returns>
    public ProjectSettings Clone()
    {
        ProjectSettings copy = new()
        {
            Name = Name,
            SettingsVersion = SettingsVersion,
            Profile = Profile,
            TokenPrefix = TokenPrefix,
            PrivateIp = PrivateIp,
            VaultProtection = VaultProtection,
            EmitLegend = EmitLegend,
            EmitResidualReport = EmitResidualReport,
        };

        CopyInto(DisabledCategories, copy.DisabledCategories);
        CopyInto(AllowKeep, copy.AllowKeep);
        CopyInto(AlwaysObfuscate, copy.AlwaysObfuscate);
        CopyInto(LineExceptions, copy.LineExceptions);
        CopyInto(IgnoredSuggestions, copy.IgnoredSuggestions);
        CopyInto(KeptEntityDomains, copy.KeptEntityDomains);

        // Sans les liens, un instantané lirait « in » à la place du dossier en
        // clair : l'aperçu ne montrerait pas les fichiers qui partiront.
        Exchange.CopyTo(copy.Exchange);

        return copy;
    }

    /// <summary>
    /// Écrit les réglages, précédés de leur en-tête explicatif. Le fichier est
    /// écrit à côté puis mis en place d'un seul geste : une coupure ne laisse
    /// jamais des réglages à moitié écrits.
    /// </summary>
    /// <param name="path">Chemin du fichier de réglages</param>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AtomicFile.WriteAllText(path, FileHeader + JsonSerializer.Serialize(this, SerializerOptions) + Environment.NewLine);
    }

    /// <summary>
    /// Recopie les éléments d'une liste dans une autre.
    /// </summary>
    /// <param name="source">Liste d'origine</param>
    /// <param name="target">Liste à remplir</param>
    private static void CopyInto(IList<string> source, IList<string> target)
    {
        foreach (string item in source)
        {
            target.Add(item);
        }
    }
}
