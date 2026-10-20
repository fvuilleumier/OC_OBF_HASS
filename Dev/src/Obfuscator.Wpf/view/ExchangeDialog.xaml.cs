using System.Windows;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Liste à confirmer d'un échange, dans le thème de l'application : ce que
/// l'obfuscation ou le rapatriement va écrire, fichier par fichier, avec ses
/// alertes. C'est le moment où l'on voit, sans équivoque, ce qui va changer de
/// l'autre côté ; rien n'est écrit tant qu'on n'a pas confirmé.
/// </summary>
public partial class ExchangeDialog : Window
{
    private readonly ExchangeDialogContent _content;

    /// <summary>
    /// Construit la boîte de dialogue.
    /// </summary>
    /// <param name="content">Ce qu'elle affiche</param>
    private ExchangeDialog(ExchangeDialogContent content)
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);

        _content = content;
        HeadingText.Text = content.Heading;
        FolderText.Text = content.Folder;
        FolderText.ToolTip = content.Folder;
        SummaryText.Text = content.Summary;
        FooterText.Text = content.Footer;

        BlockingPanel.Visibility = content.Blocking.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BlockingText.Text = string.Join(Environment.NewLine, content.Blocking);
        WarningPanel.Visibility = content.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = string.Join(Environment.NewLine, content.Warnings);

        RowsList.ItemsSource = content.Rows;
        EmptyText.Visibility = content.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = content.EmptyMessage;

        // « Tout cocher » suit les cases qu'on coche une à une : coché si
        // toutes le sont, vide si aucune, à moitié sinon.
        foreach (ExchangeRowModel row in content.Rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ExchangeRowModel.IsChecked) or nameof(ExchangeRowModel.CanToggle))
                {
                    UpdateSelectAll();
                }
            };
        }

        UpdateSelectAll();

        ConfirmButton.Content = content.ConfirmLabel;
        ConfirmButton.IsEnabled = content.CanConfirm;
    }

    /// <summary>
    /// Montre la liste et attend la décision.
    /// </summary>
    /// <param name="owner">Fenêtre propriétaire</param>
    /// <param name="content">Ce que la liste affiche</param>
    /// <returns>Les fichiers cochés et ceux à ignorer désormais si l'utilisateur confirme, <c>null</c> s'il renonce</returns>
    public static ExchangeDialogResult? Ask(Window owner, ExchangeDialogContent content)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(content);

        ExchangeDialog dialog = new(content) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        IReadOnlyList<ExchangeRowModel> rows = dialog._content.Rows;
        return new ExchangeDialogResult(
            new HashSet<int>(rows.Where(row => row.IsChecked && !row.IgnoreLater).Select(row => row.Id)),
            [.. rows.Where(row => row.IgnoreLater).Select(row => row.Change.TargetPath)]);
    }

    /// <summary>
    /// Fait trancher les conflits d'un fichier, puis met sa ligne et les
    /// comptes à jour.
    /// </summary>
    /// <param name="sender">Bouton de la ligne</param>
    /// <param name="e">Données de l'événement</param>
    private void OnResolve(object sender, RoutedEventArgs e)
    {
        if (_content.Resolve is null || (sender as FrameworkElement)?.DataContext is not ExchangeRowModel row)
        {
            return;
        }

        ExchangeChange? updated = _content.Resolve(this, row);
        if (updated is null)
        {
            return;
        }

        row.Update(updated);
        if (_content.Summarize is not null)
        {
            SummaryText.Text = _content.Summarize();
        }
    }

    /// <summary>
    /// Coche ou décoche d'un coup toutes les cases qui peuvent changer.
    /// </summary>
    /// <param name="sender">Case « Tout cocher »</param>
    /// <param name="e">Données de l'événement</param>
    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        bool check = SelectAllCheck.IsChecked == true;
        foreach (ExchangeRowModel row in _content.Rows.Where(row => row.CanToggle))
        {
            row.IsChecked = check;
        }

        UpdateSelectAll();
    }

    /// <summary>
    /// Aligne « Tout cocher » sur les cases des fichiers ; la case disparaît
    /// quand aucune ne peut changer.
    /// </summary>
    private void UpdateSelectAll()
    {
        List<ExchangeRowModel> toggleable = [.. _content.Rows.Where(row => row.CanToggle)];
        SelectAllCheck.Visibility = toggleable.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        int checkedCount = toggleable.Count(row => row.IsChecked);
        SelectAllCheck.IsChecked = checkedCount switch
        {
            0 => false,
            _ when checkedCount == toggleable.Count => true,
            _ => null,
        };
    }

    /// <summary>
    /// Confirme.
    /// </summary>
    /// <param name="sender">Bouton d'écriture</param>
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
