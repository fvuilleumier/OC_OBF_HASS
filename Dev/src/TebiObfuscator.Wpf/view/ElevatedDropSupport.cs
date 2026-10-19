using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Rétablit le dépôt de fichiers depuis l'Explorateur lorsque l'application
/// tourne en administrateur.
///
/// Windows isole les processus élevés de ceux qui ne le sont pas : l'Explorateur
/// ne peut pas déposer de fichiers, par le mécanisme habituel, sur une fenêtre
/// d'un niveau supérieur au sien. C'est le cas dès que l'application est lancée
/// depuis un environnement de développement ouvert en administrateur — le
/// curseur affiche alors un sens interdit, sans autre explication.
///
/// Le seul chemin que Windows laisse ouvert est l'ancien message de dépôt de
/// fichiers, à condition de l'autoriser explicitement pour la fenêtre. Ce mode
/// n'offre aucun retour pendant le survol, mais le dépôt fonctionne. Il n'est
/// activé qu'en administrateur : partout ailleurs, le mécanisme habituel reste
/// en place, avec sa mise en évidence de la zone de dépôt.
/// </summary>
internal static class ElevatedDropSupport
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MessageFilterAllow = 1;
    private const uint QueryFileCount = 0xFFFFFFFF;

    /// <summary>Vrai si le processus tourne avec des droits d'administrateur.</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>
    /// Active le dépôt par message sur une fenêtre, si le processus est élevé.
    /// </summary>
    /// <param name="window">Fenêtre à équiper</param>
    /// <param name="onDrop">Action appelée avec les chemins déposés</param>
    /// <returns><c>true</c> si le mode de secours a été activé</returns>
    public static bool EnableIfElevated(Window window, Action<string[]> onDrop)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(onDrop);

        if (!IsElevated)
        {
            return false;
        }

        // L'enregistrement habituel de la fenêtre comme cible de dépôt a lieu
        // lorsque son contenu est affiché : c'est donc après le chargement
        // qu'il faut le retirer, sans quoi l'Explorateur continuerait de
        // l'essayer — en vain.
        window.Loaded += (_, _) =>
        {
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0)
            {
                return;
            }

            _ = RevokeDragDrop(handle);
            DragAcceptFiles(handle, true);
            _ = ChangeWindowMessageFilterEx(handle, WmDropFiles, MessageFilterAllow, 0);
            _ = ChangeWindowMessageFilterEx(handle, WmCopyData, MessageFilterAllow, 0);
            _ = ChangeWindowMessageFilterEx(handle, WmCopyGlobalData, MessageFilterAllow, 0);

            HwndSource.FromHwnd(handle)?.AddHook((nint _, int message, nint wParam, nint _, ref bool handled) =>
            {
                if (message == WmDropFiles)
                {
                    onDrop(ReadDroppedFiles(wParam));
                    handled = true;
                }

                return 0;
            });
        };

        return true;
    }

    /// <summary>
    /// Lit les chemins transmis par un message de dépôt, puis libère le
    /// descripteur.
    /// </summary>
    /// <param name="drop">Descripteur du dépôt</param>
    /// <returns>Les chemins déposés</returns>
    private static string[] ReadDroppedFiles(nint drop)
    {
        try
        {
            uint count = DragQueryFile(drop, QueryFileCount, null, 0);
            string[] paths = new string[count];

            for (uint index = 0; index < count; index++)
            {
                uint length = DragQueryFile(drop, index, null, 0);
                char[] buffer = new char[length + 1];
                _ = DragQueryFile(drop, index, buffer, length + 1);
                paths[index] = new string(buffer, 0, (int)length);
            }

            return paths;
        }
        finally
        {
            DragFinish(drop);
        }
    }

    /// <summary>
    /// Détermine si le processus tourne avec des droits d'administrateur.
    /// </summary>
    /// <returns><c>true</c> si le jeton du processus est élevé</returns>
    private static bool DetectElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Retire une fenêtre des cibles de dépôt habituelles.</summary>
    /// <param name="handle">Handle de la fenêtre</param>
    /// <returns>Code de retour du système, ignoré</returns>
    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RevokeDragDrop(nint handle);

    /// <summary>Déclare qu'une fenêtre accepte les messages de dépôt de fichiers.</summary>
    /// <param name="handle">Handle de la fenêtre</param>
    /// <param name="accept">Vrai pour accepter</param>
    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void DragAcceptFiles(nint handle, [MarshalAs(UnmanagedType.Bool)] bool accept);

    /// <summary>Lit le nombre de fichiers déposés, ou le chemin de l'un d'eux.</summary>
    /// <param name="drop">Descripteur du dépôt</param>
    /// <param name="index">Rang du fichier, ou la valeur qui demande le nombre de fichiers</param>
    /// <param name="buffer">Tampon recevant le chemin, ou <c>null</c> pour en connaître la longueur</param>
    /// <param name="size">Taille du tampon, en caractères</param>
    /// <returns>Le nombre de fichiers, ou la longueur du chemin</returns>
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint DragQueryFile(nint drop, uint index, [Out] char[]? buffer, uint size);

    /// <summary>Libère le descripteur d'un dépôt.</summary>
    /// <param name="drop">Descripteur du dépôt</param>
    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void DragFinish(nint drop);

    /// <summary>Autorise un message venu d'un processus de niveau inférieur.</summary>
    /// <param name="handle">Handle de la fenêtre</param>
    /// <param name="message">Message à autoriser</param>
    /// <param name="action">Action sur le filtre</param>
    /// <param name="changeInfo">Informations en retour, inutilisées</param>
    /// <returns><c>true</c> en cas de succès</returns>
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint handle, uint message, uint action, nint changeInfo);
}
