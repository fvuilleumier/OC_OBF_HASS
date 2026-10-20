using System.Runtime.InteropServices;
using Obfuscator.Core.Model.Exchange;

namespace Obfuscator.Cli.View;

/// <summary>
/// Retire les fichiers d'un échange en les plaçant dans la corbeille de
/// Windows, comme le fait l'application graphique : une suppression qui
/// n'aurait pas dû avoir lieu se rattrape.
///
/// La ligne de commande est pilotée par une autre application, sans personne
/// devant l'écran : la corbeille est donc atteinte sans aucune boîte de
/// dialogue, ni confirmation ni message d'erreur. Un retrait qui échoue lève
/// une exception, que la commande rapporte.
/// </summary>
internal sealed class ShellRecycler : IFileRecycler
{
    /// <summary>Opération de suppression.</summary>
    private const uint DeleteOperation = 0x0003;

    /// <summary>Sans fenêtre de progression.</summary>
    private const ushort Silent = 0x0004;

    /// <summary>Sans demande de confirmation.</summary>
    private const ushort NoConfirmation = 0x0010;

    /// <summary>Vers la corbeille, d'où l'on peut revenir en arrière.</summary>
    private const ushort AllowUndo = 0x0040;

    /// <summary>Sans boîte d'erreur : l'échec est rendu par le code de retour.</summary>
    private const ushort NoErrorInterface = 0x0400;

    /// <inheritdoc />
    public void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // La liste des fichiers de l'interpréteur se termine par deux
        // caractères nuls : le marshalling en ajoute un, le chemin porte l'autre.
        ShellFileOperation operation = new()
        {
            Function = DeleteOperation,
            From = Path.GetFullPath(path) + '\0',
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorInterface),
        };

        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted || File.Exists(path))
        {
            throw new IOException($"Impossible de placer « {Path.GetFileName(path)} » dans la corbeille (code {result}).");
        }
    }

    /// <summary>Opère sur des fichiers comme l'explorateur.</summary>
    /// <param name="operation">Description de l'opération</param>
    /// <returns>0 en cas de succès</returns>
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

    /// <summary>Description d'une opération sur des fichiers (<c>SHFILEOPSTRUCTW</c>).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        /// <summary>Fenêtre propriétaire des boîtes de dialogue, aucune ici.</summary>
        public nint Window;

        /// <summary>Opération demandée.</summary>
        public uint Function;

        /// <summary>Fichiers visés, séparés par des caractères nuls.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;

        /// <summary>Destination, sans objet pour une suppression.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;

        /// <summary>Options de l'opération.</summary>
        public ushort Flags;

        /// <summary>Vrai si l'opération a été interrompue.</summary>
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;

        /// <summary>Correspondances de noms, sans objet ici.</summary>
        public nint NameMappings;

        /// <summary>Titre de la progression, sans objet ici.</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }
}
