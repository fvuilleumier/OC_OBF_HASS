using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;

namespace MHassnage.Core.Service;

/// <summary>
/// Export complet de HA vers un dossier (par défaut l'export du dépôt privé) :
///   1. fichiers YAML copiés depuis le partage Samba (automations, configuration, thèmes, Z2m) ;
///   2. éléments (états, registres…) lus en WebSocket → Elements/&lt;clé&gt;.jsonc ;
///   3. dashboards (lovelace/config) → Dashboards/&lt;nom&gt;.yaml + une copie par vue.
/// Reprise de HassExport, Playwright remplacé par <see cref="HassClient"/>.
/// </summary>
public sealed class HassExportService(AppSettings settings, IProgress<string>? log = null)
{
    /// <summary>
    /// Clé de configuration → commande WebSocket. « KeyBy » transforme la liste renvoyée
    /// en objet indexé, comme l'était hass.&lt;clé&gt; dans le navigateur (diffs stables).
    /// </summary>
    private static readonly Dictionary<string, (string Command, string? KeyBy)> KeyCommands = new()
    {
        ["areas"] = ("config/area_registry/list", "area_id"),
        ["devices"] = ("config/device_registry/list", "id"),
        ["entities"] = ("config/entity_registry/list", "entity_id"),
        ["floors"] = ("config/floor_registry/list", "floor_id"),
        ["labels"] = ("config/label_registry/list", "label_id"),
        ["states"] = ("get_states", "entity_id"),
        ["config"] = ("get_config", null),
        ["panels"] = ("get_panels", null),
        ["services"] = ("get_services", null),
        ["themes"] = ("frontend/get_themes", null),
        ["user"] = ("auth/current_user", null),
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyCollection<string> SupportedKeys => KeyCommands.Keys;

    /// <param name="outputFolder">Dossier cible ; null = export du dépôt privé.</param>
    /// <exception cref="HassAuthException">Jeton refusé : afficher <see cref="HassTokenHelp"/>.</exception>
    public async Task ExportAsync(string? outputFolder = null, CancellationToken ct = default)
    {
        string output = outputFolder ?? settings.Repositories.Private.ExportPath;
        Directory.CreateDirectory(output);
        Log($"Export vers {output}");

        CopyFiles(output);

        Log($"Connexion à {HassClient.WebSocketUri(settings.Hass.Url)}");
        await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
        Log($"Connecté (Home Assistant {client.HaVersion})");

        await ExportElementsAsync(client, output, ct);
        await ExportDashboardsAsync(client, output, ct);
    }

    // ---------------------------------------------------------------- Samba

    private void CopyFiles(string output)
    {
        string root = settings.Hass.DefaultSambaHassPath;
        if (string.IsNullOrWhiteSpace(root) || root.Contains("À_COMPLÉTER", StringComparison.Ordinal))
        {
            Log("DefaultSambaHassPath non renseigné : copie des fichiers ignorée.");
            return;
        }
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"Partage inaccessible : {root}. Ouvre-le une fois dans l'Explorateur Windows (identifiants Samba mémorisés).");

        foreach (var e in settings.Hass.Elements)
            Copy(Path.Combine(root, e.Source), Path.Combine(output, e.Destination));

        CopyFolder(root, settings.Hass.Themes, output);
        CopyFolder(root, settings.Hass.Z2m, output);
    }

    private void CopyFolder(string root, FolderCopy folder, string output)
    {
        if (string.IsNullOrWhiteSpace(folder.Source)) return;

        string src = Path.Combine(root, folder.Source);
        string dst = Path.Combine(output, folder.Destination);
        if (!Directory.Exists(src))
        {
            Log($"ABSENT  {src}");
            return;
        }

        var files = folder.Files.Count > 0
            ? folder.Files.Select(n => Path.Combine(src, n))
            : Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories);

