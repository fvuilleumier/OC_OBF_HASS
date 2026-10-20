using System.Windows;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Fenêtre des tâches d'arrière-plan : ce qui tourne, ce qui attend, ce qui a
/// eu lieu, avec la possibilité d'annuler.
///
/// Elle n'est pas modale : on la garde ouverte à côté de la fenêtre principale
/// pour suivre une analyse longue tout en continuant à travailler.
/// </summary>
public partial class TasksWindow : Window
{
    private readonly TaskCenter _center;
    private readonly Action<bool> _autoAnalyseChanged;

    /// <summary>
    /// Construit la fenêtre.
    /// </summary>
    /// <param name="center">Centre de tâches à présenter</param>
    /// <param name="autoAnalyse">État initial de l'analyse automatique</param>
    /// <param name="autoAnalyseChanged">Action appelée quand l'utilisateur change ce réglage</param>
    internal TasksWindow(TaskCenter center, bool autoAnalyse, Action<bool> autoAnalyseChanged)
    {
        InitializeComponent();
        NativeDarkTitleBar.Apply(this);

        _center = center;
        _autoAnalyseChanged = autoAnalyseChanged;

        TaskList.ItemsSource = center.Tasks;
        AutoAnalyseCheck.IsChecked = autoAnalyse;

        center.Changed += OnCenterChanged;
        Closed += (_, _) => center.Changed -= OnCenterChanged;
        Refresh();
    }

    /// <summary>
    /// Reflète un changement du centre de tâches.
    /// </summary>
    /// <param name="sender">Centre de tâches</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCenterChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    /// <summary>
    /// Met à jour les éléments qui dépendent de l'ensemble des tâches.
    /// </summary>
    private void Refresh()
    {
        EmptyText.Visibility = _center.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelAllButton.IsEnabled = _center.ActiveCount > 0;
    }

    /// <summary>
    /// Annule une tâche.
    /// </summary>
    /// <param name="sender">Bouton de la tâche</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCancelTask(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BackgroundTaskItem task)
        {
            task.Cancel("annulée à votre demande");
        }
    }

    /// <summary>
    /// Annule toutes les tâches actives.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnCancelAll(object sender, RoutedEventArgs e)
    {
        _center.CancelAll("annulée à votre demande");
    }

    /// <summary>
    /// Efface les tâches achevées.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _center.ClearHistory();
    }

    /// <summary>
    /// Aligne la case sur le mode d'analyse changé ailleurs — par
    /// l'interrupteur du bandeau d'état.
    /// </summary>
    /// <param name="enabled">Vrai si l'analyse suit chaque modification</param>
    internal void ShowAutoAnalyse(bool enabled)
    {
        AutoAnalyseCheck.IsChecked = enabled;
    }

    /// <summary>
    /// Transmet le changement du mode d'analyse.
    /// </summary>
    /// <param name="sender">Case à cocher</param>
    /// <param name="e">Données de l'événement</param>
    private void OnAutoAnalyseChanged(object sender, RoutedEventArgs e)
    {
        _autoAnalyseChanged(AutoAnalyseCheck.IsChecked == true);
    }

    /// <summary>
    /// Ferme la fenêtre.
    /// </summary>
    /// <param name="sender">Bouton</param>
    /// <param name="e">Données de l'événement</param>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
