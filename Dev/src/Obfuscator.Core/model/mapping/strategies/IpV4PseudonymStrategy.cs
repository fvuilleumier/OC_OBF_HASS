using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Mapping.Strategies;

/// <summary>
/// Remplace une adresse IPv4 par une autre adresse, en préservant la structure
/// de sous-réseau : une adresse privée par une adresse privée de la même plage,
/// une adresse publique par une adresse du réservoir 198.18.0.0/15.
///
/// Quatre décisions méritent d'être justifiées.
///
/// Une adresse privée reste privée. Le modèle raisonne sur la nature d'une
/// adresse autant que sur sa valeur — interne ou exposée, réseau domestique ou
/// plan d'entreprise —, et une adresse de 192.168.0.0/16 remplacée par une
/// adresse d'essai lui ferait conclure à tort qu'une machine est joignable
/// depuis Internet. Elle reçoit donc une adresse de sa propre plage : 10.x reste
/// 10.x, 192.168.x reste 192.168.x.
///
/// La préservation du préfixe ensuite : remplacer chaque adresse indépendamment
/// détruirait la relation « ces machines sont dans le même sous-réseau », et
/// tout raisonnement de routage ou de filtrage deviendrait faux. Un /24 source
/// est donc associé à un /24 de substitution, et la partie hôte est conservée
/// telle quelle : la passerelle en .1 le reste. Dans les plages plus larges
/// qu'un /16 — 10.0.0.0/8, 172.16.0.0/12, 100.64.0.0/10 —, le /16 est lui aussi
/// préservé : deux réseaux d'un même site restent dans un même /16, et un /16
/// garde son adresse de réseau (10.20.0.0 devient 10.57.0.0) et de diffusion.
///
/// Le choix des réseaux de substitution enfin. Un réseau privé de substitution
/// est un réseau privé comme un autre : s'il tombait sur un réseau réel du lot,
/// le contrôle final y verrait une fuite et la restitution ne saurait plus
/// lequel des deux elle lit. Les réseaux réels — ceux du coffre et ceux des
/// fichiers du lot, que la stratégie observe avant toute attribution — sont
/// donc évités, ainsi que les numéros qu'on choisit volontiers à la main (0, 1,
/// 10, 100, 168…), qui ont le plus de chances d'apparaître plus tard. Le tirage
/// suit la clé secrète du coffre et le rang de l'attribution, jamais la valeur
/// réelle : un pseudonyme ne doit pas être une fonction de la donnée qu'il
/// masque, et le nonce, visible dans chaque jeton, ne suffirait pas à le rendre
/// imprévisible.
///
/// Pour les adresses publiques, le réservoir de la RFC 2544 : les plages de
/// documentation habituelles (192.0.2.0/24 et suivantes) sont à la fois
/// minuscules et familières aux modèles de langage, qui les produisent
/// spontanément dans leurs exemples — le pire choix pour un pseudonyme qu'on
/// devra reconnaître au retour. 198.18.0.0/15 offre 131 072 adresses non
/// routables qu'aucun modèle n'écrit de lui-même.
///
/// Un début d'adresse masqué par l'utilisateur garde lui aussi la forme d'une
/// adresse. Trois octets — <c>192.168.1</c> — désignent un /24 et reçoivent les
/// trois premiers octets de son /24 de substitution, si bien que
/// <c>192.168.1.25</c> devient le même pseudonyme que l'adresse ait été masquée
/// entière ou par son début. Deux octets — <c>10.10</c> — désignent un /16 : dans
/// une plage privée plus large, ils reçoivent le /16 de substitution des
/// adresses qu'ils commencent ; ailleurs, le réservoir ne sait pas représenter
/// un /16, et ils reçoivent un préfixe de la plage 240.0.0.0/4, réservée et
/// jamais attribuée.
/// </summary>
public sealed partial class IpV4PseudonymStrategy : IPseudonymStrategy
{
    private const uint HostMask = 0x000000FF;
    private const uint NetworkMask = 0xFFFFFF00;
    private const uint WideMask = 0xFFFF0000;

