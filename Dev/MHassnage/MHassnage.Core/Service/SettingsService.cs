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

        if (!IsPlaceholder(settings.Hass.Token))
            settings.Hass.Token = SecretProtector.Unprotect(settings.Hass.Token);

        return settings;
    }

    /// <summary>Liste les réglages manquants ou à compléter (sans jamais afficher de valeur).</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var problems = new List<string>();

        if (IsPlaceholder(settings.Hass.Url)) problems.Add("Hass:Url");
        if (IsPlaceholder(settings.Hass.Token)) problems.Add("Hass:Token (saisi en clair, chiffré au lancement suivant)");
        if (IsPlaceholder(settings.Repositories.Private.Path)) problems.Add("Repositories:Private:Path");
        if (IsPlaceholder(settings.Repositories.Public.Path)) problems.Add("Repositories:Public:Path");
        if (IsPlaceholder(settings.Obfuscator.CliPath)) problems.Add("Obfuscator:CliPath");
        if (IsPlaceholder(settings.Obfuscator.WpfPath)) problems.Add("Obfuscator:WpfPath");
        if (IsPlaceholder(settings.Obfuscator.ProjectFolder)) problems.Add("Obfuscator:ProjectFolder");

        return problems;
    }

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

        if (changed)
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
    }
}
