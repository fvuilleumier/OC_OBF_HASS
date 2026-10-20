using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Chiffrement du coffre du projet ouvert : l'interrupteur « bas / élevé » du
/// bandeau d'état.
///
/// L'interrupteur montre l'état réel du fichier, jamais une intention. Basculer
/// rechiffre le coffre avant de rendre la main ; si le rechiffrement échoue,
/// l'interrupteur revient où il était. Et un projet antérieur au choix du
/// chiffrement, dont le coffre est encore en chiffrement élevé, est rechiffré
/// dès son ouverture dans le chiffrement de ses réglages — bas, par défaut —,
/// ce que le bandeau dit une fois.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Aligne l'interrupteur sur le chiffrement du coffre du projet ouvert.
    /// </summary>
    private void ShowVaultProtection()
    {
        if (_project is null)
        {
            VaultProtectionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // C'est le chiffrement du fichier qui est montré : il ne diffère du
        // réglage que si un rechiffrement vient d'échouer, et c'est alors lui
        // qui dit la vérité.
        bool high = _project.StoredProtection == VaultProtection.High;

        VaultProtectionPanel.Visibility = Visibility.Visible;
        VaultProtectionSwitch.IsChecked = high;
        EmphasizeVaultWord(VaultLowText, active: !high);
        EmphasizeVaultWord(VaultHighText, active: high);
    }

    /// <summary>
    /// Met en évidence le mot de l'état actif, et estompe l'autre.
    /// </summary>
    /// <param name="word">Mot d'un état</param>
    /// <param name="active">Vrai si c'est l'état actif</param>
    private void EmphasizeVaultWord(TextBlock word, bool active)
    {
        word.Foreground = Themed<Brush>(active ? "TextPrimary" : "TextMuted");
        word.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>
    /// Change le chiffrement du coffre selon la position de l'interrupteur.
    /// </summary>
    /// <param name="sender">Interrupteur</param>
    /// <param name="e">Données de l'événement</param>
    private void OnVaultProtectionClick(object sender, RoutedEventArgs e)
    {
        if (_project is null || _busy)
        {
            ShowVaultProtection();
            return;
        }

        ObfuscationProject project = _project;
        VaultProtection requested = VaultProtectionSwitch.IsChecked == true ? VaultProtection.High : VaultProtection.Low;

        Run(() =>
        {
            if (!new VaultPresenter(project).ChangeProtection(requested))
            {
                SetStatus($"Le coffre est déjà en {VaultPresenter.Label(requested)}.");
                return;
            }

            SetStatus(
                requested == VaultProtection.High
                    ? "Coffre rechiffré en chiffrement élevé : seul votre compte Windows, sur ce poste, peut désormais l'ouvrir. Pour emporter le projet sur un autre poste, repassez d'abord en chiffrement bas."
                    : "Coffre rechiffré en chiffrement bas : l'application l'ouvre sur tout poste où elle est installée. Sans elle, le fichier reste illisible ; il n'est plus lié à votre compte Windows.",
                success: true);
        });

        // Réussi ou non, l'interrupteur reprend l'état réel du coffre.
        ShowVaultProtection();
    }

    /// <summary>
    /// Rechiffre à l'ouverture un coffre resté dans un autre chiffrement que
    /// celui des réglages du projet.
    /// </summary>
    /// <param name="project">Projet qui vient d'être ouvert</param>
    /// <returns>Ce qu'il faut en dire dans le bandeau, ou <c>null</c> s'il n'y avait rien à faire</returns>
    private static string? AlignVaultProtection(ObfuscationProject project)
    {
        try
        {
            if (new VaultPresenter(project).AlignProtection() is not VaultProtection before)
            {
                return null;
            }

            VaultProtection now = project.Settings.VaultProtection;
            string byDefault = now == VaultProtection.Low ? " (bas par défaut)" : string.Empty;
            return $"Son coffre était encore en {VaultPresenter.Label(before)} : il a été rechiffré en {VaultPresenter.Label(now)}, le réglage du projet{byDefault} — {VaultPresenter.Reach(now)}. L'interrupteur « Chiffrement », en bas à droite, permet d'en changer.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // Le projet reste utilisable : le coffre se relit d'après son
            // en-tête, et sera réécrit au prochain enregistrement.
            return $"Son coffre n'a pas pu être rechiffré en {VaultPresenter.Label(project.Settings.VaultProtection)} ({exception.Message}) : il reste en {VaultPresenter.Label(project.StoredProtection)} pour l'instant.";
        }
    }
}
