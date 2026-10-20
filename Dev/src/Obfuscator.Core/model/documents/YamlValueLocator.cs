using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Obfuscator.Core.Model.Documents;

/// <summary>
/// Localise les valeurs d'un document YAML.
///
/// L'analyseur est utilisé en mode événements, jamais en chargement puis
/// réécriture : un YAML resérialisé perd ses commentaires, son indentation et
/// ses ancres, ce qui rend le diff illisible et le fichier méconnaissable.
///
/// Les scalaires en bloc — <c>|</c> et <c>&gt;</c> — sont signalés mais leur
/// remplacement d'un seul tenant est laissé de côté : leur valeur logique
/// diffère du texte source par l'indentation et le repliement, et substituer le
/// bloc entier casserait le document. Les détecteurs textuels continuent, eux,
/// de s'appliquer à l'intérieur.
/// </summary>
public sealed class YamlValueLocator : IStructureLocator
{
    /// <inheritdoc />
    public bool CanHandle(FileKind kind) => kind == FileKind.Yaml;

    /// <inheritdoc />
    public IReadOnlyList<ValueLocation> Locate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<ValueLocation> locations = [];
        List<Context> stack = [];

        try
        {
            Parser parser = new(new StringReader(text));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case MappingStart:
                        Push(stack, ContextKind.Mapping);
                        break;

                    case SequenceStart:
                        Push(stack, ContextKind.Sequence);
                        break;

                    case MappingEnd:
                    case SequenceEnd:
                        if (stack.Count > 0)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        break;

                    case Scalar scalar:
                        HandleScalar(text, scalar, stack, locations);
                        break;

                    default:
                        break;
                }
            }
        }
        catch (YamlException)
        {
            // Document mal formé : on garde ce qui a été localisé et l'analyse
            // textuelle prend le relais.
        }

        return locations;
    }

    /// <summary>
    /// Traite un scalaire : soit une clé de table d'association, soit une
    /// valeur à localiser.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="scalar">Événement scalaire</param>
    /// <param name="stack">Pile des contextes ouverts</param>
    /// <param name="locations">Liste des valeurs localisées à compléter</param>
    private static void HandleScalar(string text, Scalar scalar, List<Context> stack, List<ValueLocation> locations)
    {
        Context? current = stack.Count > 0 ? stack[^1] : null;

        if (current is { Kind: ContextKind.Mapping, PendingKey: null })
        {
            current.PendingKey = scalar.Value;
            return;
        }

        string keyName = current?.PendingKey ?? DescribeIndex(current);
        if (current is not null)
        {
            if (current.Kind == ContextKind.Sequence)
            {
                current.Index++;
            }

            current.PendingKey = null;
        }

        int start = (int)scalar.Start.Index;
        int end = (int)scalar.End.Index;
        if (start < 0 || end > text.Length || end <= start)
        {
            return;
        }

        bool isBlock = scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded;
        bool isQuoted = scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted;

        // Les marques encadrent les guillemets : la valeur elle-même se trouve
        // à l'intérieur.
        if (isQuoted && end - start >= 2)
        {
            start++;
            end--;
        }

        locations.Add(new ValueLocation(
            start,
            end - start,
            ComposePath(stack, keyName),
            keyName,
            isQuoted,
            IsNumeric(scalar),
            isBlock || text[start..end].Contains('\n', StringComparison.Ordinal)));
    }

    /// <summary>
    /// Empile un nouveau contexte, en lui donnant pour nom la clé en attente.
    /// </summary>
    /// <param name="stack">Pile des contextes ouverts</param>
    /// <param name="kind">Nature du contexte</param>
    private static void Push(List<Context> stack, ContextKind kind)
    {
        string name = string.Empty;
        if (stack.Count > 0 && stack[^1].PendingKey is { } pending)
        {
            name = pending;
            stack[^1].PendingKey = null;
        }

        stack.Add(new Context(kind, name));
    }

    /// <summary>
    /// Indique si un scalaire non quoté représente un nombre.
    /// </summary>
    /// <param name="scalar">Événement scalaire</param>
    /// <returns><c>true</c> s'il s'agit d'un nombre nu</returns>
    private static bool IsNumeric(Scalar scalar)
    {
        return scalar.Style == ScalarStyle.Plain
            && double.TryParse(scalar.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Nomme un élément de séquence.
    /// </summary>
    /// <param name="context">Contexte courant</param>
    /// <returns>Le nom à utiliser dans le chemin</returns>
    private static string DescribeIndex(Context? context)
    {
        return context is { Kind: ContextKind.Sequence }
            ? string.Create(CultureInfo.InvariantCulture, $"[{context.Index}]")
            : string.Empty;
    }

    /// <summary>
    /// Compose le chemin structurel courant.
    /// </summary>
    /// <param name="stack">Pile des contextes ouverts</param>
    /// <param name="name">Nom du segment courant</param>
    /// <returns>Le chemin, par exemple <c>$.db.password</c></returns>
    private static string ComposePath(List<Context> stack, string name)
    {
        StringBuilder builder = new("$");

        foreach (string segment in stack.Select(context => context.Name).Where(segment => segment.Length > 0))
        {
            builder.Append('.').Append(segment);
        }

        if (name.Length > 0)
        {
            builder.Append('.').Append(name);
        }

        return builder.ToString();
    }

    /// <summary>Nature d'un contexte ouvert dans le document.</summary>
    private enum ContextKind
    {
        /// <summary>Table d'association clé / valeur.</summary>
        Mapping,

        /// <summary>Séquence d'éléments.</summary>
        Sequence,
    }

    /// <summary>
    /// Contexte ouvert : nom du nœud, clé en attente et index de séquence.
    /// </summary>
    /// <param name="kind">Nature du contexte</param>
    /// <param name="name">Nom du nœud dans le chemin</param>
    private sealed class Context(ContextKind kind, string name)
    {
        /// <summary>Nature du contexte.</summary>
        public ContextKind Kind { get; } = kind;

        /// <summary>Nom du nœud dans le chemin structurel.</summary>
        public string Name { get; } = name;

        /// <summary>Clé lue dont la valeur n'est pas encore arrivée.</summary>
        public string? PendingKey { get; set; }

        /// <summary>Index courant, pour une séquence.</summary>
        public int Index { get; set; }
    }
}
