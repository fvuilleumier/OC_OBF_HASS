using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace MHassnage.Core.Service;

/// <summary>Conversion JSON → YAML (dashboards lus via l'API), reprise de HassExport.</summary>
public static class YamlService
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        // Une chaîne qui ressemble à un nombre ou un booléen est mise entre guillemets :
        // indispensable pour relire le YAML sans changer les types (Appliquer dans HA)
        .WithQuotingNecessaryStrings()
        .Build();

    public static string ToYaml(JsonElement json) => Serializer.Serialize(ToObject(json));

    /// <summary>
    /// YAML → JSON pour renvoyer un dashboard à HA. Scalaire entre guillemets ou en bloc = chaîne ;
    /// sinon null / booléen / entier / décimal selon sa forme (schéma YAML 1.2 « core »).
    /// </summary>
    public static JsonNode? FromYaml(string yaml)
    {
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : ToNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ToNode(YamlDotNet.RepresentationModel.YamlNode node) => node switch
    {
        YamlDotNet.RepresentationModel.YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            KeyValuePair.Create(((YamlDotNet.RepresentationModel.YamlScalarNode)kv.Key).Value ?? "", ToNode(kv.Value)))),
        YamlDotNet.RepresentationModel.YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToNode).ToArray()),
        YamlDotNet.RepresentationModel.YamlScalarNode scalar => Scalar(scalar),
        _ => null,
    };

    private static JsonNode? Scalar(YamlDotNet.RepresentationModel.YamlScalarNode scalar)
    {
        string value = scalar.Value ?? "";
        if (scalar.Style is not (YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any))
            return JsonValue.Create(value);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (value is "" or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, inv, out long l)) return JsonValue.Create(l);
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out double d)
            && value.IndexOfAny(['.', 'e', 'E']) >= 0) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    public static string ToYaml(JsonNode? json) =>
        ToYaml(JsonDocument.Parse(json?.ToJsonString() ?? "null").RootElement);

    private static object? ToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
