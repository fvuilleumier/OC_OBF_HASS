using System.Globalization;
using System.Text.Json.Nodes;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;

namespace MHassnage.Core.Service;

/// <summary>
/// Relevé de santé et de sécurité de HA via WebSocket, en lecture seule.
/// Chaque commande est facultative : si la version de HA ne la connaît pas, elle est ignorée.
/// </summary>
public sealed class HassHealthService(AppSettings settings)
{
    private HealthSettings Limits => settings.Health;

    /// <exception cref="HassAuthException">Jeton refusé.</exception>
    public async Task<HassHealth> LoadAsync(CancellationToken ct = default)
    {
        await using var client = await HassClient.ConnectAsync(settings.Hass.Url, settings.Hass.Token, ct);
        var health = new HassHealth { Version = client.HaVersion };

        async Task<JsonNode?> Try(string command)
        {
            try { return await client.CommandAsync(command, ct: ct); }
            catch (HassCommandException) { health.Skipped.Add(command); return null; }
        }

        var states = (await Try("get_states"))?.AsArray() ?? [];
        health.Devices = (await Try("config/device_registry/list"))?.AsArray().Count ?? 0;
        health.Areas = (await Try("config/area_registry/list"))?.AsArray().Count ?? 0;

        ReadStates(health, states, Limits);
        ReadRepairs(health, await Try("repairs/list_issues"));
        ReadNotifications(health, await Try("persistent_notification/get"));
        ReadBackups(health, await Try("backup/info"));
        ReadSecurity(health, Limits,
            await Try("get_config"),
            await Try("config/auth/list"),
            await Try("auth/current_user"),
            await Try("auth/refresh_tokens"));
        return health;
    }

    private static void ReadStates(HassHealth health, JsonArray states, HealthSettings limits)
    {
        health.Entities = states.Count;
        var domains = new Dictionary<string, int>();

        foreach (var s in states.OfType<JsonObject>())
        {
            string id = Str(s["entity_id"]);
            string state = Str(s["state"]);
            var attr = s["attributes"] as JsonObject;
            string name = Str(attr?["friendly_name"]) is { Length: > 0 } n ? n : id;
            string domain = id.Contains('.') ? id[..id.IndexOf('.')] : id;
            domains[domain] = domains.GetValueOrDefault(domain) + 1;

            switch (domain)
            {
                case "update" when state == "on":
                    health.Updates.Add(new HealthItem(Str(attr?["title"]) is { Length: > 0 } t ? t : name,
                        $"{Str(attr?["installed_version"])} → {Str(attr?["latest_version"])}"));
                    break;
                case "automation":
                    health.Automations++;
                    if (state == "off") health.AutomationsOff.Add(new HealthItem(name, "désactivée"));
                    break;
            }

            if (state is "unavailable" or "unknown" && domain is not ("update" or "button" or "scene" or "event"))
                health.Unavailable.Add(new HealthItem(name, state == "unavailable" ? "indisponible" : "inconnu"));

            if (Str(attr?["device_class"]) == "battery"
                && double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out double level)
                && level < limits.BatteryWarningPercent)
                health.LowBatteries.Add(new HealthItem(name, $"{level:0} %", Serious: level < limits.BatteryCriticalPercent));
        }

        health.Domains.AddRange(domains.OrderByDescending(d => d.Value).Select(d => (d.Key, d.Value)));
        health.LowBatteries.Sort((a, b) => string.CompareOrdinal(a.Detail, b.Detail));
    }

    private static void ReadRepairs(HassHealth health, JsonNode? result)
    {
        if (result?["issues"] is not JsonArray issues) return;
        foreach (var i in issues.OfType<JsonObject>().Where(i => i["ignored"]?.GetValue<bool>() != true))
        {
            string severity = Str(i["severity"]);
            string text = Str(i["translation_key"]) is { Length: > 0 } key ? key : Str(i["issue_id"]);
            string breaks = Str(i["breaks_in_ha_version"]);
            health.Repairs.Add(new HealthItem($"{Str(i["domain"])} · {text.Replace('_', ' ')}",
                breaks.Length > 0 ? $"{severity} · cassera en {breaks}" : severity,
                Serious: severity is "error" or "critical" || breaks.Length > 0));
        }
    }

    private static void ReadNotifications(HassHealth health, JsonNode? result)
    {
        if (result is not JsonArray list) return;
        foreach (var n in list.OfType<JsonObject>())
            health.Notifications.Add(new HealthItem(Str(n["title"]) is { Length: > 0 } t ? t : Str(n["notification_id"])));
    }

    private static void ReadBackups(HassHealth health, JsonNode? result)
    {
        if (result?["backups"] is not JsonArray backups) return;
        health.BackupCount = backups.Count;
        health.LastBackup = backups
            .Select(b => DateTimeOffset.TryParse(Str(b?["date"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null)
            .Where(d => d is not null)
            .Max();
    }

    private static void ReadSecurity(HassHealth health, HealthSettings limits, JsonNode? config, JsonNode? users, JsonNode? me, JsonNode? tokens)
    {
        // Accès depuis Internet en clair
        string external = Str(config?["external_url"]);
        if (external.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            health.Security.Add(new HealthItem("URL externe en HTTP", "les identifiants transitent en clair", Serious: true));
        else if (external.Length > 0)
            health.Security.Add(new HealthItem("URL externe en HTTPS", "ok"));

        // Comptes
        if (users is JsonArray list)
        {
            var humans = list.OfType<JsonObject>()
                .Where(u => u["system_generated"]?.GetValue<bool>() != true && u["is_active"]?.GetValue<bool>() != false)
                .ToList();
            var admins = humans.Where(u => u["group_ids"] is JsonArray g && g.Any(x => Str(x) == "system-admin")).ToList();
            health.Security.Add(new HealthItem($"{admins.Count} administrateur(s) sur {humans.Count} compte(s) actif(s)",
                string.Join(", ", admins.Select(a => Str(a["name"]))), Serious: admins.Count > limits.MaxAdmins));
        }

        // Double authentification du compte du jeton
        if (me?["mfa_modules"] is JsonArray mfa)
        {
            bool enabled = mfa.Any(m => m?["enabled"]?.GetValue<bool>() == true);
            health.Security.Add(new HealthItem(enabled ? "Double authentification active" : "Double authentification désactivée",
                $"compte « {Str(me["name"])} »", Serious: !enabled));
        }

        // Jetons longue durée : nombre et jetons dormants
        if (tokens is JsonArray all)
        {
            var longLived = all.OfType<JsonObject>().Where(t => Str(t["type"]) == "long_lived_access_token").ToList();
            var stale = longLived.Where(t =>
                !DateTimeOffset.TryParse(Str(t["last_used_at"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var used)
                || (DateTimeOffset.Now - used).TotalDays > limits.StaleTokenDays).ToList();
            health.Security.Add(new HealthItem($"{longLived.Count} jeton(s) longue durée",
                stale.Count > 0 ? $"{stale.Count} inutilisé(s) depuis {limits.StaleTokenDays} j : {string.Join(", ", stale.Select(t => Str(t["client_name"])))}" : "tous utilisés récemment",
                Serious: stale.Count > 0));
        }
    }

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : node?.ToString() ?? "";
}
