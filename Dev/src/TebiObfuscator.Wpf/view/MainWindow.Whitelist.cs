using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Onglet « Liste blanche » : ce qui reste en clair par décision de
/// l'utilisateur, à deux niveaux.
///
/// La liste blanche du projet garde une valeur en clair partout — toutes ses
/// occurrences, dans tous les fichiers. Une occurrence gardée ici ne vaut que
/// pour elle-même : cette valeur, à cette ligne, à ce rang sur la ligne. Les
/// deux se décident depuis l'aperçu ; c'est ici qu'on les revoit et qu'on
/// revient dessus, puisqu'une valeur gardée partout ne s'y montre plus comme
/// une valeur repérée.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Relit du projet la liste blanche et les occurrences gardées.
    /// </summary>
    private void RebuildWhitelistView()
    {
        if (_project is null)
        {
            WhitelistGrid.ItemsSource = null;
            ExemptionsGrid.ItemsSource = null;
            WhitelistTab.Header = "Liste blanche";
            return;
        }

        List<WhitelistRow> values = [.. _project.Settings.AllowKeep
            .Concat(_project.Settings.IgnoredSuggestions)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(value => new WhitelistRow(value))];

        List<ExemptionRow> exemptions = [];
        foreach (string setting in _project.Settings.LineExceptions)
        {
            if (LineExemption.TryParse(setting, out LineExemption exemption))
            {
                exemptions.Add(new ExemptionRow(exemption));
            }
        }

        exemptions.Sort((left, right) =>
        {
            int byFile = StringComparer.OrdinalIgnoreCase.Compare(left.File, right.File);
            return byFile != 0 ? byFile : left.Exemption.Line.CompareTo(right.Exemption.Line);
        });

        WhitelistGrid.ItemsSource = values;
        ExemptionsGrid.ItemsSource = exemptions;
        WhitelistEmptyHint.Visibility = values.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExemptionsEmptyHint.Visibility = exemptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int total = values.Count + exemptions.Count;
        WhitelistTab.Header = total == 0
            ? "Liste blanche"
            : string.Create(CultureInfo.CurrentCulture, $"Liste blanche ({total})");
    }

    /// <summary>
    /// Ajoute à la main une valeur à la liste blanche.
    /// </summary>
    /// <param name="sender">Bouton « Ajouter »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAddWhitelistValue(object sender, RoutedEventArgs e)
    {
        string value = WhitelistBox.Text.Trim();
        if (value.Length == 0 || _project is null)
        {
            return;
        }

        if (_project.Policy.IsKeptEverywhere(value))
        {
            SetStatus($"« {Shorten(value)} » est déjà dans la liste blanche.");
            return;
        }

        KeepEverywhere(value);
        if (_project.Policy.IsKeptEverywhere(value))
        {
            WhitelistBox.Clear();
        }
    }

    /// <summary>
    /// Valide la saisie d'une valeur de liste blanche à la touche Entrée.
    /// </summary>
    /// <param name="sender">Zone de saisie</param>
    /// <param name="e">Touche pressée</param>
    private void OnWhitelistBoxKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter)
        {
            OnAddWhitelistValue(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Retire une valeur de la liste blanche.
    /// </summary>
    /// <param name="sender">Bouton de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnRemoveWhitelistValue(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WhitelistRow row)
        {
            ReleaseWhitelistValue(row.Value);
        }
    }

    /// <summary>
    /// Annule une occurrence gardée en clair : elle retrouve le sort que lui
    /// font les règles.
    /// </summary>
    /// <param name="sender">Bouton de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnRestoreExemption(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExemptionRow row)
        {
            return;
        }

        LineExemption exemption = row.Exemption;
        if (Decide(
                presenter => presenter.RestoreOccurrence(exemption.File, exemption.Line, exemption.Value, exemption.Occurrence),
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"Ligne {exemption.Line} de {exemption.File} : « {Shorten(exemption.Value)} » n'est plus gardé en clair à cet endroit.")))
        {
            Reconcile();
            RefreshPreviewCounts();
        }
    }
}
