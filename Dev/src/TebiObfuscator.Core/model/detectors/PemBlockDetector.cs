using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Détecte les blocs encadrés au format PEM : clés privées, certificats, clés
/// publiques.
///
/// Le bloc est traité comme un tout et jamais ligne par ligne, sous peine de
/// produire un fichier où seules certaines lignes du corps auraient été
/// remplacées. La reconnaissance s'appuie sur une référence arrière au libellé
/// d'ouverture, ce qui évite d'apparier un <c>BEGIN CERTIFICATE</c> avec un
/// <c>END PRIVATE KEY</c> lorsque plusieurs blocs se suivent.
///
/// Les certificats ne sont pas des secrets, mais ils portent une identité —
/// nom commun, organisation, noms alternatifs — et sont donc masqués eux aussi
/// par défaut.
/// </summary>
public sealed partial class PemBlockDetector : IDetector
{
    private const int TimeoutMilliseconds = 3000;

    /// <inheritdoc />
    public string RuleId => "secret.pem";

    /// <summary>Un bloc PEM tient sur plusieurs lignes : aucun nom n'en contient.</summary>
    public bool AppliesToNames => false;

    /// <summary>Bloc PEM complet, de son en-tête à son pied de même libellé.</summary>
    [GeneratedRegex(
        @"-----BEGIN (?<label>[A-Z0-9 ]+)-----.*?-----END \k<label>-----",
        RegexOptions.Singleline,
        TimeoutMilliseconds)]
    private static partial Regex PemBlock();

    /// <inheritdoc />
    public IEnumerable<DetectedSpan> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(context);
    }

    /// <summary>
    /// Parcourt les blocs PEM du texte.
    /// </summary>
    /// <param name="context">Contexte d'analyse validé</param>
    /// <returns>Un span par bloc trouvé</returns>
    private IEnumerable<DetectedSpan> Enumerate(DetectionContext context)
    {
        foreach (Match match in PemBlock().Matches(context.Text))
        {
            yield return DetectedSpan.Create(
                match.Index,
                match.Length,
                SpanCategory.KeyBlock,
                match.Value,
                RuleId,
                path: match.Groups["label"].Value);
        }
    }
}
