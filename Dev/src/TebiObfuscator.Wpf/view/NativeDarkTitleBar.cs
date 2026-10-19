using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Demande à Windows de dessiner la barre de titre en sombre.
///
/// La barre de titre n'appartient pas à l'application mais au gestionnaire de
/// fenêtres : aucune ressource WPF ne la touche. Sans cet appel, une
/// application entièrement sombre reste coiffée d'un bandeau clair, ce qui se
/// voit d'autant plus que le reste est foncé. L'alternative — redessiner soi-même
/// la barre avec WindowChrome — obligerait à réimplémenter le déplacement, le
/// redimensionnement, les boutons système et l'accrochage aux bords, pour un
/// résultat qui ne serait jamais tout à fait celui du système.
///
/// L'attribut existe depuis Windows 10 version 1809, sous deux numéros selon la
/// version. Les deux sont tentés, et un échec est sans conséquence : la fenêtre
/// garde simplement une barre claire.
/// </summary>
internal static class NativeDarkTitleBar
{
    /// <summary>Numéro de l'attribut à partir de Windows 10 version 2004.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>Numéro utilisé par les versions antérieures à 20H1.</summary>
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Applique le mode sombre à la barre de titre d'une fenêtre, dès que son
    /// handle natif existe.
    /// </summary>
    /// <param name="window">Fenêtre à habiller</param>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Le handle natif n'existe qu'à partir de SourceInitialized : appliquer
        // l'attribut plus tôt serait sans effet.
        window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle);
    }

    /// <summary>
    /// Applique l'attribut à un handle de fenêtre.
    /// </summary>
    /// <param name="handle">Handle natif de la fenêtre</param>
    private static void Apply(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        int enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) == 0)
        {
            return;
        }

        // Les versions antérieures à 20H1 emploient un autre numéro. Un second
        // échec reste sans conséquence : la fenêtre garde une barre claire, ce
        // qui vaut mieux qu'une exception au démarrage.
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    /// <summary>
    /// Règle un attribut de composition de fenêtre.
    /// </summary>
    /// <param name="handle">Handle natif de la fenêtre</param>
    /// <param name="attribute">Numéro de l'attribut</param>
    /// <param name="value">Valeur à appliquer</param>
    /// <param name="size">Taille de la valeur, en octets</param>
    /// <returns>Zéro en cas de succès</returns>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}
