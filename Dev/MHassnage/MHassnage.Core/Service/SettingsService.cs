using System.Text.Json;
using System.Text.Json.Nodes;
using MHassnage.Core.Model;
using Microsoft.Extensions.Configuration;

namespace MHassnage.Core.Service;

/// <summary>
/// Configuration réelle : %APPDATA%\MHassnage\appsettings.json (ou MHASSNAGE_SETTINGS),
/// hors de tout dépôt. Créée au premier lancement à partir de appsettings.example.json.
/// Les secrets saisis en clair y sont chiffrés au chargement suivant (clé de l'application).
/// </summary>
public static class SettingsService
{
    public const string EnvironmentVariable = "MHASSNAGE_SETTINGS";
    private const string ExampleFileName = "appsettings.example.json";

    /// <summary>Chemins (« Section:Clé ») des valeurs chiffrées dans le fichier.</summary>
    private static readonly string[] SecretKeys = ["Hass:Token"];

    public static string SettingsPath =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "MHassnage", "appsettings.json");

    /// <summary>Vrai si le fichier vient d'être créé à partir du modèle (à compléter).</summary>
    public static bool CreatedFromExample { get; private set; }

    public static AppSettings Load()
    {
        string path = SettingsPath;
        EnsureExists(path);
        ProtectClearSecrets(path);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);

        TokenUnreadable = false;
        if (!IsPlaceholder(settings.Hass.Token))
        {
            try
            {
                settings.Hass.Token = SecretProtector.Unprotect(settings.Hass.Token);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Valeur altérée, tronquée ou chiffrée par une autre clé : traitée comme absente
                settings.Hass.Token = "";
                TokenUnreadable = true;
            }
        }

        return settings;
    }

    /// <summary>Vrai si le jeton chiffré du fichier n'a pas pu être déchiffré au dernier chargement.</summary>
    public static bool TokenUnreadable { get; private set; }

    /// <summary>Liste les réglages manquants ou à compléter (sans jamais afficher de valeur).</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var problems = new List<string>();

        if (IsPlaceholder(settings.Hass.Url)) problems.Add("Hass:Url");
        if (TokenUnreadable) problems.Add("Hass:Token (illisible : valeur altérée, à ressaisir en clair)");
        else if (IsPlaceholder(settings.Hass.Token)) problems.Add("Hass:Token (saisi en clair, chiffré au lancement suivant)");
        if (IsPlaceholder(settings.Repositories.Private.Path)) problems.Add("Repositories:Private:Path");
        if (IsPlaceholder(settings.Repositories.Public.Path)) problems.Add("Repositories:Public:Path");
        if (IsPlaceholder(settings.Obfuscator.CliPath)) problems.Add("Obfuscator:CliPath");
        if (IsPlaceholder(settings.Obfuscator.WpfPath)) problems.Add("Obfuscator:WpfPath");
        if (IsPlaceholder(settings.Obfuscator.ProjectFolder)) problems.Add("Obfuscator:ProjectFolder");

        return problems;
    }

    /// <summary>
    /// Ajoute une correspondance fichier Hass ↔ fichier du dépôt (Hass:Elements) et l'enregistre :
    /// l'export suivant ramènera le fichier sous le même nom dans le dépôt.
    /// </summary>
    public static void AddElement(AppSettings settings, string haName, string repoName)
    {
        if (settings.Hass.Elements.Any(e => e.Destination.Equals(repoName, StringComparison.OrdinalIgnoreCase))) return;
        settings.Hass.Elements.Add(new FileCopy { Source = haName, Destination = repoName });

        string path = SettingsPath;
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();
        var hass = (root["Hass"] ??= new JsonObject()).AsObject();
        hass["Elements"] = new JsonArray(settings.Hass.Elements
            .Select(e => (JsonNode)new JsonObject { ["Source"] = e.Source, ["Destination"] = e.Destination })
            .ToArray());
        Write(path, root);
    }

    /// <summary>Ajoute une règle d'exclusion au dépôt (« Private » ou « Public ») et l'enregistre.</summary>
    public static void AddExclusion(AppSettings settings, string repository, string rule)
    {
        var repo = settings.Repositories.Get(repository);
        if (repo.Excluded.Contains(rule, StringComparer.OrdinalIgnoreCase)) return;
        repo.Excluded.Add(rule);

        string path = SettingsPath;
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();
        var repos = (root["Repositories"] ??= new JsonObject()).AsObject();
        var node = (repos[repository] ??= new JsonObject()).AsObject();
        node["Excluded"] = new JsonArray(repo.Excluded.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        Write(path, root);
    }

    private static void Write(string path, JsonNode root) =>
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("À_COMPLÉTER", StringComparison.Ordinal);

    private static void EnsureExists(string path)
    {
        if (File.Exists(path)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, ExampleFileName), path);
        CreatedFromExample = true;
    }

    /// <summary>Chiffre et réécrit les secrets encore en clair dans le fichier.</summary>
    private static void ProtectClearSecrets(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;

        bool changed = false;
        foreach (string key in SecretKeys)
        {
            string[] parts = key.Split(':');
            JsonNode? parent = root;
            foreach (string part in parts[..^1]) parent = parent?[part];
            if (parent?[parts[^1]] is not JsonValue node || !node.TryGetValue(out string? value)) continue;
            if (IsPlaceholder(value) || SecretProtector.IsProtected(value)) continue;

            parent[parts[^1]] = SecretProtector.Protect(value);
            changed = true;
        }

        if (changed) Write(path, root);
    }
}
