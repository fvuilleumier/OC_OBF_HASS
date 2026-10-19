using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Threading;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>État d'une tâche d'arrière-plan.</summary>
public enum BackgroundTaskState
{
    /// <summary>Demandée, en attente de son tour ou de la fin de son délai.</summary>
    Planned,

    /// <summary>En cours d'exécution.</summary>
    Running,

    /// <summary>Terminée normalement.</summary>
    Completed,

    /// <summary>Annulée avant son terme.</summary>
    Cancelled,

    /// <summary>Interrompue par une erreur.</summary>
    Failed,
}

/// <summary>
/// Tâche d'arrière-plan telle que l'écran la montre : ce qu'elle fait, où elle
/// en est, et ce qu'on peut encore en faire.
/// </summary>
public sealed class BackgroundTaskItem : NotifyingObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private BackgroundTaskState _state = BackgroundTaskState.Planned;
    private string _detail = "en attente";
    private double _progress;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private string? _outcome;

    /// <summary>
    /// Construit une tâche planifiée.
    /// </summary>
    /// <param name="kind">Famille de la tâche, qui décide des annulations mutuelles</param>
    /// <param name="title">Libellé de la tâche</param>
    internal BackgroundTaskItem(string kind, string title)
    {
        Kind = kind;
        Title = title;
        CreatedAt = DateTime.Now;
    }

    /// <summary>Famille de la tâche.</summary>
    public string Kind { get; }

    /// <summary>Libellé de la tâche.</summary>
    public string Title { get; }

    /// <summary>Heure de la demande.</summary>
    public DateTime CreatedAt { get; }

    /// <summary>État de la tâche.</summary>
    public BackgroundTaskState State
    {
        get => _state;
        private set
        {
            _state = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(StateLabel));
            NotifyPropertyChanged(nameof(CanCancel));
            NotifyPropertyChanged(nameof(IsRunning));
            NotifyPropertyChanged(nameof(IsActive));
            NotifyPropertyChanged(nameof(Timing));
        }
    }

    /// <summary>Libellé de l'état.</summary>
    public string StateLabel => State switch
    {
        BackgroundTaskState.Planned => "Prévue",
        BackgroundTaskState.Running => "En cours",
        BackgroundTaskState.Completed => "Terminée",
        BackgroundTaskState.Cancelled => "Annulée",
        _ => "En échec",
    };

    /// <summary>Étape en cours, ou issue de la tâche une fois terminée.</summary>
    public string Detail
    {
        get => _outcome ?? _detail;
        private set
        {
            _detail = value;
            NotifyPropertyChanged();
        }
    }

    /// <summary>Part du travail accomplie, entre 0 et 1.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(ProgressLabel));
        }
    }

    /// <summary>Avancement en pourcentage.</summary>
    public string ProgressLabel => string.Create(CultureInfo.CurrentCulture, $"{Progress:P0}");

    /// <summary>Vrai si la tâche peut encore être annulée.</summary>
    public bool CanCancel => State is BackgroundTaskState.Planned or BackgroundTaskState.Running;

    /// <summary>Vrai si la tâche s'exécute.</summary>
    public bool IsRunning => State == BackgroundTaskState.Running;

    /// <summary>Vrai si la tâche n'est pas encore achevée.</summary>
    public bool IsActive => CanCancel;

    /// <summary>Chronologie de la tâche, en clair.</summary>
    public string Timing => State switch
    {
        BackgroundTaskState.Planned => string.Create(CultureInfo.CurrentCulture, $"demandée à {CreatedAt:HH:mm:ss}"),
        BackgroundTaskState.Running => string.Create(CultureInfo.CurrentCulture, $"démarrée à {_startedAt:HH:mm:ss}"),
        _ when _startedAt is null => string.Create(CultureInfo.CurrentCulture, $"à {_finishedAt:HH:mm:ss}, sans avoir démarré"),
        _ => string.Create(CultureInfo.CurrentCulture, $"à {_finishedAt:HH:mm:ss}, en {FormatDuration(Duration)}"),
    };

    /// <summary>Durée d'exécution, nulle tant que la tâche n'a pas démarré.</summary>
    public TimeSpan Duration => _startedAt is null
        ? TimeSpan.Zero
        : (_finishedAt ?? DateTime.Now) - _startedAt.Value;

    /// <summary>Jeton d'annulation de la tâche.</summary>
    internal CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Demande l'annulation de la tâche, avec la raison à afficher.
    /// </summary>
    /// <param name="reason">Raison de l'annulation</param>
    public void Cancel(string reason)
    {
        if (!CanCancel || _cancellation.IsCancellationRequested)
        {
            return;
        }

        _outcome = reason;
        NotifyPropertyChanged(nameof(Detail));
        _cancellation.Cancel();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation.Dispose();
    }

    /// <summary>Marque la tâche comme démarrée.</summary>
    internal void MarkRunning()
    {
        _startedAt = DateTime.Now;
        Detail = "démarrage";
        State = BackgroundTaskState.Running;
    }

    /// <summary>
    /// Reflète l'avancement transmis par le travail en cours.
    /// </summary>
    /// <param name="report">Avancement</param>
    internal void Report(BatchProgress report)
    {
        if (State != BackgroundTaskState.Running)
        {
            return;
        }

        Progress = report.Fraction;
        Detail = report.FileName.Length == 0
            ? report.Step
            : string.Create(
                CultureInfo.CurrentCulture,
                $"fichier {report.FileIndex + 1}/{report.FileCount} · {report.FileName} · {report.Step}");
    }

    /// <summary>
    /// Clôt la tâche.
    /// </summary>
    /// <param name="state">État final</param>
    /// <param name="outcome">Issue à afficher</param>
    internal void Finish(BackgroundTaskState state, string outcome)
    {
        _finishedAt = DateTime.Now;
        _outcome ??= outcome;
        if (state == BackgroundTaskState.Completed)
        {
            Progress = 1;
        }

        NotifyPropertyChanged(nameof(Detail));
        State = state;
    }

    /// <summary>
    /// Formate une durée courte.
    /// </summary>
    /// <param name="duration">Durée à formater</param>
    /// <returns>La durée, en millisecondes ou en secondes</returns>
    internal static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalMilliseconds:0} ms")
            : string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:0.0} s");
    }
}

