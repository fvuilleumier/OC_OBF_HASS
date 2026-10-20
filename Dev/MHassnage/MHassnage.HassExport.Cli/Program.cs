using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.HassExport.Cli;

/// <summary>Console mince : exporte HA vers le dépôt privé (export implémenté à l'étape 2).</summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("MHassnage — export Home Assistant");

        var settings = SettingsService.Load();
        Console.WriteLine($"Configuration : {SettingsService.SettingsPath}"
                          + (SettingsService.CreatedFromExample ? " (créée à partir du modèle)" : ""));
        var problems = SettingsService.Validate(settings);
        if (problems.Count > 0)
        {
            Console.WriteLine("Réglages à compléter :");
            foreach (var problem in problems) Console.WriteLine($"  - {problem}");
        }

        if (problems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
        {
            Console.WriteLine();
            Console.WriteLine(HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath));
        }

        Console.WriteLine($"Destination : {settings.Repositories.Private.ExportPath}");
        Console.WriteLine("L'export sera branché à l'étape 2.");
        return 0;
    }
}
