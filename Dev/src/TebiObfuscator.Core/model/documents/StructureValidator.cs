using System.Text;
using System.Text.Json;
using System.Xml;
using YamlDotNet.Core;

namespace TebiObfuscator.Core.Model.Documents;

/// <summary>
/// Verdict d'un contrôle de syntaxe.
/// </summary>
/// <param name="Applicable">Vrai si le format se contrôle : JSON, YAML ou XML</param>
/// <param name="Valid">Vrai si le document se lit sans erreur</param>
/// <param name="Line">Ligne de la première erreur, à partir de 1 ; 0 sans erreur</param>
/// <param name="Message">Message de l'analyseur, en clair</param>
public sealed record StructureCheck(bool Applicable, bool Valid, int Line, string Message)
{
    /// <summary>Format qui ne se contrôle pas.</summary>
    public static StructureCheck NotApplicable { get; } = new(false, true, 0, string.Empty);

    /// <summary>Document lisible.</summary>
    public static StructureCheck Passed { get; } = new(true, true, 0, string.Empty);
}

/// <summary>
/// Vérifie qu'un document JSON, YAML ou XML se lit encore.
///
/// Une IA qui modifie un fichier de configuration peut en casser la syntaxe —
/// une indentation YAML décalée, une virgule JSON oubliée — et l'erreur ne se
/// verrait qu'une fois le fichier réexporté vers le système qui l'utilise. Le
/// rapatriement le contrôle donc avant d'écrire, et le signale à sa ligne. Les
/// analyseurs sont ceux que l'outil emploie déjà pour localiser les valeurs,
/// avec les mêmes tolérances : commentaires et virgules finales en JSON,
/// étiquettes propres à une application en YAML (<c>!include</c>,
/// <c>!secret</c>), qui sont signalées et jamais résolues.
/// </summary>
public static class StructureValidator
{
    /// <summary>
    /// Contrôle la syntaxe d'un document.
    /// </summary>
    /// <param name="kind">Format du document</param>
    /// <param name="text">Contenu du document</param>
    /// <returns>Le verdict</returns>
    public static StructureCheck Check(FileKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return kind switch
        {
            FileKind.Json => CheckJson(text),
            FileKind.Yaml => CheckYaml(text),
            FileKind.Xml => CheckXml(text),
            _ => StructureCheck.NotApplicable,
        };
    }

    /// <summary>
    /// Lit un document JSON jusqu'au bout.
    /// </summary>
    /// <param name="text">Contenu du document</param>
    /// <returns>Le verdict</returns>
    private static StructureCheck CheckJson(string text)
    {
        if (text.Trim().Length == 0)
        {
            return StructureCheck.Passed;
        }

        JsonReaderOptions options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 256,
        };

        try
        {
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(text), options);
            while (reader.Read())
            {
                // On ne fait que lire : une erreur lève.
            }

            return StructureCheck.Passed;
        }
        catch (JsonException exception)
        {
            // Le lecteur compte les lignes à partir de zéro.
            int line = (int)(exception.LineNumber ?? 0) + 1;
            return new StructureCheck(true, false, line, exception.Message);
        }
    }

    /// <summary>
    /// Lit un document YAML jusqu'au bout, événement par événement.
    /// </summary>
    /// <param name="text">Contenu du document</param>
    /// <returns>Le verdict</returns>
    private static StructureCheck CheckYaml(string text)
    {
        try
        {
            Parser parser = new(new StringReader(text));
            while (parser.MoveNext())
            {
                // On ne fait que lire : une erreur lève.
            }

            return StructureCheck.Passed;
        }
        catch (YamlException exception)
        {
            return new StructureCheck(true, false, (int)exception.Start.Line, exception.Message);
        }
    }

    /// <summary>
    /// Lit un document XML jusqu'au bout, sans jamais résoudre d'entité ni de
    /// définition externe.
    /// </summary>
    /// <param name="text">Contenu du document</param>
    /// <returns>Le verdict</returns>
    private static StructureCheck CheckXml(string text)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
        };

        try
        {
            using StringReader input = new(text);
            using XmlReader reader = XmlReader.Create(input, settings);
            while (reader.Read())
            {
                // On ne fait que lire : une erreur lève.
            }

            return StructureCheck.Passed;
        }
        catch (XmlException exception)
        {
            return new StructureCheck(true, false, exception.LineNumber, exception.Message);
        }
    }
}
