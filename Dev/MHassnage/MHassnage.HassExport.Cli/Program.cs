using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;
using MHassnage.Core.Service;

namespace MHassnage.HassExport.Cli;

/// <summary>
/// Console mince : exporte Hass vers le dépôt privé.
///   mhassnage-export                 → export dans Repositories:Private (ExportFolder)
///   mhassnage-export --output &lt;dir&gt;  → export dans un autre dossier (comparaison, test)
///   mhassnage-export --no-states     → sans les states (Elements/states.jsonc, Z2m/state.json)
/// Codes de retour : 0 OK, 1 erreur, 2 jeton absent ou refusé, 3 réglages incomplets.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8; // accents corrects dans la console Windows
        ColorConsole.Title("MHassnage — export Home Assistant");

        AppSettings settings;
        try
        {
            settings = SettingsService.Load();
        }
        catch (Exception ex)
        {
            ColorConsole.Error($"Configuration illisible ({SettingsService.SettingsPath}) : {ex.Message}");
            return 1;
        }

        ColorConsole.Info($"Configuration : {SettingsService.SettingsPath}"
                          + (SettingsService.CreatedFromExample ? " (créée à partir du modèle)" : ""));

        var problems = SettingsService.Validate(settings);
        if (problems.Count > 0)
        {
            ColorConsole.Warning("Réglages à compléter :");
            foreach (var problem in problems) ColorConsole.Warning($"  - {problem}");
        }

        if (problems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
        {
            Console.WriteLine();
            ColorConsole.Warning(HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath));
            return 2;
        }
        if (problems.Any(p => p.StartsWith("Hass:Url", StringComparison.Ordinal)
                           || p.StartsWith("Repositories:Private", StringComparison.Ordinal)))
            return 3;

        string? output = OptionValue(args, "--output", "-o");

        try
        {
            await new HassExportService(settings, new SynchronousProgress(ColorConsole.Log)).ExportAsync(output, includeStates: !args.Contains("--no-states", StringComparer.OrdinalIgnoreCase));
            ColorConsole.Success("Export terminé.");
            return 0;
        }
        catch (HassAuthException ex)
        {
            ColorConsole.Error($"Jeton refusé par Home Assistant : {ex.Message}");
            Console.WriteLine();
            ColorConsole.Warning(HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath));
            return 2;
        }
        catch (Exception ex)
        {
            ColorConsole.Error($"Erreur : {ex.Message}");
            return 1;
        }
    }

    private static string? OptionValue(string[] args, params string[] names)
    {
        int i = Array.FindIndex(args, a => names.Contains(a, StringComparer.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }

    /// <summary>Progress&lt;T&gt; poste sur le pool en console : on veut l'ordre exact des lignes.</summary>
    private sealed class SynchronousProgress(Action<string> write) : IProgress<string>
    {
        public void Report(string value) => write(value);
    }
}
