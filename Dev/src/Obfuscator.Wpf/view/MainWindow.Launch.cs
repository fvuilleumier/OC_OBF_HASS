using System.Windows;
using System.Windows.Interop;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Demandes de lancement : ouvrir un projet, et le cas échéant sa
/// publication ou son rapatriement, à la demande d'une autre application.
///
/// Elles arrivent par les arguments de lancement, ou d'une instance lancée
/// ensuite qui les a transmises à celle-ci. Le projet est ouvert comme s'il
/// avait été choisi parmi les projets récents, puis le geste suit le chemin
/// habituel de l'écran : rien n'est écrit sans la confirmation de l'utilisateur.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Vrai si le prochain rapatriement doit ouvrir d'emblée son premier
    /// conflit : il a été demandé au lancement.
    /// </summary>
    private bool _launchOpensConflicts;

    /// <summary>
    /// Traite une demande de lancement : met la fenêtre au premier plan, ouvre
    /// le projet, puis la liste à confirmer du geste demandé.
    /// </summary>
    /// <param name="request">Demande lue des arguments ou reçue d'une autre instance</param>
    internal void HandleLaunch(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        BringToFront();

        if (request.Error is not null)
        {
            Inform(request.Error);
            return;
        }

        // Une boîte de dialogue ouverte, ou un échange en cours d'écriture,
        // ne doit pas voir le projet changer sous elle : la demande attend que
        // l'utilisateur ait fini.
        if (_busy || ComponentDispatcher.IsThreadModal)
        {
            SetStatus("Demande reçue d'une autre application, mais une opération est en cours : terminez-la, puis relancez la demande.", success: false);
            return;
        }

        if (request.ProjectPath is not null && !OpenLaunchedProject(request.ProjectPath))
        {
            return;
        }

        switch (request.Gesture)
        {
            case LaunchGesture.Publish:
                OnObfuscate(this, new RoutedEventArgs());
                break;

            case LaunchGesture.Return:
                _launchOpensConflicts = true;
                OnReturn(this, new RoutedEventArgs());
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Ouvre le projet d'une demande de lancement, s'il n'est pas déjà ouvert.
    /// </summary>
    /// <param name="path">Dossier du projet, absolu</param>
    /// <returns><c>true</c> si le projet est ouvert</returns>
    private bool OpenLaunchedProject(string path)
    {
        if (_project is not null && SameFolder(_project.RootPath, path))
        {
            return true;
        }

        // Une demande ne crée jamais de projet : un dossier qui n'en abrite
        // pas est une erreur de l'application qui l'a envoyée.
        if (!IsProjectFolder(path))
        {
            Inform($"« {path} » ne contient pas de projet Obfuscator. L'écran habituel reste affiché.");
            return false;
        }

        CommitProjectName();
        OpenProjectAt(path, create: false);
        return _project is not null && SameFolder(_project.RootPath, path);
    }

    /// <summary>
    /// Ramène la fenêtre au premier plan, restaurée si elle était réduite.
    /// </summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();

        // Basculer « au premier plan » force Windows à montrer la fenêtre
        // même quand une autre application a le focus.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// Affiche un message sur une demande de lancement inutilisable, et le
    /// garde dans le bandeau d'état.
    /// </summary>
    /// <param name="message">Message</param>
    private void Inform(string message)
    {
        SetStatus(message, success: false);
        MessageBox.Show(this, message, "Obfuscator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
