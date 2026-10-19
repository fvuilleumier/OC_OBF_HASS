using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Documents;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Masque la valeur de toute clé dont le nom désigne un secret, un compte, un
/// port ou un identifiant, en s'appuyant sur la structure réelle du document
/// plutôt que sur une expression régulière.
///
/// C'est le détecteur le plus rentable de l'outil, pour une raison simple : il
/// ne juge pas la valeur mais la clé. Un mot de passe qui ressemble à un mot
/// ordinaire, qu'aucune analyse d'entropie ne signalerait, est masqué parce
/// qu'il se trouve sous <c>password</c>, ou sous <c>wsPassword</c> : le nom de
/// clé est jugé par <see cref="SensitiveKeyNames"/>, qui reconnaît les noms
/// composés.
///
/// Le passage par l'analyseur, plutôt que par le détecteur textuel équivalent,
/// résout trois cas que celui-ci traite mal : une valeur non quotée contenant
/// des espaces, une valeur contenant le caractère qui sert de délimiteur, et la
/// distinction entre une chaîne et un nombre — remplacer un port par un jeton
/// rendrait le document invalide.
///
/// Une valeur sur plusieurs lignes — un bloc YAML <c>password: |</c> suivi du
/// secret — ne peut pas être remplacée d'un seul tenant : le bloc perdrait son
/// indentation et le document sa validité. Chacune de ses lignes non vides
/// reçoit donc son propre pseudonyme, à sa place ; sans cela, le secret partait
/// en clair, le bloc étant écarté en entier.
/// </summary>
public sealed partial class StructuralSecretDetector : IDetector
{
    private const int TimeoutMilliseconds = 1000;

    private readonly List<IStructureLocator> _locators;

    /// <summary>
    /// Construit le détecteur à partir des localisateurs disponibles.
    /// </summary>
    /// <param name="locators">Localisateurs structurels, un par format pris en charge</param>
    public StructuralSecretDetector(IEnumerable<IStructureLocator>? locators = null)
    {
        _locators = [.. locators ?? [new JsonValueLocator(), new YamlValueLocator()]];
    }

    /// <inheritdoc />
    public string RuleId => "structure.sensitiveKey";

    /// <summary>Un nom de fichier n'a pas de structure.</summary>
    public bool AppliesToNames => false;

    /// <summary>
    /// Ligne d'en-tête d'un bloc YAML : l'indicateur <c>|</c> ou <c>&gt;</c>,
    /// ses options d'indentation et de fin, et un commentaire éventuel.
    /// </summary>
    [GeneratedRegex(@"^[|>][+\-]?\d?[+\-]?(?:\s+#.*)?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex BlockHeader();

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(context);
    }

    /// <summary>
    /// Parcourt les valeurs localisées et retient celles dont la clé est
    /// sensible.
    /// </summary>
    /// <param name="context">Contexte d'analyse validé</param>
    /// <returns>Un span par valeur à masquer</returns>
    private IEnumerable<DetectedSpan> Enumerate(DetectionContext context)
    {
        foreach (IStructureLocator locator in _locators)
        {
            if (!locator.CanHandle(context.Kind))
            {
                continue;
            }

            foreach (ValueLocation location in locator.Locate(context.Text))
            {
                if (location.Length == 0)
                {
                    continue;
                }

                // Un bloc multiligne serait remplacé à tort dans son ensemble,
                // indentation comprise : ses lignes sont traitées une à une.
                if (location.SpansSeveralLines)
                {
                    foreach (DetectedSpan line in BlockLines(context.Text, location))
                    {
                        yield return line;
                    }

                    continue;
                }

                // Le nom de la clé décide, composé ou non — « wsPassword »,
                // « db_password », « mfPort ». Une référence à une variable
                // d'environnement, un drapeau ou un renvoi ne contiennent aucun
                // secret : les masquer ferait perdre au modèle l'information la
                // plus utile de la ligne, que le secret vient d'ailleurs.
                string value = context.Text.Substring(location.Start, location.Length);
                if (SensitiveKeyNames.ClassifyPair(location.KeyName, value) is not SpanCategory category)
                {
                    continue;
                }

                // Un nombre nu remplacé par un jeton rendrait le document
                // invalide ; un port, une coordonnée ou un identifiant entier,
                // eux, reçoivent un autre nombre.
                bool renumbered = category is SpanCategory.NetworkPort or SpanCategory.Coordinate
                    || (category == SpanCategory.Identifier && value.All(char.IsAsciiDigit));

                if (location.IsNumeric && !renumbered)
                {
                    continue;
                }

                yield return DetectedSpan.Create(
                    location.Start,
                    location.Length,
                    category,
                    value,
                    RuleId,
                    path: location.Path);
            }
        }
    }

    /// <summary>
    /// Découpe une valeur sur plusieurs lignes et retient chacune de ses lignes
    /// non vides, sans son indentation, si la clé désigne un secret ou un
    /// compte. L'en-tête d'un bloc — <c>|</c>, <c>&gt;-</c>, suivi ou non d'un
    /// commentaire — n'est pas une ligne de la valeur.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="location">Valeur localisée</param>
    /// <returns>Un span par ligne à masquer</returns>
    private IEnumerable<DetectedSpan> BlockLines(string text, ValueLocation location)
    {
        int end = location.Start + location.Length;
        int position = location.Start;

        while (position < end)
        {
            int lineEnd = text.IndexOf('\n', position, end - position);
            lineEnd = lineEnd < 0 ? end : lineEnd;

            // La ligne, sans son indentation ni ses espaces de fin — fin de
            // ligne Windows comprise.
            int start = position;
            int stop = lineEnd;
            while (start < stop && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            while (stop > start && char.IsWhiteSpace(text[stop - 1]))
            {
                stop--;
            }

            position = lineEnd + 1;
            if (stop == start)
            {
                continue;
            }

            string line = text[start..stop];
            if (BlockHeader().IsMatch(line))
            {
                continue;
            }

            // Chaque ligne est jugée comme une valeur : un drapeau ou une
            // marque de substitution n'est pas un secret, même dans un bloc.
            if (SensitiveKeyNames.ClassifyPair(location.KeyName, line)
                is SpanCategory category and (SpanCategory.Secret or SpanCategory.Credential or SpanCategory.Email))
            {
                yield return DetectedSpan.Create(start, stop - start, category, line, RuleId + ".block", path: location.Path);
            }
        }
    }
}
