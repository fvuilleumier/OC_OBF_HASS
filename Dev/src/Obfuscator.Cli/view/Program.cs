using Spectre.Console;
using Spectre.Console.Cli;
using Obfuscator.Cli.View.Commands;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Cli.View;

/// <summary>
/// Point d'entrée de la View console.
///
/// Les commandes suivent l'ordre naturel du travail : on initialise un projet,
/// on relève ce qui serait masqué, on arbitre, on produit les fichiers, puis on
/// restitue la réponse du modèle. La séquence relevé → revue → obfuscation
/// existe parce qu'envoyer un fichier à un tiers est irréversible : il faut
/// pouvoir regarder avant d'agir.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Configure et exécute l'application en ligne de commande.
    /// </summary>
    /// <param name="args">Arguments transmis par le système</param>
    /// <returns>Code de sortie : 0 en cas de succès, 2 si une vérification échoue</returns>
    internal static int Main(string[] args)
    {
        CommandApp app = new();

        app.Configure(config =>
        {
            config.SetApplicationName("obfcli");
            config.SetApplicationVersion(ProductVersion.Current);
            config.UseStrictParsing();

            config.AddCommand<InitCommand>("init")
                .WithDescription("Crée un projet d'obfuscation dans un dossier.")
                .WithExample("init", "MonProjet", "--profile", "network");

            config.AddCommand<ScanCommand>("scan")
                .WithDescription("Relève ce qui serait masqué, sans rien écrire.")
                .WithExample("scan", "./in");

            config.AddCommand<ReviewCommand>("review")
                .WithDescription("Arbitre catégorie par catégorie et valeur par valeur.")
                .WithExample("review");

            config.AddCommand<ObfuscateCommand>("obfuscate")
                .WithDescription("Produit les fichiers obfusqués, leur légende et le rapport.")
                .WithExample("obfuscate", "./in", "-o", "./out");

            config.AddCommand<VerifyCommand>("verify")
                .WithDescription("Contrôle qu'aucune valeur réelle ne subsiste dans les sorties.")
                .WithExample("verify", "./out");

            config.AddCommand<DeobfuscateCommand>("deobfuscate")
                .WithDescription("Restitue les valeurs réelles dans un fichier ou le presse-papiers.")
                .WithExample("deobfuscate", "--clipboard");

            // Une erreur attendue — projet absent, chemin introuvable — doit
            // produire un message lisible et non une trace d'exception.
            config.SetExceptionHandler((exception, _) =>
            {
                AnsiConsole.MarkupLine("[red]{0}[/]", exception.Message.EscapeMarkup());
                return 1;
            });
        });

        return app.Run(args);
    }
}
