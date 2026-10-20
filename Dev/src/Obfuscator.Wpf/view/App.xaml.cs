using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Point d'entrée de l'application graphique.
///
/// Sans argument, elle ouvre sa fenêtre comme elle l'a toujours fait. Avec
/// <c>[dossierProjet] [--publish | --return]</c>, elle ouvre ce projet et, le
/// cas échéant, la liste à confirmer du geste demandé : c'est ainsi qu'une
/// autre application lui passe la main quand un échange attend une décision.
/// Si une instance tourne déjà, la demande lui est transmise et cette
/// instance-ci s'arrête aussitôt.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Une application WPF n'est pas libérée par Dispose : l'instance unique est libérée à la sortie, dans OnExit.")]
public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>
    /// Lit les arguments, transmet la demande à l'instance déjà ouverte s'il y
    /// en a une, sinon ouvre la fenêtre et lui confie la demande.
    /// </summary>
    /// <param name="e">Arguments de lancement</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        LaunchRequest request = LaunchRequest.Parse(e.Args, Directory.GetCurrentDirectory());
        _instance = new SingleInstance();
        bool primary = _instance.TryBecomePrimary();

        // Une demande valide part vers la fenêtre déjà ouverte : une seconde
        // fenêtre sur le même coffre se contredirait avec la première. Un
        // lancement sans argument, lui, ouvre sa fenêtre comme toujours.
        if (!primary && !request.IsEmpty && request.Error is null && _instance.TrySend(request))
        {
            Shutdown(0);
            return;
        }

        MainWindow window = new();
        MainWindow = window;
        window.Show();

        if (primary)
        {
            _instance.Listen(received => Dispatcher.InvokeAsync(() => window.HandleLaunch(received)));
        }

        // La demande est traitée une fois la fenêtre affichée : ses boîtes de
        // dialogue ont besoin d'une fenêtre propriétaire visible.
        if (!request.IsEmpty)
        {
            window.ContentRendered += OnFirstRender;

            void OnFirstRender(object? sender, EventArgs args)
            {
                window.ContentRendered -= OnFirstRender;
                window.HandleLaunch(request);
            }
        }
    }

    /// <summary>
    /// Rend le verrou de l'instance unique et arrête l'écoute.
    /// </summary>
    /// <param name="e">Données de fermeture</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _instance = null;
        base.OnExit(e);
    }
}