        foreach (string file in files)
            Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
    }

    private void Copy(string src, string dst)
    {
        if (!File.Exists(src))
        {
            Log($"ABSENT  {src}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        Log($"OK  {dst}");
    }

    // ---------------------------------------------------------------- Éléments

    private async Task ExportElementsAsync(HassClient client, string output, CancellationToken ct)
    {
        string folder = Path.Combine(output, settings.Repositories.Private.ElementsFolder);
        Directory.CreateDirectory(folder);

        foreach (string key in settings.Hass.Keys)
        {
            if (!KeyCommands.TryGetValue(key, out var command))
            {
                Log($"IGNORÉ  clé « {key} » inconnue (connues : {string.Join(", ", KeyCommands.Keys)})");
                continue;
            }

            JsonNode? result;
            try
            {
                result = await client.CommandAsync(command.Command, ct: ct);
            }
            catch (HassCommandException ex)
            {
                Log($"ERREUR  {key} : {ex.Message}");
                continue;
            }

            if (command.KeyBy is { } keyBy && result is JsonArray list)
                result = IndexBy(list, keyBy);

            string content = $"// WebSocket : {command.Command}\n{result?.ToJsonString(Indented) ?? "null"}\n";
            string file = Path.Combine(folder, $"{key}.jsonc");
            await File.WriteAllTextAsync(file, content, ct);
            Log($"OK  {file}");
        }
    }

    /// <summary>Liste → objet indexé et trié par clé (ordre stable d'un export à l'autre).</summary>
    private static JsonObject IndexBy(JsonArray list, string keyBy)
    {
        var indexed = new JsonObject();
        foreach (var item in list
                     .OfType<JsonObject>()
                     .OrderBy(i => i[keyBy]?.GetValue<string>(), StringComparer.Ordinal))
        {
            string? id = item[keyBy]?.GetValue<string>();
            if (id is not null) indexed[id] = item.DeepClone();
        }
        return indexed;
    }

    // ---------------------------------------------------------------- Dashboards

    private async Task ExportDashboardsAsync(HassClient client, string output, CancellationToken ct)
    {
        string folder = Path.Combine(output, settings.Hass.Dashboards.Destination);

        foreach (var dashboard in settings.Hass.Dashboards.Items)
        {
            // Seul le 1er segment compte : « mon-dashboard/0 » → « mon-dashboard »
            string segment = (dashboard.UrlPath ?? "").Trim('/').Split('/')[0];
            string? urlPath = segment is "" or "lovelace" ? null : segment;

            JsonNode? config;
            try
            {
                config = await client.CommandAsync("lovelace/config",
                    new JsonObject { ["url_path"] = urlPath, ["force"] = true }, ct);
            }
            catch (HassCommandException ex)
            {
                Log($"ERREUR  dashboard « {segment} » : {ex.Message}");
                continue;
            }

            string name = FileName(string.IsNullOrWhiteSpace(dashboard.FileName) ? segment : dashboard.FileName);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"{name}.yaml");
            await File.WriteAllTextAsync(file, YamlService.ToYaml(config), ct);
            Log($"OK  {file}");

            if (config?["views"] is not JsonArray views) continue;

            string viewsFolder = Path.Combine(folder, "views", name);
            Directory.CreateDirectory(viewsFolder);
            // Vues supprimées ou renommées dans HA : leurs anciens fichiers ne doivent pas survivre
            foreach (string old in Directory.EnumerateFiles(viewsFolder, "*.yaml")) File.Delete(old);
            int i = 0;
            foreach (var view in views)
            {
                i++;
                string? viewName = view?["path"]?.GetValue<string>() ?? view?["title"]?.GetValue<string>();
                string viewFile = Path.Combine(viewsFolder, $"{i:00} - {FileName(viewName ?? "vue")}.yaml");
                await File.WriteAllTextAsync(viewFile, YamlService.ToYaml(view), ct);
                Log($"OK  {viewFile}");
            }
        }
    }

    private static string FileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private void Log(string message) => log?.Report(message);
}
