using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Règle de détection fondée sur une expression régulière.
/// </summary>
/// <param name="RuleId">Identifiant stable de la règle</param>
/// <param name="Category">Catégorie attribuée aux valeurs trouvées</param>
/// <param name="Pattern">Expression régulière à appliquer</param>
/// <param name="ValueGroup">
/// Groupe de capture délimitant la valeur à masquer. Le groupe 0 couvre toute
/// la correspondance ; un groupe nommé permet de ne masquer que la valeur d'une
/// paire clé/valeur, en laissant la clé lisible pour le modèle.
/// </param>
/// <param name="Confidence">Confiance de la règle, dans l'intervalle [0, 1]</param>
/// <param name="Captures">
/// Sous-captures à masquer séparément, pour les valeurs composites. Lorsque
/// cette liste est fournie, elle remplace <paramref name="ValueGroup"/>.
/// </param>
public sealed record RegexRule(
    string RuleId,
    SpanCategory Category,
    Regex Pattern,
    string ValueGroup = "0",
    double Confidence = 1.0,
    IReadOnlyList<RegexCapture>? Captures = null);

/// <summary>
/// Sous-capture d'une règle composite.
///
/// Les valeurs composites — URL, chemins UNC, chaînes de connexion — ne doivent
/// surtout pas être remplacées d'un bloc : le schéma, le port et le chemin
/// portent du contexte technique utile au modèle. La règle décrit donc
/// plusieurs sous-captures et ne masque que les composants identifiants.
/// </summary>
/// <param name="Group">Nom du groupe de capture</param>
/// <param name="Category">Catégorie attribuée à ce composant</param>
/// <param name="RuleSuffix">Suffixe ajouté à l'identifiant de règle, pour la traçabilité</param>
public sealed record RegexCapture(string Group, SpanCategory Category, string RuleSuffix);

/// <summary>
/// Détecteur générique appliquant un jeu de règles régulières au texte source.
///
/// Toutes les expressions sont compilées avec un délai maximal : les fichiers
/// analysés ne sont pas maîtrisés, et une entrée pathologique ne doit pas
/// pouvoir bloquer l'outil sur un retour sur trace catastrophique.
/// </summary>
/// <param name="ruleId">Nom de la famille de règles, pour les rapports</param>
/// <param name="rules">Règles à appliquer</param>
public class RegexRuleDetector(string ruleId, IEnumerable<RegexRule> rules) : IDetector
{
    private readonly List<RegexRule> _rules = [.. rules];

    /// <inheritdoc />
    public string RuleId { get; } = ruleId;

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        // La validation est séparée de l'itérateur pour qu'un appelant reçoive
        // l'exception au moment de l'appel, et non au premier parcours.
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(context);
    }

    /// <summary>
    /// Applique chaque règle au texte et produit les spans correspondants.
    /// </summary>
    /// <param name="context">Contexte d'analyse validé</param>
    /// <returns>Les spans repérés</returns>
    private IEnumerable<DetectedSpan> Enumerate(DetectionContext context)
    {
        return _rules
            .Where(rule => !context.IsName || AppliesToName(rule))
            .SelectMany(rule => ApplyRule(rule, context.Text));
    }

    /// <summary>
    /// Point d'extension permettant à un détecteur dérivé d'écarter une règle
    /// quand le texte est un nom de fichier ou de dossier.
    /// </summary>
    /// <param name="rule">Règle examinée</param>
    /// <returns><c>true</c> si la règle s'applique aussi à un nom</returns>
    protected virtual bool AppliesToName(RegexRule rule) => true;

    /// <summary>
    /// Applique une règle au texte et produit les spans correspondants.
    /// </summary>
    /// <param name="rule">Règle à appliquer</param>
    /// <param name="text">Texte source</param>
    /// <returns>Les spans repérés par cette règle</returns>
    private IEnumerable<DetectedSpan> ApplyRule(RegexRule rule, string text)
    {
        foreach (Match match in rule.Pattern.Matches(text))
        {
            IEnumerable<DetectedSpan> spans = rule.Captures is { Count: > 0 }
                ? ExtractComponents(rule, match)
                : ExtractWholeValue(rule, match);

            foreach (DetectedSpan span in spans)
            {
                yield return span;
            }
        }
    }

    /// <summary>
    /// Extrait les composants sensibles d'une valeur composite.
    /// </summary>
    /// <param name="rule">Règle composite</param>
    /// <param name="match">Correspondance trouvée</param>
    /// <returns>Un span par composant retenu</returns>
    private IEnumerable<DetectedSpan> ExtractComponents(RegexRule rule, Match match)
    {
        foreach (RegexCapture capture in rule.Captures!)
        {
            Group component = match.Groups[capture.Group];
            if (component.Success && component.Length > 0 && Accept(rule, component.Value))
            {
                yield return DetectedSpan.Create(
                    component.Index,
                    component.Length,
                    capture.Category,
                    component.Value,
                    $"{rule.RuleId}.{capture.RuleSuffix}",
                    rule.Confidence);
            }
        }
    }

    /// <summary>
    /// Extrait la valeur unique désignée par la règle.
    /// </summary>
    /// <param name="rule">Règle simple</param>
    /// <param name="match">Correspondance trouvée</param>
    /// <returns>Le span correspondant, ou aucun si la valeur est rejetée</returns>
    private IEnumerable<DetectedSpan> ExtractWholeValue(RegexRule rule, Match match)
    {
        Group group = match.Groups[rule.ValueGroup];
        if (!group.Success || group.Length == 0 || !Accept(rule, group.Value))
        {
            yield break;
        }

        yield return DetectedSpan.Create(
            group.Index,
            group.Length,
            rule.Category,
            group.Value,
            rule.RuleId,
            rule.Confidence);
    }

    /// <summary>
    /// Point d'extension permettant à un détecteur dérivé de rejeter une
    /// correspondance après coup — validation par somme de contrôle, valeur
    /// manifestement factice, etc.
    /// </summary>
    /// <param name="rule">Règle ayant produit la correspondance</param>
    /// <param name="value">Valeur capturée</param>
    /// <returns><c>true</c> si la correspondance doit être retenue</returns>
    protected virtual bool Accept(RegexRule rule, string value) => true;
}
