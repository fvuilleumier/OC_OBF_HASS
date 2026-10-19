using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TebiObfuscator.Core.Model.Detectors;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Mapping.Strategies;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Projet d'obfuscation : un dossier de travail qui réunit les réglages, le
/// vocabulaire, le coffre et les fichiers.
///
/// Le projet existe pour une raison précise : rendre les attributions stables
/// sur l'ensemble d'un lot. Si le même serveur recevait quatre pseudonymes
/// différents dans quatre fichiers d'un même export, le modèle conclurait à
/// quatre machines et son analyse serait fausse. Tout ce qui est partagé entre
/// fichiers — coffre, réservoir d'adresses, vocabulaire, listes — vit donc ici.
/// </summary>
public sealed class ObfuscationProject
{
    /// <summary>Nom du dossier qui abrite les données du projet.</summary>
    public const string MetadataFolderName = ".tebiobf";

    private readonly VaultProtectors _protectors;

    /// <summary>
    /// Construit un projet à partir de ses composants déjà chargés.
    /// </summary>
    /// <param name="rootPath">Dossier racine du projet</param>
    /// <param name="settings">Réglages du projet</param>
    /// <param name="store">Coffre du projet</param>
    /// <param name="vocabulary">Vocabulaire de l'organisation</param>
    /// <param name="protectors">Chiffrements possibles du coffre</param>
    private ObfuscationProject(
        string rootPath,
        ProjectSettings settings,
        MappingStore store,
        Vocabulary vocabulary,
        VaultProtectors protectors)
    {
        RootPath = rootPath;
        Settings = settings;
        Store = store;
        Vocabulary = vocabulary;
        _protectors = protectors;
        StoredProtection = settings.VaultProtection;
        Format = new TokenFormat(store.Prefix, store.Nonce);
        Policy = BuildPolicy(settings);
        Policy.Pseudonyms = store;
    }

    /// <summary>Dossier racine du projet.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Nom d'affichage du projet : celui que l'utilisateur a choisi, ou à
    /// défaut le nom du dossier. Le dossier reste l'identité réelle du projet ;
    /// le nom n'est qu'une étiquette, qu'on peut donc changer sans rien casser.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Settings.Name)
        ? Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : Settings.Name;

    /// <summary>Réglages persistés.</summary>
    public ProjectSettings Settings { get; }

    /// <summary>Coffre des attributions.</summary>
    public MappingStore Store { get; }

    /// <summary>
    /// Chiffrement sous lequel le coffre est enregistré sur disque, lu dans son
    /// en-tête à l'ouverture. Il ne diffère du réglage que tant que le coffre
    /// n'a pas été réécrit : un projet antérieur au choix du chiffrement a un
    /// coffre en chiffrement élevé, alors que le réglage par défaut est bas.
    /// </summary>
    public VaultProtection StoredProtection { get; private set; }

    /// <summary>Vocabulaire de l'organisation.</summary>
    public Vocabulary Vocabulary { get; }

    /// <summary>Format des jetons du projet.</summary>
    public TokenFormat Format { get; }

    /// <summary>Politique dérivée des réglages.</summary>
    public ObfuscationPolicy Policy { get; private set; }

    /// <summary>Dossier des données du projet.</summary>
    public string MetadataPath => Path.Combine(RootPath, MetadataFolderName);

    /// <summary>Chemin du fichier de réglages.</summary>
    public string SettingsPath => Path.Combine(MetadataPath, "project.jsonc");

    /// <summary>Chemin du coffre protégé.</summary>
    public string MappingPath => Path.Combine(MetadataPath, "mapping.enc");

    /// <summary>Chemin du fichier de vocabulaire.</summary>
    public string VocabularyPath => Path.Combine(MetadataPath, "vocabulary.txt");

    /// <summary>Chemin du dernier relevé de détection.</summary>
    public string FindingsPath => Path.Combine(MetadataPath, "findings.jsonc");

    /// <summary>Dossier des fichiers à obfusquer.</summary>
    public string InputPath => Path.Combine(RootPath, "in");

    /// <summary>Dossier des fichiers obfusqués et de leurs légendes.</summary>
    public string OutputPath => Path.Combine(RootPath, "out");

    /// <summary>Vrai si le projet lit ses fichiers dans un dossier en clair lié, et non dans « in ».</summary>
    public bool IsSourceLinked => !string.IsNullOrWhiteSpace(Settings.Exchange.ClearFolder);

