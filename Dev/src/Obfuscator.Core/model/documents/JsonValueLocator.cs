using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Obfuscator.Core.Model.Documents;

/// <summary>
/// Localise les valeurs d'un document JSON ou JSONC.
///
/// Le lecteur bas niveau du cadriciel donne des positions exactes tout en
/// tolérant commentaires et virgules finales, ce qui couvre le JSONC sans
/// bibliothèque supplémentaire. Deux précautions gouvernent l'implémentation.
///
/// D'abord la conversion des offsets : le lecteur indexe en octets UTF-8 alors
/// que tout le reste de l'outil raisonne en unités UTF-16. Mélanger les deux
/// produit des décalages silencieux dès qu'un accent apparaît — cas garanti sur
/// des fichiers suisses. La conversion se fait donc en une seule passe triée,
/// et jamais offset par offset.
///
/// Ensuite l'échec : un document invalide ne doit pas interrompre le traitement.
/// L'analyse s'arrête alors sur ce qu'elle a déjà localisé, et les détecteurs
/// textuels prennent le relais.
/// </summary>
public sealed class JsonValueLocator : IStructureLocator
{
    /// <inheritdoc />
    public bool CanHandle(FileKind kind) => kind == FileKind.Json;

    /// <inheritdoc />
    public IReadOnlyList<ValueLocation> Locate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        List<RawLocation> raw = ReadLocations(utf8);
        if (raw.Count == 0)
        {
            return [];
        }