/// <summary>
/// Exécute les analyses en arrière-plan, une à la fois, et tient le journal
/// des tâches pour l'écran.
///
/// Trois règles gouvernent ce qui s'y passe.
///
/// Une seule analyse tourne à la fois : deux analyses du même lot se
/// disputeraient le processeur pour produire deux résultats dont un seul sera
/// affiché.
///
/// Relancer une tâche annule celle de la même famille, qu'elle soit prévue ou
/// en cours : son résultat reposerait sur un état déjà dépassé. La nouvelle
/// attend que l'ancienne ait rendu la main, ce qui est rapide — l'annulation
/// est examinée entre deux détecteurs.
///
/// Le travail s'exécute sur un fil de priorité réduite : l'écran reste fluide
/// même pendant l'analyse d'un gros journal, au prix d'une analyse à peine plus
/// longue.
///
/// L'obfuscation n'y passe pas : elle écrit des fichiers et enrichit le coffre,
/// elle ne se lance donc qu'à la demande, et l'écran attend sa fin.
/// </summary>
internal sealed class TaskCenter
{
    /// <summary>Nombre de tâches achevées conservées dans le journal.</summary>
    private const int HistoryLimit = 30;

    private readonly Dispatcher _dispatcher;
    private Task _lastRun = Task.CompletedTask;

