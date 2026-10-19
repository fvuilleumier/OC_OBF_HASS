using System.Diagnostics;
using System.Text;

namespace TebiObfuscator.Cli.View;

/// <summary>
/// Accès au presse-papiers de Windows.
///
/// L'aller-retour avec un modèle de langage se fait presque toujours par
/// copier-coller : coller la réponse dans un fichier pour la restituer, puis
/// rouvrir le fichier, casse ce geste. L'accès passe par PowerShell plutôt que
/// par une dépendance d'interface graphique, afin que la View console reste un
/// simple exécutable sans pile d'affichage.
/// </summary>
internal static class ClipboardAccess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Chemins absolus des utilitaires système. Les désigner par leur nom seul
    /// laisserait la variable PATH décider de l'exécutable réellement lancé.
    /// </summary>
    private static readonly string PowerShellPath =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static readonly string ClipPath = Path.Combine(Environment.SystemDirectory, "clip.exe");

    /// <summary>
    /// Lit le contenu textuel du presse-papiers.
    /// </summary>
    /// <returns>Le texte lu, éventuellement vide</returns>
    /// <exception cref="InvalidOperationException">Si le presse-papiers est inaccessible</exception>
    public static string Read()
    {
        ProcessStartInfo startInfo = new(PowerShellPath)
        {
            // -Raw évite que PowerShell ne découpe le contenu en tableau de
            // lignes, ce qui perdrait les fins de ligne d'origine.
            Arguments = "-NoProfile -NonInteractive -Command \"Get-Clipboard -Raw\"",
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de lire le presse-papiers.");

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(Timeout);

        return output.TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Remplace le contenu du presse-papiers.
    /// </summary>
    /// <param name="text">Texte à y placer</param>
    /// <exception cref="InvalidOperationException">Si le presse-papiers est inaccessible</exception>
    public static void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        ProcessStartInfo startInfo = new(ClipPath)
        {
            RedirectStandardInput = true,
            StandardInputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible d'écrire dans le presse-papiers.");

        process.StandardInput.Write(text);
        process.StandardInput.Close();
        process.WaitForExit(Timeout);
    }
}
