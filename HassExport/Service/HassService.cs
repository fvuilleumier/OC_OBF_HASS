using System.Text.Json;
using HassExport.Model;
using Microsoft.Playwright;

namespace HassExport.Service;

public class HassService(HassSettings settings)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void AfficherAide()
    {
        Console.WriteLine("""
            ================================================================
             HassExport - à faire la PREMIÈRE fois
            ================================================================
             1. Renseigner l'URL de HA dans appsettings.json (ex: http://192.168.x.x:8123)
             2. dotnet build
             3. Installer Chromium pour Playwright (une seule fois) :
                  pwsh bin/Debug/net10.0/playwright.ps1 install chromium
             4. Mettre "Headless": false, lancer : dotnet run
                -> Chromium s'ouvre : se connecter à Home Assistant
                   (cocher "Rester connecté"). La session est mémorisée
                   dans le dossier ProfileFolder.
             5. Ensuite on peut passer "Headless": true.
             Session expirée ? Supprimer ProfileFolder et refaire l'étape 4.
             Fichiers YAML (automations, themes, z2m...) : module "Samba share"
               installé dans HA, puis ouvrir une fois DefaultHassPath
               (\\IP\config) dans l'Explorateur et mémoriser les identifiants.
             Dashboards : UrlPath = fin de l'URL du dashboard
               (http://IP:port/<UrlPath>/...), "lovelace" = défaut.
            ================================================================
            """);
    }

    public async Task ExportAsync()
    {
        var url = settings.Url.Trim();
        if (url.Contains("À_COMPLÉTER") || url.Length == 0)
            throw new InvalidOperationException("Url non renseignée dans appsettings.json.");
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        using var playwright = await Playwright.CreateAsync();

        var profile = Path.GetFullPath(settings.ProfileFolder);
        Console.WriteLine($"Profil navigateur : {profile}");

        IBrowserContext context;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profile, new BrowserTypeLaunchPersistentContextOptions { Headless = settings.Headless });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            throw new InvalidOperationException(
                "Chromium n'est pas installé : pwsh bin/Debug/net10.0/playwright.ps1 install chromium");
        }

        await using (context)
        {
            // Nouvel onglet dédié (l'onglet about:blank initial du profil persistant est ignoré)
            var page = context.Pages.First();

            Console.WriteLine($"Navigation vers {url} ...");
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
            Console.WriteLine($"Page chargée : {page.Url}");

            await AttendreHassAsync(page);

            var elements = Path.Combine(settings.OutputFolder, settings.ElementsFolder);
            Directory.CreateDirectory(elements);

            foreach (var key in settings.Keys)
            {
                // Sérialisation côté navigateur : hass contient des objets circulaires (connection, auth...)
                var json = await page.EvaluateAsync<string?>(
                    "k => JSON.stringify(document.querySelector('home-assistant').hass[k] ?? null)", key);

                var pretty = JsonSerializer.Serialize(JsonDocument.Parse(json ?? "null").RootElement, Indented);
                var content = $"// document.querySelector('home-assistant').hass.{key}\n{pretty}\n";

                var file = Path.Combine(elements, $"{key}.jsonc");
                await File.WriteAllTextAsync(file, content);
                Console.WriteLine($"OK  {file}");
            }

            await ExporterDashboardsAsync(page);
        }
    }

    /// <summary>
    /// Dashboards en mode UI : lecture via l'API WebSocket (lovelace/config), écriture en YAML,
    /// puis une copie par vue dans Dashboards\views\[FileName]\.
    /// </summary>
    private async Task ExporterDashboardsAsync(IPage page)
    {
        var dossier = Path.Combine(settings.OutputFolder, settings.Dashboards.Destination);

        foreach (var d in settings.Dashboards.Items)
        {
            string? urlPath = d.UrlPath is "" or "lovelace" ? null : d.UrlPath;
            string json;
            try
            {
                json = await page.EvaluateAsync<string>(
                    @"async p => JSON.stringify(await document.querySelector('home-assistant').hass
                        .callWS({ type: 'lovelace/config', url_path: p }))", urlPath);
            }
            catch (PlaywrightException ex)
            {
                Console.WriteLine($"ERREUR  dashboard '{d.UrlPath}' : {ex.Message.Split('\n')[0]}");
                continue;
            }

            var config = JsonDocument.Parse(json).RootElement;
            var nom = NomFichier(string.IsNullOrWhiteSpace(d.FileName) ? d.UrlPath : d.FileName);

            Directory.CreateDirectory(dossier);
            var file = Path.Combine(dossier, $"{nom}.yaml");
            await File.WriteAllTextAsync(file, YamlService.ToYaml(config));
            Console.WriteLine($"OK  {file}");

            if (!config.TryGetProperty("views", out var views)) continue;

            var dossierVues = Path.Combine(dossier, "views", nom);
            Directory.CreateDirectory(dossierVues);
            var i = 0;
            foreach (var view in views.EnumerateArray())
            {
                i++;
                var nomVue = view.TryGetProperty("path", out var p) ? p.GetString()
                           : view.TryGetProperty("title", out var t) ? t.GetString()
                           : null;
                var fileVue = Path.Combine(dossierVues, $"{i:00} - {NomFichier(nomVue ?? "vue")}.yaml");
                await File.WriteAllTextAsync(fileVue, YamlService.ToYaml(view));
                Console.WriteLine($"OK  {fileVue}");
            }
        }
    }

    private static string NomFichier(string nom) =>
        string.Concat(nom.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private async Task AttendreHassAsync(IPage page)
    {
        const string test = "() => !!document.querySelector('home-assistant')?.hass?.states";
        var limite = DateTime.Now.AddMinutes(5);

        while (DateTime.Now < limite)
        {
            try
            {
                if (await page.EvaluateAsync<bool>(test)) return;
            }
            catch (PlaywrightException) { /* page en cours de navigation (login -> accueil) */ }

            if (page.Url.Contains("/auth/"))
            {
                if (settings.Headless)
                    throw new InvalidOperationException(
                        "Session absente ou expirée : repasser \"Headless\": false et se reconnecter.");
                Console.WriteLine(">> Connecte-toi à Home Assistant dans la fenêtre Chromium (coche \"Rester connecté\").");
            }
            else
            {
                Console.WriteLine($"Attente de hass... (page actuelle : {page.Url})");
            }

            await Task.Delay(5_000);
        }

        throw new TimeoutException("hass n'est pas disponible après 5 minutes.");
    }
}
