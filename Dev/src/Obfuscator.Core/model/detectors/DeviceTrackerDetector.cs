using System.Text.RegularExpressions;
using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Detectors;

/// <summary>
/// Pseudonymise le nom d'un traceur d'appareil Home Assistant :
/// <c>device_tracker.galaxy_tab_a8</c> devient <c>device_tracker.TBXA7_ID_0001</c>.
///
/// Contrairement aux capteurs ou aux interrupteurs, dont le nom décrit une
/// fonction de la maison et reste en clair (voir <see cref="Obfuscator.Core.Model.Policy.EntityIdentifiers"/>),
/// un traceur porte le nom d'un appareil personnel — un téléphone, une
/// tablette, souvent avec le prénom de son propriétaire. Le domaine reste
/// lisible, pour que l'IA sache qu'elle regarde un traceur ; seul ce qui suit
/// le point est remplacé, d'un seul tenant.
/// </summary>
public sealed partial class DeviceTrackerDetector : IDetector
{
    /// <summary>Identifiant de la règle, dans les rapports.</summary>
    public const string DeviceTrackerRuleId = "identity.deviceTracker";

    /// <summary>
    /// Priorité du nom d'appareil. Elle dépasse celle de l'identifiant repéré
    /// sous la clé <c>entity_id</c> — qui masquerait sinon le domaine avec le
    /// nom — et celle des termes du vocabulaire : un prénom contenu dans le nom
    /// (<c>iphone_emma</c>) ne doit pas laisser le reste du nom en clair autour
    /// de son jeton. Elle reste sous celle d'une adresse de courrier et des
    /// secrets, qu'un nom d'entité ne peut de toute façon pas contenir.
    /// </summary>
    private const int Priority = 790;

    private const int TimeoutMilliseconds = 2000;

    /// <summary>
    /// Entité de traceur : le domaine, collé à aucune lettre, aucun chiffre,
    /// aucun souligné ni trait d'union (« my_device_tracker.x » n'en est pas
    /// une), puis le nom de l'objet. Un point avant reste permis, pour les
    /// modèles (« states.device_tracker.galaxy_tab_a8.state »).
    /// </summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_\-])device_tracker\.(?<name>[\p{L}\p{N}_]+)",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex DeviceTracker();

    /// <inheritdoc />
    public string RuleId => DeviceTrackerRuleId;

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(context.Text);
    }

    /// <summary>
    /// Produit un span par nom de traceur trouvé.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <returns>Les noms d'appareils, sans leur domaine</returns>
    private static IEnumerable<DetectedSpan> Enumerate(string text)
    {
        foreach (Match match in DeviceTracker().Matches(text))
        {
            Group name = match.Groups["name"];
            yield return new DetectedSpan(
                name.Index,
                name.Length,
                SpanCategory.Identifier,
                name.Value,
                Priority,
                1.0,
                DeviceTrackerRuleId);
        }
    }
}