    /// <summary>
    /// Construit le centre de tâches, rattaché au fil de l'écran.
    /// </summary>
    /// <param name="dispatcher">Répartiteur du fil de l'écran</param>
    public TaskCenter(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Levé à chaque changement d'état d'une tâche, sur le fil de l'écran.</summary>
    public event EventHandler? Changed;

    /// <summary>Tâches, de la plus récente à la plus ancienne.</summary>
    public ObservableCollection<BackgroundTaskItem> Tasks { get; } = [];

    /// <summary>Tâche en cours d'exécution, s'il y en a une.</summary>
    public BackgroundTaskItem? Running => Tasks.FirstOrDefault(task => task.IsRunning);

    /// <summary>Tâche la plus récente encore active, prévue ou en cours.</summary>
    public BackgroundTaskItem? Active => Tasks.FirstOrDefault(task => task.IsActive);

    /// <summary>Nombre de tâches encore actives.</summary>
    public int ActiveCount => Tasks.Count(task => task.IsActive);

    /// <summary>
    /// Planifie une tâche, en annulant celles de la même famille.
    /// </summary>
    /// <typeparam name="TResult">Type du résultat produit</typeparam>
    /// <param name="kind">Famille de la tâche</param>
    /// <param name="title">Libellé affiché</param>
    /// <param name="delay">Délai avant démarrage, pour regrouper des demandes rapprochées</param>
    /// <param name="prepare">
    /// Appelé sur le fil de l'écran au moment du démarrage : capture l'état à
    /// traiter et retourne le travail à exécuter en arrière-plan, ou
    /// <c>null</c> s'il n'y a plus rien à faire.
    /// </param>
    /// <param name="completed">Appelé sur le fil de l'écran avec le résultat, si la tâche n'a pas été annulée</param>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "La tâche est confiée au journal, qui la libère lorsqu'elle en sort.")]
    public void Schedule<TResult>(
        string kind,
        string title,
        TimeSpan delay,
        Func<Func<IProgress<BatchProgress>, CancellationToken, TResult>?> prepare,
        Action<TResult> completed)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(completed);

        Cancel(kind, "remplacée par une demande plus récente");

        // Le centre garde la propriété de la tâche : c'est lui qui la libère
        // quand elle sort du journal.
        BackgroundTaskItem task = new(kind, title);
        Tasks.Insert(0, task);
        TrimHistory();
        Notify();

        // Le déroulement démarre dans une opération du répartiteur de l'écran :
        // ses attentes reprennent ainsi toujours sur ce fil, quel que soit
        // l'appelant, et la préparation comme la remise du résultat — qui
        // touchent au projet et aux contrôles — ne s'exécutent jamais ailleurs.
        _ = _dispatcher.InvokeAsync(() => RunAsync(task, delay, prepare, completed));
    }

    /// <summary>
    /// Annule les tâches actives d'une famille.
    /// </summary>
    /// <param name="kind">Famille concernée</param>
    /// <param name="reason">Raison affichée</param>
    public void Cancel(string kind, string reason)
    {
        foreach (BackgroundTaskItem task in Tasks.Where(task => task.Kind == kind && task.CanCancel).ToList())
        {
            task.Cancel(reason);
        }
    }

    /// <summary>
    /// Annule toutes les tâches actives.
    /// </summary>
    /// <param name="reason">Raison affichée</param>
    public void CancelAll(string reason)
    {
        foreach (BackgroundTaskItem task in Tasks.Where(task => task.CanCancel).ToList())
        {
            task.Cancel(reason);
        }
    }

    /// <summary>
    /// Efface du journal les tâches achevées.
    /// </summary>
    public void ClearHistory()
    {
        foreach (BackgroundTaskItem task in Tasks.Where(task => !task.IsActive).ToList())
        {
            Tasks.Remove(task);
            task.Dispose();
        }

        Notify();
    }

    /// <summary>
    /// Déroule une tâche : délai, attente de la précédente, travail en
    /// arrière-plan, puis remise du résultat.
    /// </summary>
    /// <typeparam name="TResult">Type du résultat</typeparam>
    /// <param name="task">Tâche à dérouler</param>
    /// <param name="delay">Délai avant démarrage</param>
    /// <param name="prepare">Préparation sur le fil de l'écran</param>
    /// <param name="completed">Remise du résultat sur le fil de l'écran</param>
    /// <returns>La fin du déroulement</returns>
    private async Task RunAsync<TResult>(
        BackgroundTaskItem task,
        TimeSpan delay,
        Func<Func<IProgress<BatchProgress>, CancellationToken, TResult>?> prepare,
        Action<TResult> completed)
    {
        CancellationToken token = task.Token;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous = _lastRun;
        _lastRun = finished.Task;

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(true);
            }

