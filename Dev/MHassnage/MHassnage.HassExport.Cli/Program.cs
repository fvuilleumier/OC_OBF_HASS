using MHassnage.Core.Service;

namespace MHassnage.HassExport.Cli;

/// <summary>Console mince : exporte HA vers le dépôt privé (export implémenté à l'étape 2).</summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("MHassnage — export Home Assistant");

        var settings = SettingsService.Load();
        var problems = SettingsService.Validate(settings);
        if (problems.Count > 0)
        {
            Console.WriteLine("Réglages à compléter :");
            foreach (var problem in problems) Console.WriteLine($"  - {problem}");
        }

        Console.WriteLine($"Destination : {settings.Repositories.Private.ExportPath}");
        Console.WriteLine("L'export sera branché à l'étape 2.");
        return 0;
    }
}