    /// <summary>Vrai si le projet écrit ses fichiers obfusqués dans le dossier de l'IA, et non dans « out ».</summary>
    public bool IsTargetLinked => !string.IsNullOrWhiteSpace(Settings.Exchange.AiFolder);

    /// <summary>
    /// Dossier d'où partent les fichiers à obfusquer, et où reviennent les
    /// fichiers rapatriés : le dossier en clair lié, sinon « in ». Un dossier
    /// lié introuvable n'est pas remplacé par « in » : c'est une erreur, que
    /// l'appelant doit dire.
    /// </summary>
    public string SourcePath => IsSourceLinked ? Path.GetFullPath(Settings.Exchange.ClearFolder) : InputPath;

    /// <summary>
    /// Dossier où vont les fichiers obfusqués, et d'où reviennent ceux que
    /// l'IA a modifiés : le dossier de l'IA lié, sinon « out ».
    /// </summary>
    public string TargetPath => IsTargetLinked ? Path.GetFullPath(Settings.Exchange.AiFolder) : OutputPath;

    /// <summary>Chemin du registre des échanges, chiffré comme le coffre.</summary>
    public string ExchangeRegisterPath => Path.Combine(MetadataPath, "exchange.enc");

    /// <summary>
    /// Dossier des contenus reçus par la cible, pseudonymisés, rangés sous
    /// leur empreinte : la base des fusions du rapatriement.
    /// </summary>
    public string ExchangeContentPath => Path.Combine(MetadataPath, "exchange");

    /// <summary>
    /// Protection à appliquer au registre des échanges : celle du coffre, dans
    /// le chiffrement que demandent les réglages. Il associe des noms de
    /// fichiers en clair à leurs pseudonymes, et mérite donc la même protection.
    /// La relecture suit l'en-tête du fichier, quel que soit le réglage.
    /// </summary>
    internal IMappingProtector ExchangeProtector => _protectors.For(Settings.VaultProtection);

    /// <summary>
    /// Crée un projet neuf sur disque.
    /// </summary>
    /// <param name="rootPath">Dossier racine à initialiser</param>
    /// <param name="protectors">Chiffrements possibles du coffre</param>
    /// <param name="profile">Profil de départ</param>
    /// <param name="protection">Chiffrement du coffre, bas par défaut</param>
    /// <returns>Le projet créé et enregistré</returns>
    public static ObfuscationProject Create(
        string rootPath,
        VaultProtectors protectors,
        string profile = "network",
        VaultProtection protection = VaultProtection.Low)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(protectors);

        // Un chemin relatif serait résolu, à chaque usage, depuis le dossier
        // courant du processus — qui n'est pas le même selon qu'on lance
        // l'application depuis l'explorateur ou depuis Visual Studio. Il est
        // donc fixé une fois pour toutes.
        rootPath = Path.GetFullPath(rootPath);

        ProjectSettings settings = new() { Profile = profile, VaultProtection = protection };
        ApplyProfileDefaults(settings);
        settings.ApplyEntityDefaults();

        MappingStore store = MappingStore.CreateNew(settings.TokenPrefix);
        ObfuscationProject project = new(rootPath, settings, store, new Vocabulary(), protectors);

        Directory.CreateDirectory(project.MetadataPath);
        Directory.CreateDirectory(project.InputPath);
        Directory.CreateDirectory(project.OutputPath);
        project.Save();

