using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Mapping;

/// <summary>
/// Coffre du projet : il mémorise l'association entre valeurs réelles et
/// pseudonymes, et la rend stable dans le temps et entre fichiers.
///
/// Cette stabilité n'est pas un confort mais une condition du raisonnement de
/// l'IA : si le même serveur recevait quatre pseudonymes différents dans quatre
/// fichiers d'un même export, le modèle conclurait à quatre machines. Le coffre
/// est donc rechargé à chaque exécution et enrichi, jamais reconstruit.
/// </summary>
public sealed class MappingStore : IPseudonymRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Séparateur entre la catégorie et la valeur dans une clé d'attribution.
    /// Le caractère nul ne peut apparaître dans aucun des deux, ce qui évite
    /// qu'une valeur contenant le séparateur ne fabrique une collision de clé.
    /// </summary>
    private const char KeySeparator = '\0';

    /// <summary>Taille de la clé de tirage, en octets.</summary>
    private const int AllocationKeySize = 32;

    private readonly Dictionary<string, MappingEntry> _byCanonical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MappingEntry> _byPseudonym = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastIndexByFamily = new(StringComparer.Ordinal);
    private readonly byte[] _allocationKey;

    /// <summary>
    /// Construit un coffre vide.
    /// </summary>
    /// <param name="prefix">Préfixe des jetons du projet</param>
    /// <param name="nonce">Nonce du projet</param>
    /// <param name="createdAt">Date de création du coffre</param>
    /// <param name="allocationKey">Clé de tirage du coffre</param>
    private MappingStore(string prefix, string nonce, DateTimeOffset createdAt, byte[] allocationKey)
    {
        Prefix = prefix;
        Nonce = nonce;
        CreatedAt = createdAt;
        _allocationKey = allocationKey;
    }

    /// <summary>Préfixe des jetons émis par ce projet.</summary>
    public string Prefix { get; }

    /// <summary>Nonce identifiant ce projet dans ses jetons.</summary>
    public string Nonce { get; }

    /// <summary>Date de création du coffre.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Entrées du coffre, dans l'ordre d'insertion des clés.</summary>
    public IReadOnlyCollection<MappingEntry> Entries => _byCanonical.Values;

    /// <summary>
    /// Clé de tirage : tirée au sort à la création du coffre, et gardée
    /// chiffrée avec lui. Elle décide des pseudonymes qui ne sont pas de simples
    /// numéros d'ordre — le réseau privé de substitution d'un réseau privé —
    /// sans que personne puisse refaire le tirage : le nonce, lui, figure dans
    /// chaque jeton envoyé à l'IA, et un tirage qui n'en dépendrait que se
    /// rejouerait à l'envers.
    /// </summary>
    internal byte[] AllocationKey => [.. _allocationKey];

    /// <summary>
    /// Crée un coffre neuf pour un projet, en tirant au sort son nonce.
    /// </summary>
    /// <param name="prefix">Préfixe des jetons, <c>TBX</c> par défaut</param>
    /// <returns>Un coffre vide, prêt à recevoir des attributions</returns>
    public static MappingStore CreateNew(string prefix = TokenFormat.DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return new MappingStore(
            prefix.ToUpperInvariant(),
            TokenFormat.CreateNonce(),
            DateTimeOffset.UtcNow,
            RandomNumberGenerator.GetBytes(AllocationKeySize));
    }

    /// <summary>
    /// Produit une copie indépendante du coffre, entrées et compteurs compris.
    ///
    /// C'est ce qui permet à l'aperçu de tourner en arrière-plan sans risque :
    /// il attribue ses pseudonymes dans la copie, pendant que l'écran continue
    /// de modifier le projet et que le coffre réel peut être enregistré. Les
    /// attributions étant déterministes, la copie produit exactement les
    /// pseudonymes qu'attribuera l'obfuscation, tant que le coffre réel n'a pas
    /// changé entre-temps.
    /// </summary>
    /// <returns>Un coffre identique, sans lien avec l'original</returns>
    public MappingStore Clone()
    {
        MappingStore copy = new(Prefix, Nonce, CreatedAt, [.. _allocationKey]);
        Dictionary<MappingEntry, MappingEntry> copies = [];

        foreach (KeyValuePair<string, MappingEntry> pair in _byCanonical)
        {
            MappingEntry duplicate = pair.Value.Clone();
            copies[pair.Value] = duplicate;
            copy._byCanonical[pair.Key] = duplicate;
        }

        // Les deux index doivent désigner les mêmes instances copiées, faute de
        // quoi une variante notée par l'un serait ignorée de l'autre.
        foreach (KeyValuePair<string, MappingEntry> pair in _byPseudonym)
        {
            copy._byPseudonym[pair.Key] = copies.TryGetValue(pair.Value, out MappingEntry? duplicate)
                ? duplicate
                : pair.Value.Clone();
        }

        foreach (KeyValuePair<string, int> pair in _lastIndexByFamily)
        {
            copy._lastIndexByFamily[pair.Key] = pair.Value;
        }

        return copy;
    }

    /// <summary>
    /// Relève l'état du coffre, pour retrouver ensuite ce qu'un traitement y a
    /// ajouté (voir <see cref="DeltaSince"/>).
    /// </summary>
    /// <returns>Le relevé</returns>
    public VaultBaseline CaptureBaseline()
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (MappingEntry entry in _byCanonical.Values)
        {
            counts[entry.Pseudonym] = entry.Variants.Count;
        }

        return new VaultBaseline(counts);
    }

    /// <summary>
    /// Donne ce que le coffre a reçu depuis un relevé : les attributions
    /// nouvelles, copiées, et les graphies nouvelles des attributions qui
    /// existaient déjà. Les graphies s'ajoutent toujours à la fin de la liste
    /// d'une entrée : celles qui dépassent le compte relevé sont nouvelles.
    /// </summary>
    /// <param name="baseline">Relevé pris avant le traitement</param>
    /// <returns>Les ajouts</returns>
    public VaultDelta DeltaSince(VaultBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        List<MappingEntry> entries = [];
        List<(string Pseudonym, string Variant)> variants = [];

        foreach (MappingEntry entry in _byCanonical.Values)
        {
            if (!baseline.TryGetVariantCount(entry.Pseudonym, out int known))
            {
                entries.Add(entry.Clone());
                continue;
            }

            for (int index = known; index < entry.Variants.Count; index++)
            {
                variants.Add((entry.Pseudonym, entry.Variants[index]));
            }
        }

        return new VaultDelta(entries, variants);
    }

    /// <summary>
    /// Recherche l'entrée déjà attribuée à une valeur.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <param name="canonical">Forme canonique de la valeur</param>
    /// <param name="entry">Entrée trouvée, le cas échéant</param>
    /// <returns><c>true</c> si la valeur avait déjà reçu un pseudonyme</returns>
    public bool TryGetByValue(SpanCategory category, string canonical, out MappingEntry entry)
    {
        return _byCanonical.TryGetValue(ValueKey(category, canonical), out entry!);
    }

    /// <summary>
    /// Recherche l'entrée correspondant à un pseudonyme, pour la désobfuscation.
    /// </summary>
    /// <param name="pseudonym">Pseudonyme rencontré dans un texte</param>
    /// <param name="entry">Entrée trouvée, le cas échéant</param>
    /// <returns><c>true</c> si le pseudonyme est connu de ce coffre</returns>
    public bool TryGetByPseudonym(string pseudonym, out MappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(pseudonym);
        return _byPseudonym.TryGetValue(pseudonym, out entry!);
    }

    /// <summary>
    /// Réserve le prochain numéro d'ordre disponible dans la famille d'une
    /// catégorie. Les compteurs sont dérivés des entrées existantes au
    /// chargement, ce qui évite qu'un compteur persisté se désynchronise du
    /// contenu réel du coffre.
    /// </summary>
    /// <param name="category">Catégorie dont on veut le prochain numéro</param>
    /// <returns>Le numéro d'ordre à utiliser, à partir de 1</returns>
    public int ReserveIndex(SpanCategory category)
    {
        string family = category.FamilyCode();
        int next = (_lastIndexByFamily.TryGetValue(family, out int last) ? last : 0) + 1;
        _lastIndexByFamily[family] = next;
        return next;
    }

    /// <summary>
    /// Enregistre une nouvelle association valeur / pseudonyme.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <param name="canonical">Forme canonique de la valeur</param>
    /// <param name="original">Graphie observée dans le fichier source</param>
    /// <param name="index">Numéro d'ordre attribué dans la famille</param>
    /// <param name="pseudonym">Pseudonyme substitué dans les sorties</param>
    /// <returns>L'entrée créée</returns>
    /// <exception cref="InvalidOperationException">Si le pseudonyme est déjà attribué à une autre valeur</exception>
    public MappingEntry Add(SpanCategory category, string canonical, string original, int index, string pseudonym)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonical);
        ArgumentException.ThrowIfNullOrEmpty(pseudonym);

        // Deux valeurs réelles partageant un pseudonyme rendraient la
        // désobfuscation ambiguë : c'est un défaut de la fabrique de
        // pseudonymes, pas une situation à contourner silencieusement.
        if (_byPseudonym.ContainsKey(pseudonym))
        {
            throw new InvalidOperationException(
                $"Le pseudonyme « {pseudonym} » est déjà attribué à une autre valeur.");
        }

        MappingEntry entry = new()
        {
            Pseudonym = pseudonym,
            Category = category,
            Canonical = canonical,
            Original = original,
            Index = index,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _byCanonical[ValueKey(category, canonical)] = entry;
        _byPseudonym[pseudonym] = entry;
        TrackIndex(category, index);
        return entry;
    }

    /// <summary>
    /// Indique si un pseudonyme est déjà utilisé, quelle que soit sa catégorie.
    /// </summary>
    /// <param name="pseudonym">Pseudonyme à tester</param>
    /// <returns><c>true</c> si le pseudonyme est déjà attribué</returns>
    public bool IsPseudonymTaken(string pseudonym)
    {
        ArgumentNullException.ThrowIfNull(pseudonym);
        return _byPseudonym.ContainsKey(pseudonym);
    }

    /// <inheritdoc />
    public bool IsPseudonym(string value) => IsPseudonymTaken(value);

    /// <summary>
    /// Sérialise puis protège le coffre, et l'écrit sur disque.
    ///
    /// Le coffre est écrit à côté, puis mis en place d'un seul geste : un
    /// coffre à moitié écrit — coupure, disque plein — perdrait toutes les
    /// correspondances, et avec elles toute possibilité de restaurer les
    /// réponses déjà reçues. Le texte en clair n'est jamais écrit.
    /// </summary>
    /// <param name="path">Chemin du fichier de coffre</param>
    /// <param name="protector">Protection à appliquer avant écriture</param>
    public void Save(string path, IMappingProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        VaultDocument document = new()
        {
            Prefix = Prefix,
            Nonce = Nonce,
            CreatedAt = CreatedAt,
            AllocationKey = Convert.ToBase64String(_allocationKey),
        };

        foreach (MappingEntry entry in _byCanonical.Values)
        {
            document.Entries.Add(entry);
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        AtomicFile.WriteAllBytes(path, protector.Protect(plaintext));
    }

    /// <summary>
    /// Lit un coffre depuis le disque et le déprotège.
    /// </summary>
    /// <param name="path">Chemin du fichier de coffre</param>
    /// <param name="protector">Protection utilisée à l'écriture</param>
    /// <returns>Le coffre rechargé, compteurs reconstruits</returns>
    /// <exception cref="InvalidDataException">Si le contenu du coffre est illisible</exception>
    public static MappingStore Load(string path, IMappingProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        byte[] plaintext = protector.Unprotect(File.ReadAllBytes(path));
        VaultDocument? document = JsonSerializer.Deserialize<VaultDocument>(plaintext, SerializerOptions);
        if (document is null)
        {
            throw new InvalidDataException($"Coffre illisible : {path}");
        }

        MappingStore store = new(document.Prefix, document.Nonce, document.CreatedAt, ReadAllocationKey(document.AllocationKey));
        foreach (MappingEntry entry in document.Entries)
        {
            store._byCanonical[ValueKey(entry.Category, entry.Canonical)] = entry;
            store._byPseudonym[entry.Pseudonym] = entry;
            store.TrackIndex(entry.Category, entry.Index);
        }

        return store;
    }

    /// <summary>
    /// Relit la clé de tirage d'un coffre. Un coffre écrit avant qu'elle
    /// n'existe en reçoit une neuve, enregistrée avec lui à la prochaine
    /// écriture : ses attributions existantes ne dépendent pas d'elle.
    /// </summary>
    /// <param name="encoded">Clé lue dans le coffre, en base 64</param>
    /// <returns>La clé</returns>
    private static byte[] ReadAllocationKey(string? encoded)
    {
        if (!string.IsNullOrEmpty(encoded))
        {
            try
            {
                byte[] key = Convert.FromBase64String(encoded);
                if (key.Length == AllocationKeySize)
                {
                    return key;
                }
            }
            catch (FormatException)
            {
                // Clé illisible : on en tire une neuve, comme pour un coffre
                // qui n'en avait pas.
            }
        }

        return RandomNumberGenerator.GetBytes(AllocationKeySize);
    }

    /// <summary>
    /// Met à jour le compteur d'une famille si le numéro fourni le dépasse.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <param name="index">Numéro d'ordre observé</param>
    private void TrackIndex(SpanCategory category, int index)
    {
        string family = category.FamilyCode();
        if (!_lastIndexByFamily.TryGetValue(family, out int last) || index > last)
        {
            _lastIndexByFamily[family] = index;
        }
    }

    /// <summary>
    /// Compose la clé d'attribution d'une valeur. La catégorie fait partie de la
    /// clé : la chaîne <c>backup</c> peut être à la fois un nom d'hôte et un nom
    /// de projet, et les deux méritent des pseudonymes distincts.
    /// </summary>
    /// <param name="category">Nature de la valeur</param>
    /// <param name="canonical">Forme canonique de la valeur</param>
    /// <returns>La clé du dictionnaire d'attribution</returns>
    private static string ValueKey(SpanCategory category, string canonical)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{category}{KeySeparator}{canonical}");
    }

    /// <summary>
    /// Forme sérialisée du coffre. Séparée du modèle en mémoire pour que le
    /// format sur disque puisse évoluer (champ <c>Version</c>) sans contraindre
    /// les structures internes.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    private sealed class VaultDocument
    {
        /// <summary>Version du format de coffre.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Préfixe des jetons du projet.</summary>
        public string Prefix { get; set; } = TokenFormat.DefaultPrefix;

        /// <summary>Nonce du projet.</summary>
        public string Nonce { get; set; } = string.Empty;

        /// <summary>Date de création du coffre.</summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>Clé de tirage, en base 64 ; absente des coffres antérieurs.</summary>
        public string AllocationKey { get; set; } = string.Empty;

        /// <summary>Entrées du coffre.</summary>
        public IList<MappingEntry> Entries { get; } = [];
    }
}
