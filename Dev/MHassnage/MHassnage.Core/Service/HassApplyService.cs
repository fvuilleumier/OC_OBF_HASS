using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;

namespace MHassnage.Core.Service;

/// <summary>
/// « Appliquer dans HA » : compare le dépôt privé à HA, sauvegarde ce qui va être remplacé,
/// écrit (Samba pour les fichiers, lovelace/config/save pour les dashboards), vérifie la
/// configuration puis redémarre HA si un fichier de configuration a changé.
/// La même mécanique sert à « Restaurer la version précédente ».
///
/// Ne fait jamais : supprimer un fichier de HA absent du dépôt, écrire les states
/// (Hass:StateFiles), écrire les éléments JSON (Elements/*.jsonc, lecture seule).
/// </summary>
public sealed class HassApplyService(AppSettings settings, IProgress<string>? log = null)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private string SambaRoot => settings.Hass.DefaultSambaHassPath;
    private string ExportPath => settings.Repositories.Private.ExportPath;

    // ================================================================ Analyse

    /// <summary>Liste les éléments du dépôt privé et leur version actuelle dans HA.</summary>
    /// <param name="unmappedNames">Nom côté HA choisi pour les fichiers sans correspondance (clé : chemin dans le dépôt).</param>
    public async Task<List<ApplyItem>> AnalyzeAsync(IReadOnlyDictionary<string, string>? unmappedNames = null, CancellationToken ct = default)
    {
        EnsureSamba();
        var items = new List<ApplyItem>();
        var git = new GitService(settings.Repositories.Private.Path, settings.Git.ExecutablePath);

        async Task<string?> Head(string repoPath)
        {
            string rel = Path.GetRelativePath(settings.Repositories.Private.Path, Path.Combine(ExportPath, repoPath)).Replace('\\', '/');
            string content = await git.ReadAtHeadAsync(rel, ct);
            return content.Length == 0 ? null : ApplyItem.Normalize(content);
        }

        async Task AddFile(string repoPath, string haPath, ApplyCategory category, bool unmapped = false)
        {
            string repoFile = Path.Combine(ExportPath, repoPath);
            if (!File.Exists(repoFile) || IsExcluded(repoPath) || IsState(repoPath)) return;
            items.Add(new ApplyItem
            {
                Target = new ApplyTarget(ApplyKind.File, haPath.Replace('\\', '/')),
                Category = category,
                RepoPath = repoPath.Replace('\\', '/'),
                RepoContent = ApplyItem.Normalize(await File.ReadAllTextAsync(repoFile, ct)),
                HaContent = ReadHaFile(haPath),
                HeadContent = await Head(repoPath),
                IsUnmapped = unmapped,
            });
        }

        // 1. Fichiers déclarés (configuration.yaml, automations.yaml…)
        foreach (var e in settings.Hass.Elements)
            await AddFile(e.Destination, e.Source, ApplyCategory.Config);

        // 2. Fichiers YAML nouveaux à la racine de l'export (ex. un fichier sorti de configuration.yaml)
        var known = settings.Hass.Elements.Select(e => e.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(ExportPath))
            foreach (string file in Directory.EnumerateFiles(ExportPath, "*.yaml", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (known.Contains(name)) continue;
                string haName = unmappedNames?.GetValueOrDefault(name) ?? name.ToLowerInvariant();
                await AddFile(name, haName, ApplyCategory.Config, unmapped: true);
            }

        // 3. Dossiers copiés (thèmes, Zigbee2MQTT)
        await AddFolder(settings.Hass.Themes, ApplyCategory.Theme);
        await AddFolder(settings.Hass.Z2m, ApplyCategory.Z2m);

        async Task AddFolder(FolderCopy folder, ApplyCategory category)
        {
            if (string.IsNullOrWhiteSpace(folder.Source)) return;
            string repoDir = Path.Combine(ExportPath, folder.Destination);
            var files = folder.Files.Count > 0
                ? folder.Files.Select(f => Path.Combine(repoDir, f))
                : Directory.Exists(repoDir) ? Directory.EnumerateFiles(repoDir, "*", SearchOption.AllDirectories) : [];
            foreach (string file in files)
            {
                string rel = Path.GetRelativePath(repoDir, file);
                await AddFile(Path.Combine(folder.Destination, rel), Path.Combine(folder.Source, rel), category);
            }
        }

        // 4. Dashboards (fichier principal ; les fichiers de vues ne sont qu'une copie de lecture)
        await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
        foreach (var d in settings.Hass.Dashboards.Items)
        {
            string segment = (d.UrlPath ?? "").Trim('/').Split('/')[0];
            string urlPath = segment is "lovelace" ? "" : segment;
            string name = HassExportService.SafeFileName(string.IsNullOrWhiteSpace(d.FileName) ? segment : d.FileName);
            string repoPath = $"{settings.Hass.Dashboards.Destination}/{name}.yaml";
            string repoFile = Path.Combine(ExportPath, repoPath);
            if (!File.Exists(repoFile) || IsExcluded(repoPath)) continue;

            var current = await ReadDashboardAsync(client, urlPath, ct);
            items.Add(new ApplyItem
            {
                Target = new ApplyTarget(ApplyKind.Dashboard, urlPath),
                Category = ApplyCategory.Dashboard,
                RepoPath = repoPath,
                RepoContent = ApplyItem.Normalize(await File.ReadAllTextAsync(repoFile, ct)),
                HaContent = current is null ? null : ApplyItem.Normalize(YamlService.ToYaml(current)),
                HeadContent = await Head(repoPath),
            });
        }

        return items;
    }

    /// <summary>Relit dans HA le contenu actuel d'un fichier (après changement de son nom côté HA).</summary>
    public string? ReadHaFile(string haPath)
    {
        string full = Path.Combine(SambaRoot, haPath);
        return File.Exists(full) ? ApplyItem.Normalize(File.ReadAllText(full)) : null;
    }

    // ================================================================ Application

    /// <summary>
    /// Sauvegarde puis écrit les éléments ; vérifie la configuration et recharge / redémarre HA.
    /// </summary>
    /// <returns>Dossier de la sauvegarde prise avant l'écriture.</returns>
    public async Task<string> ApplyAsync(IReadOnlyList<ApplyItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) throw new InvalidOperationException("Aucun élément à appliquer.");
        EnsureSamba();

        var changes = items.Select(i => new Change(i.Target, i.Category,
            i.Target.Kind == ApplyKind.Dashboard ? YamlService.FromYaml(i.RepoContent)?.ToJsonString() : i.RepoContent)).ToList();

        // Contrôle avant toute écriture : un dashboard illisible ne doit rien casser
        foreach (var c in changes.Where(c => c.Target.Kind == ApplyKind.Dashboard && c.Content is null))
            throw new InvalidOperationException($"YAML vide ou illisible pour le {c.Target}.");

        string backup = await WriteAsync(changes, "Appliquer dans HA", ct);

        // Nouveaux fichiers : correspondance enregistrée pour que l'export les ramène sous le même nom
        foreach (var i in items.Where(i => i.IsUnmapped))
        {
            SettingsService.AddElement(settings, i.Target.Key, i.RepoPath);
            Log($"OK  correspondance enregistrée : {i.Target.Key} (HA) ↔ {i.RepoPath} (dépôt)");
        }

        await ReloadAsync(changes.Select(c => c.Category).ToHashSet(), ct);
        return backup;
    }

    /// <summary>Remet HA dans l'état d'une sauvegarde (elle-même sauvegardée avant, donc réversible).</summary>
    public async Task RestoreAsync(ApplyBackup backup, CancellationToken ct = default)
    {
        EnsureSamba();
        var changes = backup.Manifest.Entries.Select(e => new Change(
            new ApplyTarget(e.Kind, e.Key), e.Category,
            e.Existed ? File.ReadAllText(Path.Combine(backup.Folder, e.File)) : null)).ToList();

        await WriteAsync(changes, $"Avant restauration du {backup.Manifest.Created.LocalDateTime:dd.MM.yyyy HH:mm}", ct);
        await ReloadAsync(changes.Select(c => c.Category).ToHashSet(), ct);
    }

    /// <summary>Contenu à écrire ; null = supprimer le fichier (il n'existait pas avant).</summary>
    private sealed record Change(ApplyTarget Target, ApplyCategory Category, string? Content);

    private async Task<string> WriteAsync(List<Change> changes, string label, CancellationToken ct)
    {
        await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);

        // 1. Sauvegarde de l'état actuel de tout ce qui va être touché
        string folder = Path.Combine(settings.Hass.ApplyBackupPath, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var manifest = new ApplyBackupManifest { Created = DateTimeOffset.Now, Label = label, HaVersion = client.HaVersion };
        int n = 0;
        foreach (var c in changes)
        {
            string? current = c.Target.Kind == ApplyKind.File
                ? ReadRaw(c.Target.Key)
                : (await ReadDashboardAsync(client, c.Target.Key, ct))?.ToJsonString(Indented);
            var entry = new ApplyBackupEntry { Kind = c.Target.Kind, Key = c.Target.Key, Category = c.Category, Existed = current is not null };
            if (current is not null)
            {
                entry.File = $"{++n:000}{(c.Target.Kind == ApplyKind.File ? Path.GetExtension(c.Target.Key) : ".json")}";
                await File.WriteAllTextAsync(Path.Combine(folder, entry.File), current, Utf8, ct);
            }
            manifest.Entries.Add(entry);
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, Indented), Utf8, ct);
        Log($"OK  sauvegarde de la version actuelle de HA : {folder}");
        Prune();

        // 2. Écriture : fichiers d'abord (un nouveau fichier inclus doit exister avant configuration.yaml)
        foreach (var c in changes.Where(c => c.Target.Kind == ApplyKind.File)
                                 .OrderBy(c => c.Target.Key.Equals("configuration.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            string full = Path.Combine(SambaRoot, c.Target.Key);
            if (c.Content is null)
            {
                if (File.Exists(full)) File.Delete(full);
                Log($"OK  supprimé : {c.Target.Key}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // HA tourne sous Linux : fins de ligne « \n », UTF-8 sans BOM
            await File.WriteAllTextAsync(full, c.Content.Replace("\r\n", "\n"), Utf8, ct);
            Log($"OK  écrit : {c.Target.Key}");
        }

        foreach (var c in changes.Where(c => c.Target.Kind == ApplyKind.Dashboard && c.Content is not null))
        {
            await client.CommandAsync("lovelace/config/save", new JsonObject
            {
                ["url_path"] = c.Target.Key.Length == 0 ? null : c.Target.Key,
                ["config"] = JsonNode.Parse(c.Content!),
            }, ct);
            Log($"OK  enregistré : {c.Target}");
        }
        return folder;
    }

    // ================================================================ Rechargement

    private async Task ReloadAsync(HashSet<ApplyCategory> categories, CancellationToken ct)
    {
        if (categories.Contains(ApplyCategory.Z2m))
            Log("ABSENT  fichiers Zigbee2MQTT modifiés : redémarre l'add-on Zigbee2MQTT pour qu'ils soient pris en compte.");

        if (categories.Contains(ApplyCategory.Config))
        {
            var check = await CheckConfigAsync(ct);
            if (check.Valid == false)
                throw new InvalidOperationException(
                    $"Configuration invalide, HA n'a PAS été redémarré : {check.Errors}\n" +
                    "Corrige le fichier, ou « Restaurer la version précédente ».");
            Log(check.Valid == true ? "OK  configuration valide" : "ABSENT  vérification de la configuration indisponible : redémarrage quand même");

            await RestartAsync(ct);
            return;
        }

        if (categories.Contains(ApplyCategory.Theme))
        {
            await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
            await client.CommandAsync("call_service", new JsonObject { ["domain"] = "frontend", ["service"] = "reload_themes" }, ct);
            Log("OK  thèmes rechargés");
        }
    }

    /// <summary>Vérification de configuration de HA (POST /api/config/core/check_config).</summary>
    public async Task<ConfigCheck> CheckConfigAsync(CancellationToken ct = default)
    {
        Log("Vérification de la configuration par Home Assistant…");
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl() + "/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Hass.Token);
        try
        {
            using var response = await http.PostAsync("api/config/core/check_config", null, ct);
            if (!response.IsSuccessStatusCode) return new ConfigCheck(null, $"HTTP {(int)response.StatusCode}");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return new ConfigCheck(json?["result"]?.GetValue<string>() == "valid", json?["errors"]?.ToString() ?? "");
        }
        catch (HttpRequestException ex)
        {
            return new ConfigCheck(null, ex.Message);
        }
    }

    /// <summary>Redémarre HA puis attend qu'il réponde de nouveau (5 min max).</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        Log("Redémarrage de Home Assistant…");
        try
        {
            await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
            await client.CommandAsync("call_service", new JsonObject { ["domain"] = "homeassistant", ["service"] = "restart" }, ct);
        }
        catch (Exception ex) when (ex is not HassAuthException and not OperationCanceledException)
        {
            // La connexion se ferme pendant le redémarrage : attendu
        }

        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        var limit = DateTime.Now.AddMinutes(5);
        while (DateTime.Now < limit)
        {
            try
            {
                await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
                Log($"OK  Home Assistant a redémarré (version {client.HaVersion})");
                return;
            }
            catch (Exception ex) when (ex is not HassAuthException and not OperationCanceledException)
            {
                Log("… Home Assistant ne répond pas encore");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
        throw new TimeoutException("Home Assistant ne répond plus 5 minutes après le redémarrage : « Restaurer la version précédente » si besoin.");
    }

    // ================================================================ Sauvegardes

    public List<ApplyBackup> ListBackups()
    {
        string root = settings.Hass.ApplyBackupPath;
        if (!Directory.Exists(root)) return [];
        var list = new List<ApplyBackup>();
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ApplyBackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
                if (manifest is not null) list.Add(new ApplyBackup(dir, manifest));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* sauvegarde incomplète : ignorée */ }
        }
        return list.OrderByDescending(b => b.Manifest.Created).ToList();
    }

    private void Prune()
    {
        foreach (var old in ListBackups().Skip(Math.Max(1, settings.Hass.ApplyBackupKeep)))
        {
            try { Directory.Delete(old.Folder, recursive: true); } catch (IOException) { }
        }
    }

    // ================================================================ Outils

    private static async Task<JsonNode?> ReadDashboardAsync(HassClient client, string urlPath, CancellationToken ct)
    {
        try
        {
            return await client.CommandAsync("lovelace/config",
                new JsonObject { ["url_path"] = urlPath.Length == 0 ? null : urlPath, ["force"] = true }, ct);
        }
        catch (HassCommandException ex) when (ex.Code == "config_not_found")
        {
            return null;
        }
    }

    private string? ReadRaw(string haPath)
    {
        string full = Path.Combine(SambaRoot, haPath);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private bool IsExcluded(string repoPath)
    {
        string rel = Path.GetRelativePath(settings.Repositories.Private.Path, Path.Combine(ExportPath, repoPath)).Replace('\\', '/');
        return Exclusions.IsExcluded(rel, settings.Repositories.Private.Excluded)
               || rel.StartsWith(settings.Repositories.Private.ElementsFolder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsState(string path) =>
        settings.Hass.StateFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private void EnsureSamba()
    {
        if (string.IsNullOrWhiteSpace(SambaRoot) || !Directory.Exists(SambaRoot))
            throw new DirectoryNotFoundException(
                $"Partage Samba inaccessible : « {SambaRoot} ». Ouvre-le une fois dans l'Explorateur Windows.");
    }

    private string BaseUrl()
    {
        string url = settings.Hass.Url.Trim().TrimEnd('/');
        return url.Contains("://", StringComparison.Ordinal) ? url : "http://" + url;
    }

    private void Log(string message) => log?.Report(message);
}