    /// <summary>Nombre de sous-réseaux /24 disponibles dans le réservoir.</summary>
    private const uint AvailableNetworks = IpAddressTools.SubstitutionPoolSize / 256;

    /// <summary>
    /// Premiers octets des préfixes /16 de substitution, pris dans la plage
    /// réservée 240.0.0.0/4. Les valeurs par lesquelles commence un masque —
    /// 240, 248, 252, 254, 255 — en sont écartées : un masque comme
    /// <c>240.0.0.0</c> ne doit jamais être pris pour un pseudonyme à la
    /// restitution.
    /// </summary>
    private static readonly uint[] WideFirstOctets = [250, 251, 253];

    /// <summary>
    /// Octets que l'on choisit volontiers à la main pour numéroter un réseau :
    /// les petits nombres, les dizaines, les répétitions, et ceux des boîtiers
    /// les plus répandus (168, 178, 188). Un réseau réel qui n'est pas encore
    /// apparu a toutes les chances d'en porter un : la substitution les évite
    /// tant qu'elle le peut.
    /// </summary>
    private static readonly HashSet<int> CommonOctets =
    [
        .. Enumerable.Range(0, 21),
        .. Enumerable.Range(1, 25).Select(tens => tens * 10),
        33, 44, 55, 66, 77, 88, 99, 101, 102, 111, 128, 168, 172, 178, 188, 192, 222, 254, 255,
    ];

    // Réservoir public, et anciennes attributions d'adresses privées qui y ont été faites.
    private readonly Dictionary<uint, uint> _networkMap = [];
    private readonly Dictionary<uint, uint> _wideMap = [];
    private uint _nextNetwork;
    private int _nextWide;

    // Plages privées : substitutions attribuées, et réseaux réels à éviter.
    private readonly Dictionary<uint, uint> _privateWide = [];
    private readonly Dictionary<uint, uint> _privateNetworks = [];
    private readonly HashSet<uint> _takenWide = [];
    private readonly HashSet<uint> _takenNetworks = [];
    private readonly HashSet<uint> _realWide = [];
    private readonly HashSet<uint> _realNetworks = [];
    private readonly MappingStore _store;
    private readonly byte[] _key;

    /// <summary>
    /// Construit la stratégie en reconstituant les tables des sous-réseaux et
    /// des préfixes déjà attribués, afin qu'une adresse garde le même
    /// pseudonyme d'une exécution à l'autre.
    /// </summary>
    /// <param name="store">Coffre du projet, éventuellement déjà peuplé</param>
    public IpV4PseudonymStrategy(MappingStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _key = store.AllocationKey;

        foreach (MappingEntry entry in store.Entries)
        {
            if (entry.Category != SpanCategory.IpV4
                || !IpAddressTools.TryParseAddressOrPrefix(entry.Canonical, out uint source, out int octets)
                || !IpAddressTools.TryParseAddressOrPrefix(entry.Pseudonym, out uint target, out int written)
                || octets != written)
            {
                continue;
            }

            Reserve(source, octets);
            Record(source, target, octets);
        }
    }

    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.IpV4;

    /// <inheritdoc />
    public void Observe(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        foreach (Match match in AddressOrPrefix().Matches(sourceText))
        {
            if (IpAddressTools.TryParseAddressOrPrefix(match.Value, out uint value, out int octets))
            {
                Reserve(value, octets);
            }
        }
    }

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string? pseudonym = IpAddressTools.TryParseAddressOrPrefix(request.Canonical, out uint source, out int octets)
            ? AddressFor(source, octets)
            : null;

