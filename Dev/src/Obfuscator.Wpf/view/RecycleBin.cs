using System.Windows.Threading;
using Obfuscator.Core.Model.Exchange;
using VisualBasicIO = Microsoft.VisualBasic.FileIO;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Retire les fichiers d'un échange en les plaçant dans la corbeille de
/// Windows : une suppression qui n'aurait pas dû avoir lieu se rattrape.
///
/// La corbeille passe par l'interpréteur de commandes du système, qui peut
/// afficher une boîte d'erreur : l'appel est donc fait sur le fil de l'écran,
/// même quand l'échange s'écrit en arrière-plan.
/// </summary>
/// <param name="dispatcher">Fil de l'écran</param>
internal sealed class RecycleBin(Dispatcher dispatcher) : IFileRecycler
{
    /// <inheritdoc />
    public void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        dispatcher.Invoke(() => VisualBasicIO.FileSystem.DeleteFile(
            path,
            VisualBasicIO.UIOption.OnlyErrorDialogs,
            VisualBasicIO.RecycleOption.SendToRecycleBin));
    }
}
