using System.Text.Json;
using YamlDotNet.Serialization;

namespace HassExport.Service;

/// <summary>Conversion JSON -> YAML (pour les dashboards récupérés via l'API).</summary>
public static class YamlService
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    public static string ToYaml(JsonElement json) => Serializer.Serialize(ToObject(json));

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
