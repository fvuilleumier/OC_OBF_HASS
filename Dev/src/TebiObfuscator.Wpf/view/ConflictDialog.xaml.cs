using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TebiObfuscator.Core.Model.Exchange;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Résolution des conflits d'un fichier rapatrié, passage par passage, à la
/// façon d'un outil de fusion : la version en clair et celle de l'IA côte à
/// côte, et pour chaque passage, ce que l'on garde. Rien n'est écrit ici.
/// </summary>
public partial class ConflictDialog : Window
{
    private readonly List<ConflictRowModel> _rows;

    /// <summary>
    /// Construit la fenêtre.
    /// </summary>
    /// <param name="file">Chemin du fichier en clair</param>
    /// <param name="rows">Conflits du fichier</param>
    private ConflictDialog(string file, List<ConflictRowModel> rows)
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);

        _rows = rows;
        HeadingText.Text = $"Conflits de « {file} »";
        ConflictsList.ItemsSource = rows;

        foreach (ConflictRowModel row in rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        UpdateCount();
    }

    /// <summary>
    /// Montre les conflits d'un fichier et attend les choix.
    /// </summary>
    /// <param name="owner">Fenêtre propriétaire</param>
    /// <param name="file">Chemin du fichier en clair</param>
    /// <param name="blocks">Conflits, dans l'ordre du fichier</param>
    /// <param name="choices">Choix déjà faits, un par conflit</param>
    /// <returns>Un choix par conflit si l'utilisateur applique, <c>null</c> s'il renonce</returns>
    public static IReadOnlyList<ConflictChoice>? Ask(Window owner, string file, IReadOnlyList<ConflictBlock> blocks, IReadOnlyList<ConflictChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(choices);

        List<ConflictRowModel> rows = [.. blocks.Select(block => new ConflictRowModel(
            block,
            blocks.Count,
            block.Index < choices.Count ? choices[block.Index] : ConflictChoice.Unresolved))];

        ConflictDialog dialog = new(file, rows) { Owner = owner };
        return dialog.ShowDialog() == true ? [.. rows.Select(row => row.Choice)] : null;
    }

    /// <summary>
    /// Garde le côté cliqué d'un passage.
    /// </summary>
    /// <param name="sender">Côté du passage</param>
    /// <param name="e">Clic</param>
    private void OnPickSide(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConflictRowModel row, Tag: string tag })
        {
            row.Choice = Enum.Parse<ConflictChoice>(tag);
        }
    }

    /// <summary>
    /// Applique le choix d'un bouton à son passage.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConflictRowModel row, Tag: string tag })
        {
            row.Choice = Enum.Parse<ConflictChoice>(tag);
        }
    }

    /// <summary>
    /// Applique un choix à tous les passages.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnChooseAll(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ConflictChoice choice = Enum.Parse<ConflictChoice>(tag);
            foreach (ConflictRowModel row in _rows)
            {
                row.Choice = choice;
            }
        }
    }

    /// <summary>
    /// Suit les choix pour le compte affiché en tête.
    /// </summary>
    /// <param name="sender">Passage</param>
    /// <param name="e">Propriété changée</param>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConflictRowModel.Choice))
        {
            UpdateCount();
        }
    }

    /// <summary>
    /// Dit combien de passages restent à trancher.
    /// </summary>
    private void UpdateCount()
    {
        int open = _rows.Count(row => row.IsUnresolved);
        CountText.Text = open == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{_rows.Count} passage(s), tous tranchés")
            : string.Create(CultureInfo.CurrentCulture, $"{open} passage(s) sur {_rows.Count} encore non tranché(s)");
    }

    /// <summary>
    /// Applique les choix.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    /// <summary>
    /// Renonce.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
