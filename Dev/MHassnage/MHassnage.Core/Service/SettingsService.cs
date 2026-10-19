using System.Reflection;
using MHassnage.Core.Model;
using Microsoft.Extensions.Configuration;

namespace MHassnage.Core.Service;

/// <summary>
/// Charge la configuration : appsettings.json, puis appsettings.local.json (non versionné),
/// puis user-secrets (jeton HA).
/// </summary>
public static class SettingsService
{
    public static AppSettings Load(string? baseDirectory = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(SettingsService).Assembly, optional: true)
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);
        return settings;
    }

    /// <summary>Liste les réglages manquants ou à compléter (sans jamais afficher de valeur).</summary>
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var problems = new List<string>();

        static bool Missing(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Contains("À_COMPLÉTER", StringComparison.Ordinal);

        if (Missing(settings.Hass.Url)) problems.Add("Hass:Url");
        if (Missing(settings.Hass.Token)) problems.Add("Hass:Token (dotnet user-secrets set \"Hass:Token\" \"…\")");
        if (Missing(settings.Repositories.Private.Path)) problems.Add("Repositories:Private:Path");
        if (Missing(settings.Repositories.Public.Path)) problems.Add("Repositories:Public:Path");
        if (Missing(settings.Obfuscator.CliPath)) problems.Add("Obfuscator:CliPath");
        if (Missing(settings.Obfuscator.WpfPath)) problems.Add("Obfuscator:WpfPath");
        if (Missing(settings.Obfuscator.ProjectFolder)) problems.Add("Obfuscator:ProjectFolder");

        return problems;
    }
}