        return project;
    }

    /// <summary>
    /// Ouvre un projet existant. Le coffre est relu quel que soit son
    /// chiffrement : c'est son en-tête qui le dit, pas le réglage du projet.
    /// </summary>
    /// <param name="rootPath">Dossier racine du projet</param>
    /// <param name="protectors">Chiffrements possibles du coffre</param>
    /// <returns>Le projet chargé</returns>
    /// <exception cref="DirectoryNotFoundException">Si le dossier n'abrite pas de projet</exception>
    public static ObfuscationProject Open(string rootPath, VaultProtectors protectors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(protectors);

        rootPath = Path.GetFullPath(rootPath);
        string metadataPath = Path.Combine(rootPath, MetadataFolderName);
        if (!Directory.Exists(metadataPath))
        {
            throw new DirectoryNotFoundException(
                $"Aucun projet TebiObfuscator dans « {rootPath} ». Lancez d'abord la commande d'initialisation.");
        }

        ProjectSettings settings = ProjectSettings.Load(Path.Combine(metadataPath, "project.jsonc"));
        string mappingPath = Path.Combine(metadataPath, "mapping.enc");

        MappingStore store;
        VaultProtection stored = settings.VaultProtection;
        if (File.Exists(mappingPath))
        {
            stored = VaultProtectors.Identify(mappingPath);
            store = MappingStore.Load(mappingPath, protectors.For(stored));
        }
        else
        {
            store = MappingStore.CreateNew(settings.TokenPrefix);
        }

        Vocabulary vocabulary = Vocabulary.Load(Path.Combine(metadataPath, "vocabulary.txt"));
        return new ObfuscationProject(rootPath, settings, store, vocabulary, protectors)
        {
            StoredProtection = stored,
        };
    }

    /// <summary>
    /// Vrai pour un instantané : une copie détachée du projet, destinée à une
    /// analyse en arrière-plan, qu'il est interdit d'enregistrer.
    /// </summary>
    public bool IsSnapshot { get; private init; }

    /// <summary>
    /// Produit un instantané du projet : réglages, vocabulaire et coffre
    /// recopiés, sans lien avec l'original.
    ///
    /// Une analyse en arrière-plan ne peut pas travailler sur le projet vivant.
    /// L'écran continue de le modifier pendant qu'elle tourne — un terme
    /// ajouté, une catégorie décochée — et l'aperçu attribue des pseudonymes :
    /// sur des objets partagés, le premier cas fausserait l'analyse en cours de
    /// route, le second écrirait dans le coffre réel depuis un autre fil. Sur
    /// une copie prise au lancement, l'analyse voit un état cohérent du début à
    /// la fin, et le projet vivant n'est jamais touché.
    /// </summary>
    /// <returns>L'instantané, qu'on ne peut pas enregistrer</returns>
    public ObfuscationProject CreateSnapshot()
    {
        return new ObfuscationProject(RootPath, Settings.Clone(), Store.Clone(), Vocabulary.Clone(), _protectors)
        {
            IsSnapshot = true,
            StoredProtection = StoredProtection,
        };
    }

    /// <summary>
    /// Enregistre réglages, vocabulaire et coffre, ce dernier dans le
    /// chiffrement que demandent les réglages.
    /// </summary>
    /// <exception cref="InvalidOperationException">Sur un instantané</exception>
    public void Save()
    {
        SaveRules();
        SaveVault();
    }

    /// <summary>
    /// Enregistre les réglages et le vocabulaire, sans réécrire le coffre.
    ///
    /// C'est l'enregistrement qui suit une décision de revue : aucune ne touche
    /// au coffre, que seule l'obfuscation enrichit — et elle l'enregistre
    /// elle-même. Le réécrire à chaque case cochée coûterait un chiffrement
    /// complet pour rien.
    /// </summary>
    /// <exception cref="InvalidOperationException">Sur un instantané</exception>
    public void SaveRules()
    {
        EnsureWritable();

        Directory.CreateDirectory(MetadataPath);
        Settings.Save(SettingsPath);
        Vocabulary.Save(VocabularyPath);
    }

    /// <summary>
    /// Change le chiffrement du coffre, et le rechiffre aussitôt : le réglage
    /// doit dire l'état du fichier, pas une intention qui n'attendrait que la
    /// prochaine obfuscation.
    ///
    /// Le coffre est réécrit avant les réglages, parce que c'est lui qui
    /// compte. Si sa réécriture échoue, le réglage est rétabli et rien n'a
    /// changé ; si c'est ensuite l'enregistrement des réglages qui échoue, le
    /// coffre est déjà dans le nouveau chiffrement — il se relit de toute façon
    /// d'après son en-tête, et les deux se rejoignent au prochain enregistrement.
    /// </summary>
    /// <param name="protection">Chiffrement voulu</param>
    /// <exception cref="InvalidOperationException">Sur un instantané</exception>
    public void ChangeVaultProtection(VaultProtection protection)
    {
        EnsureWritable();

        VaultProtection previous = Settings.VaultProtection;
        Settings.VaultProtection = protection;
        try
        {
            SaveVault();
        }
        catch
        {
            Settings.VaultProtection = previous;
            throw;
        }

        SaveRules();

        // Le registre des échanges suit le coffre. S'il ne pouvait pas être
        // réécrit, il resterait lisible dans l'ancien chiffrement, que dit son
        // en-tête, et passerait au nouveau à sa prochaine écriture.
        if (File.Exists(ExchangeRegisterPath))
        {
            ExchangeRegister.Load(ExchangeRegisterPath, ExchangeProtector).Save(ExchangeRegisterPath, ExchangeProtector);
        }
    }

    /// <summary>
    /// Rechiffre le coffre dans le chiffrement des réglages, s'il est encore
    /// enregistré sous un autre — typiquement, le coffre en chiffrement élevé
    /// d'un projet antérieur au choix du chiffrement.
    /// </summary>
    /// <returns><c>true</c> si le coffre a été rechiffré</returns>
    /// <exception cref="InvalidOperationException">Sur un instantané</exception>
    public bool AlignVaultProtection()
    {
        EnsureWritable();

        if (StoredProtection == Settings.VaultProtection || !File.Exists(MappingPath))
        {
            return false;
        }

        SaveVault();
        return true;
    }

    /// <summary>
    /// Enregistre le coffre dans le chiffrement que demandent les réglages.
    /// </summary>
    private void SaveVault()
    {
        EnsureWritable();

        Directory.CreateDirectory(MetadataPath);
        Store.Save(MappingPath, _protectors.For(Settings.VaultProtection));
        StoredProtection = Settings.VaultProtection;
    }

    /// <summary>
    /// Refuse toute écriture depuis un instantané. Il porte des attributions
    /// d'aperçu que l'obfuscation n'a jamais écrites : l'enregistrer écraserait
    /// le coffre réel par une copie qui n'est pas la sienne.
    /// </summary>
    /// <exception cref="InvalidOperationException">Sur un instantané</exception>
    private void EnsureWritable()
    {
        if (IsSnapshot)
        {
            throw new InvalidOperationException("Un instantané de projet ne s'enregistre pas.");
        }
    }

    /// <summary>
    /// Calcule l'empreinte des règles qui décident de ce qui sera masqué.
    ///
    /// L'écran s'en sert pour savoir si son aperçu est encore d'actualité :
    /// deux empreintes égales garantissent que l'analyse produirait le même
    /// résultat, et un retour en arrière — un terme ajouté puis retiré — rend
    /// l'aperçu de nouveau à jour sans rien relancer. Les exceptions ponctuelles
    /// et les propositions écartées n'y figurent pas : l'écran les reflète
    /// directement, sans nouvelle analyse.
    /// </summary>
    /// <returns>L'empreinte, en hexadécimal</returns>
    public string RulesFingerprint()
    {
        StringBuilder canonical = new();
        canonical.Append("privateIp=").Append(Settings.PrivateIp.ToString()).Append('\n');

        AppendSorted(canonical, "disabled", Settings.DisabledCategories);
        AppendSorted(canonical, "keep", Settings.AllowKeep);
        AppendSorted(canonical, "always", Settings.AlwaysObfuscate);
        AppendSorted(canonical, "entity", Settings.KeptEntityDomains);
        AppendSorted(canonical, "term", Vocabulary.Terms.Select(term =>
            string.Create(CultureInfo.InvariantCulture, $"{term.Text}=>{term.Category}")));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// Ajoute une liste triée à la forme canonique des règles.
    /// </summary>
    /// <param name="canonical">Forme canonique en cours de composition</param>
    /// <param name="label">Nature des éléments</param>
    /// <param name="items">Éléments à ajouter</param>
    private static void AppendSorted(StringBuilder canonical, string label, IEnumerable<string> items)
    {
        foreach (string item in items.Order(StringComparer.Ordinal))
        {
            canonical.Append(label).Append(':').Append(item).Append('\n');
        }
    }

    /// <summary>
    /// Indique si une valeur est de celles qu'on a explicitement demandé de
    /// masquer : terme du vocabulaire, à l'accent près, ou valeur de la liste
    /// noire. Un identifiant d'entité gardé en clair ne la protège pas.
    /// </summary>
    /// <param name="value">Valeur examinée</param>
    /// <returns><c>true</c> si la valeur est demandée explicitement</returns>
    public bool IsExplicitTerm(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string trimmed = value.Trim();
        string folded = TextFolding.Fold(trimmed);
        return Settings.AlwaysObfuscate.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || Vocabulary.Terms.Any(term => string.Equals(TextFolding.Fold(term.Text), folded, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reconstruit la politique à partir des réglages, après une modification
    /// venue de l'écran de revue.
    /// </summary>
    public void RefreshPolicy()
    {
        Policy = BuildPolicy(Settings);
        Policy.Pseudonyms = Store;
    }

    /// <summary>
    /// Construit le jeu de détecteurs activés pour ce projet.
    /// </summary>
    /// <returns>Les détecteurs, du plus spécifique au plus général</returns>
    public IReadOnlyList<IDetector> CreateDetectors()
    {
        return
        [
            new StructuralSecretDetector(),
            new SecretDetector(),
            new SensitiveKeyDetector(),
            new PemBlockDetector(),
            new IdentityDetector(),
            new NetworkDetector(),
            new VocabularyDetector(Vocabulary),
        ];
    }

    /// <summary>
    /// Construit un moteur d'obfuscation lié au coffre du projet.
    /// </summary>
    /// <returns>Le moteur prêt à traiter un lot de fichiers</returns>
    public ObfuscationEngine CreateObfuscationEngine()
    {
        PseudonymFactory factory = new(Store, Format, CreateStrategies());
        return new ObfuscationEngine(Format, Store, factory, CreateDetectors(), Policy);
    }

    /// <summary>
    /// Construit un moteur de restitution lié au coffre du projet.
    /// </summary>
    /// <returns>Le moteur prêt à restituer un texte</returns>
    public DeobfuscationEngine CreateDeobfuscationEngine()
    {
        return new DeobfuscationEngine(Format, Store);
    }

    /// <summary>
    /// Construit les stratégies de pseudonymes format-préservants.
    /// </summary>
    /// <returns>Les stratégies, consultées avant la stratégie neutre</returns>
    private IEnumerable<IPseudonymStrategy> CreateStrategies()
    {
        return
        [
            new IpV4PseudonymStrategy(Store),
            new IpV6PseudonymStrategy(),
            new MacPseudonymStrategy(),
            new PortPseudonymStrategy(),
            new EmailPseudonymStrategy(),
            new DomainPseudonymStrategy(),
            new GuidPseudonymStrategy(),
            new IdentifierPseudonymStrategy(),
            new CoordinatePseudonymStrategy(Store),
            new PemPseudonymStrategy(),
        ];
    }

    /// <summary>
    /// Traduit les réglages en politique exécutable.
    /// </summary>
    /// <param name="settings">Réglages du projet</param>
    /// <returns>La politique correspondante</returns>
    private static ObfuscationPolicy BuildPolicy(ProjectSettings settings)
    {
        ObfuscationPolicy policy = new()
        {
            PrivateIp = settings.PrivateIp,
            Entities = new EntityIdentifiers(settings.KeptEntityDomains),
        };

        foreach (string name in settings.DisabledCategories)
        {
            if (Enum.TryParse(name, ignoreCase: true, out SpanCategory category))
            {
                policy.Disable(category);
            }
        }

        foreach (string term in settings.AllowKeep)
        {
            policy.Allowlist.Add(term);
        }

        foreach (string term in settings.AlwaysObfuscate)
        {
            policy.AlwaysObfuscate(term);
        }

        foreach (string entry in settings.LineExceptions)
        {
            if (LineExemption.TryParse(entry, out LineExemption exception))
            {
                policy.Except(exception);
            }
        }

        foreach (string term in settings.IgnoredSuggestions)
        {
            policy.Ignore(term);
        }

        return policy;
    }

    /// <summary>
    /// Applique les valeurs par défaut du profil choisi.
    ///
    /// Les profils ne font que présélectionner des réglages usuels. Les
    /// adresses privées sont remplacées quel que soit le profil, par d'autres
    /// adresses privées de la même plage : dans une configuration réseau, le
    /// plan d'adressage reste exploitable — sous-réseaux et parties hôtes sont
    /// préservés — sans dire celui de l'organisation.
    /// </summary>
    /// <param name="settings">Réglages à compléter</param>
    private static void ApplyProfileDefaults(ProjectSettings settings)
    {
        // Les identifiants globaux sont masqués d'office : un GUID de coffre,
        // de locataire ou d'application désigne une installation précise.
        settings.SettingsVersion = ProjectSettings.CurrentVersion;
        settings.PrivateIp = PrivateIpPolicy.Pseudonymize;

        if (settings.Profile == "appconfig")
        {
            settings.DisabledCategories.Add(nameof(SpanCategory.PhoneNumber));
        }
    }
}