        return ConvertOffsets(text, raw);
    }

    /// <summary>
    /// Parcourt le document et relève la position de chaque valeur scalaire,
    /// en offsets d'octets.
    /// </summary>
    /// <param name="utf8">Document encodé en UTF-8</param>
    /// <returns>Les positions relevées, dans l'ordre du document</returns>
    private static List<RawLocation> ReadLocations(byte[] utf8)
    {
        JsonReaderOptions options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 128,
        };

        Utf8JsonReader reader = new(utf8, options);
        List<RawLocation> locations = [];

        // La pile reconstitue le chemin : le lecteur ne le fournit pas, et il
        // est indispensable pour désigner « $.db.password » dans un rapport.
        List<string> path = [];
        string? pendingKey = null;
        int arrayIndex = 0;
        bool inArray = false;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pendingKey = reader.GetString();
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        path.Add(pendingKey ?? Describe(inArray, arrayIndex));
                        inArray = reader.TokenType == JsonTokenType.StartArray;
                        arrayIndex = 0;
                        pendingKey = null;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (path.Count > 0)
                        {
                            path.RemoveAt(path.Count - 1);
                        }

                        inArray = false;
                        pendingKey = null;
                        break;

                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        locations.Add(CaptureScalar(ref reader, path, pendingKey, inArray, ref arrayIndex));
                        pendingKey = null;
                        break;

                    default:
                        pendingKey = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Document mal formé : on conserve ce qui a été localisé avant le
            // point de rupture plutôt que de tout perdre.
        }

        return locations;
    }

    /// <summary>
    /// Relève la position d'une valeur scalaire.
    /// </summary>
    /// <param name="reader">Lecteur positionné sur la valeur</param>
    /// <param name="path">Chemin courant</param>
    /// <param name="key">Nom de la clé portant la valeur, s'il y en a une</param>
    /// <param name="inArray">Vrai si la valeur appartient à un tableau</param>
    /// <param name="arrayIndex">Index courant dans le tableau, incrémenté ici</param>
    /// <returns>La position relevée</returns>
    private static RawLocation CaptureScalar(
        ref Utf8JsonReader reader,
        List<string> path,
        string? key,
        bool inArray,
        ref int arrayIndex)
    {
        bool isString = reader.TokenType == JsonTokenType.String;
        long start = reader.TokenStartIndex;
        long end = reader.BytesConsumed;

        // TokenStartIndex pointe le guillemet ouvrant : la valeur elle-même
        // commence un octet plus loin et s'arrête avant le guillemet fermant.
        if (isString)
        {
            start++;
            end--;
        }

        string name = key ?? Describe(inArray, arrayIndex);
        if (key is null && inArray)
        {
            arrayIndex++;
        }

        return new RawLocation(
            (int)start,
            (int)(end - start),
            ComposePath(path, name),
            name,
            isString,
            reader.TokenType == JsonTokenType.Number);
    }

    /// <summary>
    /// Convertit des positions exprimées en octets UTF-8 vers des positions en
    /// unités UTF-16, en une seule traversée du texte.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="raw">Positions en octets, dans l'ordre du document</param>
    /// <returns>Les positions converties</returns>
    private static List<ValueLocation> ConvertOffsets(string text, List<RawLocation> raw)
    {
        // Les bornes sont traitées ensemble et triées : appeler un convertisseur
        // par position coûterait un parcours complet du texte à chaque fois.
        List<int> boundaries = [];
        foreach (RawLocation location in raw)
        {
            boundaries.Add(location.ByteStart);
            boundaries.Add(location.ByteStart + location.ByteLength);
        }

        Dictionary<int, int> charByByte = MapOffsets(text, boundaries);
        List<ValueLocation> converted = new(raw.Count);

        foreach (RawLocation location in raw)
        {
            if (!charByByte.TryGetValue(location.ByteStart, out int start)
                || !charByByte.TryGetValue(location.ByteStart + location.ByteLength, out int end))
            {
                continue;
            }

            string value = text[start..end];
            converted.Add(new ValueLocation(
                start,
                end - start,
                location.Path,
                location.KeyName,
                location.IsQuoted,
                location.IsNumeric,
                value.Contains('\n', StringComparison.Ordinal)));
        }

        return converted;
    }

    /// <summary>
    /// Construit la table des positions demandées, octets vers caractères.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="byteOffsets">Positions d'octets recherchées</param>
    /// <returns>La table de conversion</returns>
    private static Dictionary<int, int> MapOffsets(string text, List<int> byteOffsets)
    {
        HashSet<int> wanted = [.. byteOffsets];
        Dictionary<int, int> map = [];

        int byteIndex = 0;
        int charIndex = 0;

        while (charIndex <= text.Length)
        {
            if (wanted.Contains(byteIndex))
            {
                map[byteIndex] = charIndex;
            }

            if (charIndex == text.Length)
            {
                break;
            }

            byteIndex += Utf8Length(text, charIndex);

            // Une paire de substitution UTF-16 occupe deux caractères pour un
            // seul point de code : les deux avancent ensemble.
            charIndex += char.IsHighSurrogate(text[charIndex]) && charIndex + 1 < text.Length ? 2 : 1;
        }

        return map;
    }

    /// <summary>
    /// Calcule le nombre d'octets UTF-8 du point de code commençant à une
    /// position donnée.
    /// </summary>
    /// <param name="text">Texte source</param>
    /// <param name="index">Position du premier caractère du point de code</param>
    /// <returns>Le nombre d'octets correspondant</returns>
    private static int Utf8Length(string text, int index)
    {
        char character = text[index];

        if (char.IsHighSurrogate(character) && index + 1 < text.Length)
        {
            return 4;
        }

        return character switch
        {
            < (char)0x80 => 1,
            < (char)0x800 => 2,
            _ => 3,
        };
    }

    /// <summary>
    /// Compose un chemin structurel lisible.
    /// </summary>
    /// <param name="path">Segments parcourus</param>
    /// <param name="name">Nom du segment courant</param>
    /// <returns>Le chemin, par exemple <c>$.db.password</c></returns>
    private static string ComposePath(List<string> path, string name)
    {
        StringBuilder builder = new("$");

        foreach (string segment in path)
        {
            if (segment.Length > 0)
            {
                builder.Append('.').Append(segment);
            }
        }

        return builder.Append('.').Append(name).ToString();
    }

    /// <summary>
    /// Nomme un élément dépourvu de clé.
    /// </summary>
    /// <param name="inArray">Vrai si l'élément appartient à un tableau</param>
    /// <param name="index">Index dans le tableau</param>
    /// <returns>Le nom à utiliser dans le chemin</returns>
    private static string Describe(bool inArray, int index)
    {
        return inArray ? string.Create(CultureInfo.InvariantCulture, $"[{index}]") : string.Empty;
    }

    /// <summary>
    /// Position d'une valeur, encore exprimée en octets.
    /// </summary>
    /// <param name="ByteStart">Début, en octets UTF-8</param>
    /// <param name="ByteLength">Longueur, en octets UTF-8</param>
    /// <param name="Path">Chemin structurel</param>
    /// <param name="KeyName">Nom de la clé</param>
    /// <param name="IsQuoted">Vrai si la valeur est une chaîne</param>
    /// <param name="IsNumeric">Vrai si la valeur est un nombre</param>
    private sealed record RawLocation(
        int ByteStart,
        int ByteLength,
        string Path,
        string KeyName,
        bool IsQuoted,
        bool IsNumeric);
}
