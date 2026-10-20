using System.Windows;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Demande de confirmation, dans le thème de l'application.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>
    /// Construit la boîte de dialogue.
    /// </summary>
    private ConfirmDialog()
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);
    }

    /// <summary>
    /// Pose une question fermée à l'utilisateur.
    /// </summary>
    /// <param name="owner">Fenêtre propriétaire</param>
    /// <param name="heading">Question posée</param>
    /// <param name="message">Explication des conséquences</param>
    /// <param name="confirmLabel">Libellé du bouton de confirmation</param>
    /// <param name="cancelLabel">Libellé du bouton d'annulation</param>
    /// <returns><c>true</c> si l'utilisateur a confirmé</returns>
    public static bool Ask(Window owner, string heading, string message, string confirmLabel, string cancelLabel = "Annuler")
    {
        ArgumentNullException.ThrowIfNull(owner);

        ConfirmDialog dialog = new()
        {
            Owner = owner,
        };

        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;
        dialog.CancelButton.Content = cancelLabel;

        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Confirme.
    /// </summary>
    /// <param name="sender">Bouton de confirmation</param>
    /// <param name="e">Données de l'événement</param>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    /// <summary>
    /// Renonce.
    /// </summary>
    /// <param name="sender">Bouton d'annulation</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
