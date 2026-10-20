using System.Windows;
using System.Windows.Interop;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Console de la fenêtre principale : les messages du bandeau d'état en
/// entier, avec leur historique, à lire, sélectionner et copier.
///
/// Elle n'est pas modale : on la garde ouverte à côté de la fenêtre principale
/// pour suivre une analyse, et chaque nouveau message s'y ajoute.
/// </summary>
public partial class ConsoleWindow : Window
{
    private readonly StatusJournal _journal;

    /// <summary>
    /// Construit la console sur le journal des messages.
    /// </summary>
    /// <param name="journal">Journal à présenter, partagé avec la fenêtre principale</param>
    internal ConsoleWindow(StatusJournal journal)
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);

        _journal = journal;
        ConsoleText.Text = journal.FormatAll();

        // Le message le plus récent est celui qu'on vient chercher : la
        // console s'ouvre en bas.
        Loaded += (_, _) => ConsoleText.ScrollToEnd();
    }

    /// <summary>
    /// Ajoute un message à la console, et la fait défiler jusqu'à lui si l'on
    /// était déjà en bas — sans arracher la lecture d'un message plus ancien.
    /// </summary>
    /// <param name="entry">Message ajouté au journal</param>
    internal void Append(StatusEntry entry)
    {
        bool atEnd = ConsoleText.VerticalOffset + ConsoleText.ViewportHeight >= ConsoleText.ExtentHeight - 1;
        string line = StatusJournal.Format(entry);

        ConsoleText.AppendText(ConsoleText.Text.Length == 0 ? line : Environment.NewLine + line);

        if (atEnd)
        {
            ConsoleText.ScrollToEnd();
        }
    }

    /// <summary>
    /// Copie toute la console dans le presse-papiers.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (ConsoleText.Text.Length == 0)
        {
            return;
        }

        try
        {
            ClipboardBridge.Write(new ClipboardContent(ConsoleText.Text, null), new WindowInteropHelper(this).Handle);
        }
        catch (InvalidOperationException exception)
        {
            // Le presse-papiers est tenu par une autre application : on le
            // dit, plutôt que de faire croire que la copie a eu lieu.
            MessageBox.Show(this, $"Copie impossible : {exception.Message}", "Console", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Efface la console et le journal.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        _journal.Clear();
        ConsoleText.Clear();
    }

    /// <summary>
    /// Ferme la console.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