        // Plage épuisée : on bascule sur un jeton neutre plutôt que de
        // réutiliser une adresse, ce qui rendrait la restitution ambiguë. Un
        // pseudonyme d'adresse ne dépend pas non plus du numéro d'ordre : s'il
        // était déjà pris — un coffre retouché à la main —, le redemander
        // donnerait le même, alors que le jeton neutre change à chaque demande.
        return pseudonym is null || _store.IsPseudonymTaken(pseudonym)
            ? request.Format.Compose(SpanCategory.IpV4.FamilyCode(), request.Index)
            : pseudonym;
    }

    /// <summary>
    /// Compose l'adresse ou le début d'adresse de substitution.
    /// </summary>
    /// <param name="source">Adresse ou début d'adresse d'origine, aligné à gauche</param>
    /// <param name="octets">Nombre d'octets écrits, de 2 à 4</param>
    /// <returns>Le pseudonyme, ou <c>null</c> si la plage de substitution est épuisée</returns>
    private string? AddressFor(uint source, int octets)
    {
        if (octets == 2)
        {
            return WidePrefix(source);
        }

        if (!TryTargetNetwork(source & NetworkMask, out uint targetNetwork))
        {
            return null;
        }

        return octets == 3
            ? IpAddressTools.FormatPrefix(targetNetwork, 3)
            : IpAddressTools.Format(targetNetwork | (source & HostMask));
    }

    /// <summary>
    /// Donne le /24 de substitution associé à un /24 source, en l'attribuant
    /// au besoin.
    /// </summary>
    /// <param name="sourceNetwork">Sous-réseau /24 d'origine</param>
    /// <param name="targetNetwork">Sous-réseau /24 de substitution</param>
    /// <returns><c>false</c> si aucun réseau de substitution n'est disponible</returns>
    private bool TryTargetNetwork(uint sourceNetwork, out uint targetNetwork)
    {
        // Une attribution faite dans le réservoir — adresse publique, ou
        // adresse privée pseudonymisée par une version antérieure — est
        // conservée : un /24 garde son pseudonyme d'une exécution à l'autre.
        if (_networkMap.TryGetValue(sourceNetwork, out targetNetwork))
        {
            return true;
        }

        if (IpAddressTools.TryGetPrivateBlock(sourceNetwork, out uint block, out int prefixLength))
        {
            return TryPrivateNetwork(block, prefixLength, sourceNetwork, out targetNetwork);
        }

        if (_nextNetwork >= AvailableNetworks)
        {
            return false;
        }

        targetNetwork = IpAddressTools.SubstitutionPoolStart + (_nextNetwork << 8);
        _networkMap[sourceNetwork] = targetNetwork;
        _nextNetwork++;
        return true;
    }

    /// <summary>
    /// Donne le /24 privé de substitution d'un /24 privé, dans la même plage.
    ///
    /// Dans une plage plus large qu'un /16, le /16 est tiré d'abord, puis le
    /// troisième octet ; les troisièmes octets 0 et 255 sont gardés, pour que
    /// le /16 de substitution conserve son adresse de réseau et de diffusion —
    /// le /16, lui, change toujours. Dans 192.168.0.0/16, seul le troisième
    /// octet est tiré, et il change toujours.
    /// </summary>
    /// <param name="block">Première adresse de la plage privée</param>
    /// <param name="prefixLength">Longueur du préfixe de la plage</param>
    /// <param name="sourceNetwork">Sous-réseau /24 d'origine</param>
    /// <param name="targetNetwork">Sous-réseau /24 de substitution</param>
    /// <returns><c>false</c> si la plage est épuisée</returns>
    private bool TryPrivateNetwork(uint block, int prefixLength, uint sourceNetwork, out uint targetNetwork)
    {
        if (_privateNetworks.TryGetValue(sourceNetwork, out targetNetwork))
        {
            return true;
        }

        int third = (int)((sourceNetwork >> 8) & 0xFF);
        uint parent;
        int chosen;

        if (prefixLength < 16)
        {
            if (!TryPrivateWide(block, prefixLength, sourceNetwork & WideMask, out parent))
            {
                return false;
            }

            // Le tirage est rangé sous le /16 de substitution, et non sous le
            // /16 réel : rien de la valeur réelle n'entre dans le tirage.
            uint wide = parent;
            chosen = third is 0 or 255
                ? third
                : Pick(
                    string.Create(CultureInfo.InvariantCulture, $"net|{wide:X8}"),
                    _privateNetworks.Values.Count(target => (target & WideMask) == wide),
                    256,
                    candidate => candidate is not (0 or 255) && !_takenNetworks.Contains(wide | ((uint)candidate << 8)),
                    candidate => _realNetworks.Contains(wide | ((uint)candidate << 8)));
        }
        else
        {
            parent = block;
            chosen = Pick(
                string.Create(CultureInfo.InvariantCulture, $"net|{block:X8}"),
                _privateNetworks.Values.Count(target => (target & WideMask) == block),
                256,
                candidate => candidate != third && !_takenNetworks.Contains(block | ((uint)candidate << 8)),
                candidate => _realNetworks.Contains(block | ((uint)candidate << 8)));
        }

        if (chosen < 0)
        {
            return false;
        }

        targetNetwork = parent | ((uint)chosen << 8);
        _privateNetworks[sourceNetwork] = targetNetwork;
        _takenNetworks.Add(targetNetwork);
        return true;
    }

    /// <summary>
    /// Donne le /16 privé de substitution d'un /16 privé, dans une plage plus
    /// large qu'un /16. Il diffère toujours du /16 d'origine.
    /// </summary>
    /// <param name="block">Première adresse de la plage privée</param>
    /// <param name="prefixLength">Longueur du préfixe de la plage, inférieure à 16</param>
    /// <param name="sourceWide">/16 d'origine</param>
    /// <param name="targetWide">/16 de substitution</param>
    /// <returns><c>false</c> si la plage est épuisée</returns>
    private bool TryPrivateWide(uint block, int prefixLength, uint sourceWide, out uint targetWide)
    {
        if (_privateWide.TryGetValue(sourceWide, out targetWide))
        {
            return true;
        }

        int count = 1 << (16 - prefixLength);
        int sourceIndex = (int)((sourceWide - block) >> 16);

        int chosen = Pick(
            string.Create(CultureInfo.InvariantCulture, $"wide|{block:X8}"),
            _privateWide.Values.Count(target => (target & BlockMask(prefixLength)) == block),
            count,
            candidate => candidate != sourceIndex && !_takenWide.Contains(block + ((uint)candidate << 16)),
            candidate => _realWide.Contains(block + ((uint)candidate << 16)),
            candidate => (int)(((block + ((uint)candidate << 16)) >> 16) & 0xFF));

        if (chosen < 0)
        {
            return false;
        }

        targetWide = block + ((uint)chosen << 16);
        _privateWide[sourceWide] = targetWide;
        _takenWide.Add(targetWide);
        return true;
    }

    /// <summary>
    /// Tire une valeur parmi <paramref name="count"/>, en trois passes : hors
    /// réseaux réels et hors numéros usuels d'abord, hors réseaux réels ensuite,
    /// n'importe laquelle de libre enfin. Le point de départ dépend de la clé
    /// secrète du coffre, du niveau tiré et du rang de l'attribution à ce
    /// niveau ; il ne dépend jamais de la valeur réelle.
    /// </summary>
    /// <param name="level">Niveau tiré, qui sépare les tirages entre eux</param>
    /// <param name="rank">Nombre d'attributions déjà faites à ce niveau</param>
    /// <param name="count">Nombre de valeurs possibles</param>
    /// <param name="usable">Valeur libre et admissible</param>
    /// <param name="isReal">Valeur qui désigne un réseau réel</param>
    /// <param name="octetOf">Octet lisible de la valeur, pour écarter les numéros usuels ; la valeur elle-même par défaut</param>
    /// <returns>La valeur tirée, ou -1 s'il n'en reste aucune</returns>
    private int Pick(
        string level,
        int rank,
        int count,
        Func<int, bool> usable,
        Func<int, bool> isReal,
        Func<int, int>? octetOf = null)
    {
        octetOf ??= static candidate => candidate;
        int start = (int)(Draw(level, rank) % (uint)count);

        Func<int, bool>[] passes =
        [
            candidate => !isReal(candidate) && !CommonOctets.Contains(octetOf(candidate)),
            candidate => !isReal(candidate),
            static _ => true,
        ];

        foreach (Func<int, bool> pass in passes)
        {
            for (int step = 0; step < count; step++)
            {
                int candidate = (start + step) % count;
                if (usable(candidate) && pass(candidate))
                {
                    return candidate;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Tire un nombre pseudo-aléatoire, reproductible pour un même coffre.
    /// </summary>
    /// <param name="level">Niveau tiré</param>
    /// <param name="rank">Rang de l'attribution à ce niveau</param>
    /// <returns>Le nombre tiré</returns>
    private uint Draw(string level, int rank)
    {
        byte[] digest = HMACSHA256.HashData(
            _key,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Obfuscator/ipv4/{level}/{rank}")));

        return BinaryPrimitives.ReadUInt32LittleEndian(digest);
    }

    /// <summary>
    /// Donne le préfixe /16 de substitution d'un début d'adresse à deux
    /// octets, en l'attribuant au besoin.
    /// </summary>
    /// <param name="source">Début d'adresse d'origine, aligné à gauche</param>
    /// <returns>Le préfixe de substitution, ou <c>null</c> si la plage est épuisée</returns>
    private string? WidePrefix(uint source)
    {
        uint sourceWide = source & WideMask;

        // Dans une plage privée plus large qu'un /16, le début d'adresse reçoit
        // le /16 de substitution de toutes les adresses qu'il commence — sauf
        // s'il avait déjà reçu un préfixe de la plage réservée, qu'il garde.
        if (IpAddressTools.TryGetPrivateBlock(sourceWide, out uint block, out int prefixLength)
            && prefixLength < 16
            && !_wideMap.ContainsKey(sourceWide))
        {
            return TryPrivateWide(block, prefixLength, sourceWide, out uint privateTarget)
                ? IpAddressTools.FormatPrefix(privateTarget, 2)
                : null;
        }

        if (!_wideMap.TryGetValue(sourceWide, out uint target))
        {
            if (_nextWide >= WideFirstOctets.Length * 256)
            {
                return null;
            }

            target = (WideFirstOctets[_nextWide / 256] << 24) | ((uint)(_nextWide % 256) << 16);
            _wideMap[sourceWide] = target;
            _nextWide++;
        }

        return IpAddressTools.FormatPrefix(target, 2);
    }

    /// <summary>
    /// Retient un réseau réel, que les substitutions doivent éviter.
    /// </summary>
    /// <param name="value">Adresse ou début d'adresse, aligné à gauche</param>
    /// <param name="octets">Nombre d'octets écrits</param>
    private void Reserve(uint value, int octets)
    {
        if (!IpAddressTools.TryGetPrivateBlock(value, out _, out int prefixLength))
        {
            return;
        }

        if (prefixLength < 16)
        {
            _realWide.Add(value & WideMask);
        }

        if (octets >= 3)
        {
            _realNetworks.Add(value & NetworkMask);
        }
    }

    /// <summary>
    /// Reprend une attribution trouvée au coffre.
    /// </summary>
    /// <param name="source">Adresse ou début d'adresse d'origine</param>
    /// <param name="target">Pseudonyme attribué</param>
    /// <param name="octets">Nombre d'octets écrits, le même des deux côtés</param>
    private void Record(uint source, uint target, int octets)
    {
        bool sameBlock = IpAddressTools.TryGetPrivateBlock(source, out uint block, out int prefixLength)
            && IpAddressTools.TryGetPrivateBlock(target, out uint targetBlock, out _)
            && block == targetBlock;

        if (octets == 2)
        {
            if (Array.IndexOf(WideFirstOctets, target >> 24) >= 0)
            {
                RecordWide(source, target);
            }
            else if (sameBlock && prefixLength < 16)
            {
                RecordPrivateWide(source & WideMask, target & WideMask);
            }

            return;
        }

        // Adresse complète ou /24 : la même table, puisqu'un /24 masqué par son
        // début et ses adresses masquées entières doivent concorder.
        if ((target & NetworkMask) - IpAddressTools.SubstitutionPoolStart < IpAddressTools.SubstitutionPoolSize)
        {
            RecordNetwork(source, target);
            return;
        }

        if (sameBlock)
        {
            _privateNetworks[source & NetworkMask] = target & NetworkMask;
            _takenNetworks.Add(target & NetworkMask);

            if (prefixLength < 16)
            {
                RecordPrivateWide(source & WideMask, target & WideMask);
            }
        }
    }

    /// <summary>
    /// Reprend une attribution de /16 privé trouvée au coffre.
    /// </summary>
    /// <param name="sourceWide">/16 d'origine</param>
    /// <param name="targetWide">/16 de substitution</param>
    private void RecordPrivateWide(uint sourceWide, uint targetWide)
    {
        _privateWide.TryAdd(sourceWide, targetWide);
        _takenWide.Add(targetWide);
    }

    /// <summary>
    /// Reprend une attribution de /24 du réservoir trouvée au coffre.
    /// </summary>
    /// <param name="source">Adresse ou début d'adresse d'origine</param>
    /// <param name="target">Pseudonyme attribué</param>
    private void RecordNetwork(uint source, uint target)
    {
        _networkMap[source & NetworkMask] = target & NetworkMask;

        // Le compteur repart du sous-réseau le plus élevé déjà attribué, et non
        // du nombre d'entrées : la table peut comporter des trous si des
        // attributions ont été supprimées à la main.
        uint usedIndex = ((target & NetworkMask) - IpAddressTools.SubstitutionPoolStart) >> 8;
        if (usedIndex + 1 > _nextNetwork)
        {
            _nextNetwork = usedIndex + 1;
        }
    }

    /// <summary>
    /// Reprend une attribution de préfixe /16 de la plage réservée trouvée au
    /// coffre.
    /// </summary>
    /// <param name="source">Début d'adresse d'origine</param>
    /// <param name="target">Préfixe attribué</param>
    private void RecordWide(uint source, uint target)
    {
        int position = Array.IndexOf(WideFirstOctets, target >> 24);
        if (position < 0)
        {
            return;
        }

        _wideMap[source & WideMask] = target & WideMask;

        int usedIndex = (position * 256) + (int)((target >> 16) & 0xFF);
        if (usedIndex + 1 > _nextWide)
        {
            _nextWide = usedIndex + 1;
        }
    }

    /// <summary>
    /// Masque d'une plage, d'après la longueur de son préfixe.
    /// </summary>
    /// <param name="prefixLength">Longueur du préfixe</param>
    /// <returns>Le masque correspondant</returns>
    private static uint BlockMask(int prefixLength) => uint.MaxValue << (32 - prefixLength);

    /// <summary>
    /// Adresse ou début d'adresse de deux octets au moins, dans un texte
    /// quelconque. La recherche est large à dessein : retenir à tort un
    /// numéro de version comme réseau réel ne coûte qu'un réseau de moins à
    /// tirer.
    /// </summary>
    [GeneratedRegex(@"(?<![\d.])\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2}(?!\d)", RegexOptions.None, 2000)]
    private static partial Regex AddressOrPrefix();
}
