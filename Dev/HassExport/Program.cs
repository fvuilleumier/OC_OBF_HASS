using HassExport.Model;
using HassExport.Service;
using Microsoft.Extensions.Configuration;

namespace HassExport;

public static class Program
{
    public static async Task<int> Main()
    {
        HassService.AfficherAide();

        var settings = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build()
            .GetSection("Hass")
            .Get<HassSettings>() ?? throw new InvalidOperationException("Section 'Hass' manquante.");

        try
        {
            new FileService(settings).Export();
            await new HassService(settings).ExportAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erreur : {ex.Message}");
            return 1;
        }
    }
}