            // L'analyse précédente a été annulée par cette demande : on attend
            // qu'elle ait rendu la main plutôt que de la faire tourner en
            // parallèle.
            await previous.ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            Func<IProgress<BatchProgress>, CancellationToken, TResult>? work = prepare();
            if (work is null)
            {
                task.Finish(BackgroundTaskState.Cancelled, "plus rien à analyser");
                return;
            }

            task.MarkRunning();
            Notify();

            Progress<BatchProgress> progress = new(report =>
            {
                task.Report(report);
                Notify();
            });

            TResult result = await RunOnWorkerAsync(() => work(progress, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            completed(result);
            task.Finish(BackgroundTaskState.Completed, string.Create(
                CultureInfo.CurrentCulture,
                $"terminée en {BackgroundTaskItem.FormatDuration(task.Duration)}"));
        }
        catch (OperationCanceledException)
        {
            task.Finish(BackgroundTaskState.Cancelled, "annulée");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Une tâche d'arrière-plan qui échoue ne doit ni fermer la
            // fenêtre, ni rester affichée « en cours » : l'erreur est rendue
            // lisible et la session continue.
            task.Finish(BackgroundTaskState.Failed, exception.Message);
        }
        finally
        {
            // La fin n'est annoncée qu'après celle de la tâche précédente : la
            // suivante, qui attend celle-ci, attend ainsi toute la chaîne — y
            // compris une analyse annulée qui n'aurait pas encore rendu la main.
            _ = previous.ContinueWith(
                _ => finished.SetResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Notify();
        }
    }

    /// <summary>
    /// Exécute un travail sur un fil dédié, de priorité réduite.
    ///
    /// Un fil dédié plutôt qu'un fil du réservoir partagé : abaisser la
    /// priorité d'un fil du réservoir pénaliserait tout ce qu'il exécuterait
    /// ensuite, alors qu'un fil dédié disparaît avec son travail.
    /// </summary>
    /// <typeparam name="TResult">Type du résultat</typeparam>
    /// <param name="work">Travail à exécuter</param>
    /// <param name="token">Jeton d'annulation</param>
    /// <returns>Le résultat du travail</returns>
    private static Task<TResult> RunOnWorkerAsync<TResult>(Func<TResult> work, CancellationToken token)
    {
        return Task.Factory.StartNew(
            () =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                return work();
            },
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Distingue les erreurs dont l'écran peut rendre compte de celles qui
    /// doivent arrêter le processus.
    ///
    /// Une analyse ne modifie que son instantané : quelle que soit l'erreur,
    /// le projet réel reste intact, et poursuivre la session est sans risque.
    /// Seules les défaillances du processus lui-même sont laissées remonter.
    /// </summary>
    /// <param name="exception">Erreur survenue</param>
    /// <returns><c>true</c> si l'erreur peut être affichée et la session poursuivie</returns>
    private static bool IsRecoverable(Exception exception)
    {
        return exception is not (OutOfMemoryException
            or AccessViolationException
            or InvalidProgramException
            or StackOverflowException);
    }

    /// <summary>
    /// Limite le journal aux tâches récentes.
    /// </summary>
    private void TrimHistory()
    {
        List<BackgroundTaskItem> finished = [.. Tasks.Where(task => !task.IsActive)];
        foreach (BackgroundTaskItem task in finished.Skip(HistoryLimit))
        {
            Tasks.Remove(task);
            task.Dispose();
        }
    }

    /// <summary>
    /// Prévient l'écran d'un changement, sur son fil.
    /// </summary>
    private void Notify()
    {
        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
