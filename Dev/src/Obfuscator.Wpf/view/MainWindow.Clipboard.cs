using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Wpf.View;

/// <summary>
/// Projet proposé pour le presse-papiers sur l'accueil.
/// </summary>
/// <param name="Path">Dossier du projet</param>
/// <param name="Label">Libellé affiché</param>
/// <param name="IsDefault">Vrai pour le projet « Presse-papiers » par défaut</param>
public sealed record ClipboardProjectChoice(string Path, string Label, bool IsDefault)
{
    /// <summary>
    /// Rend le libellé : c'est ce texte que la liste déroulante affiche une
    /// fois le choix fait.
    /// </summary>
    /// <returns>Le libellé</returns>
    public override string ToString() => Label;
}

/// <summary>
/// Issue d'une action sur le presse-papiers.
/// </summary>
/// <param name="Message">Compte rendu à afficher</param>
/// <param name="Success">Vrai pour un succès, faux pour un échec, null pour une information</param>
/// <param name="Replacements">Nombre de valeurs pseudonymisées, pour relancer l'aperçu</param>
internal sealed record ClipboardOutcome(string Message, bool? Success, int Replacements = 0)
{
    /// <summary>Vrai si le compte rendu signale des valeurs restées en clair, à regarder.</summary>
    public bool Caution { get; init; }

    /// <summary>Trace du contenu déposé dans le presse-papiers, s'il y en a un.</summary>
    public ClipboardStamp? Stamp { get; init; }
}

/// <summary>
/// Pseudonymisation et restauration du presse-papiers, avec ou sans projet
/// ouvert.
///
/// Projet ouvert, c'est son coffre qui sert. Sur l'accueil, l'utilisateur
/// choisit le coffre — l'un de ses projets récents, ou un projet
/// « Presse-papiers » créé au premier usage pour qui n'en a aucun. Un coffre
/// est indispensable dans tous les cas : c'est lui qui garde les
/// correspondances, et sans lui la réponse de l'IA ne pourrait pas être
/// restaurée.
///
/// À la restauration depuis l'accueil, le projet d'où viennent les pseudonymes
/// est reconnu de lui-même : chaque projet signe ses jetons de son nonce, et
/// celui qui restitue le plus de valeurs est retenu. Restaurer avec le mauvais
/// coffre est l'erreur la plus facile à commettre, et la plus déroutante — le
/// texte ressort intact, sans qu'on comprenne pourquoi.
///
/// Le compte rendu dit ce qui a été masqué, famille par famille, et ce qui
/// reste en clair. Sans vocabulaire, seules les valeurs reconnaissables
/// d'elles-mêmes — adresses, courriels, secrets — sont masquées : un texte fait
/// surtout de noms ressortirait presque intact, et l'utilisateur doit le savoir
/// avant de le coller dans l'IA, pas après.
/// </summary>
public partial class MainWindow
{
    /// <summary>Nombre de termes restés en clair cités dans le compte rendu.</summary>
    private const int CandidatesShown = 5;

    /// <summary>
    /// Délai du contrôle différé d'un dépôt : le temps qu'un outil qui
    /// surveille le presse-papiers ait réagi à la modification.
    /// </summary>
    private static readonly TimeSpan ClipboardWatchDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Dossier du projet « Presse-papiers » par défaut.</summary>
    private static string DefaultClipboardProjectPath => Path.Combine(UserPreferences.ProfileFolder, "Presse-papiers");

    /// <summary>Vrai pendant le remplissage de la liste des coffres, qui ne doit pas passer pour un choix.</summary>
    private bool _fillingClipboardProjects;

    /// <summary>Contrôle différé du dernier dépôt.</summary>
    private DispatcherTimer? _clipboardWatch;

    /// <summary>Fenêtre propriétaire des dépôts dans le presse-papiers.</summary>
    private nint ClipboardOwner => new WindowInteropHelper(this).Handle;

    // ═════════════════════════ Actions des boutons ══════════════════════════

    /// <summary>
    /// Pseudonymise le presse-papiers : avec le projet ouvert, ou avec le
    /// coffre choisi sur l'accueil.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnObfuscateClipboard(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_project is null)
        {
            Run(() => ReportClipboard(ObfuscateClipboard(OpenClipboardProject(createIfMissing: true), ClipboardOwner)));
            return;
        }

        ObfuscationProject project = _project;
        Run(() =>
        {
            ClipboardOutcome outcome = ObfuscateClipboard(project, ClipboardOwner);
            ReportClipboard(outcome);

            // Le coffre a reçu de nouvelles attributions : l'aperçu les reprend
            // pour que ses numéros restent ceux qu'écrira l'obfuscation.
            if (_preferences.AnalyseAfterEachChange && _analysis is not null && outcome.Replacements > 0)
            {
                ScheduleAnalysis(ChangeAnalysisDelay, null, OpenFilesOnly);
            }
        });
    }

    /// <summary>
    /// Restaure le presse-papiers : avec le projet ouvert, ou, sur l'accueil,
    /// avec le projet que ses pseudonymes désignent.
    /// </summary>
    /// <param name="sender">Bouton à l'origine de l'action</param>
    /// <param name="e">Données de l'événement</param>
    private void OnDeobfuscateClipboard(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_project is not null)
        {
            ObfuscationProject project = _project;
            Run(() => ReportClipboard(DeobfuscateClipboard([project], project, ClipboardOwner)));
            return;
        }

        Run(() =>
        {
            List<ObfuscationProject> candidates = OpenClipboardCandidates();
            if (candidates.Count == 0)
            {
                ReportClipboard(new ClipboardOutcome(
                    "Aucun coffre à consulter : pseudonymisez d'abord un texte, ou ouvrez le projet dont viennent les pseudonymes.",
                    null));
                return;
            }

            ObfuscationProject preferred = FindCandidate(candidates, SelectedClipboardProject()?.Path) ?? candidates[0];
            ReportClipboard(DeobfuscateClipboard(candidates, preferred, ClipboardOwner));
        });
    }

    /// <summary>
    /// Retient le coffre choisi pour le presse-papiers.
    /// </summary>
    /// <param name="sender">Liste déroulante</param>
    /// <param name="e">Changement de sélection</param>
    private void OnClipboardProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingClipboardProjects || ClipboardProjectBox.SelectedItem is not ClipboardProjectChoice choice)
        {
            return;
        }

        _preferences.ClipboardProject = choice.IsDefault ? string.Empty : choice.Path;
        _preferences.Save();
        ShowClipboardProjectHint();
    }

    /// <summary>
    /// Ouvre le projet du coffre choisi, pour en modifier les règles.
    /// </summary>
    /// <param name="sender">Lien</param>
    /// <param name="e">Données de l'événement</param>
    private void OnOpenClipboardProject(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedClipboardProject() is not ClipboardProjectChoice choice)
        {
            return;
        }

        if (choice.IsDefault && !IsProjectFolder(choice.Path))
        {
            Run(() => _ = OpenClipboardProject(createIfMissing: true));
        }

        OpenProjectAt(choice.Path, create: false);
    }

    // ═══════════════════════════ Coffres proposés ═══════════════════════════

    /// <summary>
    /// Remplit la liste des coffres de l'accueil : le projet par défaut, puis
    /// les projets récents encore présents, et resélectionne le choix retenu.
    /// </summary>
    private void RefreshClipboardProjects()
    {
        List<ClipboardProjectChoice> choices =
        [
            new ClipboardProjectChoice(DefaultClipboardProjectPath, "Presse-papiers (projet par défaut)", IsDefault: true),
        ];

        choices.AddRange(_preferences.RecentItems()
            .Where(recent => IsProjectFolder(recent.Path) && !SameFolder(recent.Path, DefaultClipboardProjectPath))
            .Select(recent => new ClipboardProjectChoice(recent.Path, recent.Name, IsDefault: false)));

        // Tant que l'utilisateur n'a rien choisi, le projet le plus récent est
        // proposé : son vocabulaire masque les noms qu'aucune règle ne devine.
        string? preference = _preferences.ClipboardProject;
        ClipboardProjectChoice selected = preference switch
        {
            null => choices.Count > 1 ? choices[1] : choices[0],
            "" => choices[0],
            _ => choices.FirstOrDefault(choice => SameFolder(choice.Path, preference)) ?? choices[0],
        };

        _fillingClipboardProjects = true;
        try
        {
            ClipboardProjectBox.ItemsSource = choices;
            ClipboardProjectBox.SelectedItem = selected;
        }
        finally
        {
            _fillingClipboardProjects = false;
        }

        ShowClipboardProjectHint();
    }

    /// <summary>
    /// Donne le coffre choisi sur l'accueil.
    /// </summary>
    /// <returns>Le choix, ou <c>null</c> si la liste est vide</returns>
    private ClipboardProjectChoice? SelectedClipboardProject()
    {
        return ClipboardProjectBox.SelectedItem as ClipboardProjectChoice;
    }

    /// <summary>
    /// Décrit, sous la liste, ce que le coffre choisi saura masquer.
    /// </summary>
    private void ShowClipboardProjectHint()
    {
        ClipboardProjectHint.Text = SelectedClipboardProject() is ClipboardProjectChoice choice
            ? DescribeVault(choice)
            : string.Empty;
    }

    /// <summary>
    /// Dit ce qu'un coffre saura masquer : c'est son vocabulaire qui fait la
    /// différence sur les noms, qu'aucune règle ne devine.
    /// </summary>
    /// <param name="choice">Coffre proposé</param>
    /// <returns>La description</returns>
    private static string DescribeVault(ClipboardProjectChoice choice)
    {
        const string Recognizable = "seules les valeurs reconnaissables d'elles-mêmes — adresses, courriels, secrets, IBAN… — sont masquées, pas les noms";

        if (!IsProjectFolder(choice.Path))
        {
            return $"Créé au premier usage. Sans vocabulaire, {Recognizable}.";
        }

        try
        {
            ObfuscationProject project = ObfuscationProject.Open(choice.Path, VaultProtectors.CreateDefault());
            int terms = project.Vocabulary.Terms.Count;
            int values = project.Store.Entries.Count;

            return terms == 0
                ? string.Create(CultureInfo.CurrentCulture, $"Aucun terme de vocabulaire : {Recognizable}. {values} correspondance(s) au coffre.")
                : string.Create(CultureInfo.CurrentCulture, $"{terms} terme(s) de vocabulaire — noms, lieux, clients… — masqué(s) en plus des valeurs reconnaissables d'elles-mêmes. {values} correspondance(s) au coffre.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return $"Ouverture impossible : {exception.Message}";
        }
    }

    /// <summary>
    /// Ouvre le projet du coffre choisi, en créant au besoin le projet par
    /// défaut.
    /// </summary>
    /// <param name="createIfMissing">Vrai pour créer le projet par défaut s'il n'existe pas encore</param>
    /// <returns>Le projet ouvert</returns>
    /// <exception cref="InvalidOperationException">Si le projet choisi n'existe plus</exception>
    private ObfuscationProject OpenClipboardProject(bool createIfMissing)
    {
        ClipboardProjectChoice choice = SelectedClipboardProject()
            ?? new ClipboardProjectChoice(DefaultClipboardProjectPath, "Presse-papiers", IsDefault: true);

        if (IsProjectFolder(choice.Path))
        {
            return ObfuscationProject.Open(choice.Path, VaultProtectors.CreateDefault());
        }

        if (!choice.IsDefault || !createIfMissing)
        {
            throw new InvalidOperationException($"Le projet « {choice.Label} » n'existe plus dans « {choice.Path} ».");
        }

        ObfuscationProject created = ObfuscationProject.Create(choice.Path, VaultProtectors.CreateDefault());
        ReviewPresenter presenter = new(created);
        presenter.Rename("Presse-papiers");
        presenter.Commit();
        return created;
    }

    /// <summary>
    /// Ouvre tous les coffres proposés qui existent, pour reconnaître celui
    /// d'où viennent des pseudonymes. Un coffre illisible — en chiffrement
    /// élevé pour un autre compte Windows, par exemple — est simplement ignoré.
    /// </summary>
    /// <returns>Les projets ouverts</returns>
    private List<ObfuscationProject> OpenClipboardCandidates()
    {
        List<ObfuscationProject> projects = [];
        IEnumerable<ClipboardProjectChoice> choices = ClipboardProjectBox.ItemsSource as IEnumerable<ClipboardProjectChoice> ?? [];

        foreach (ClipboardProjectChoice choice in choices.Where(choice => IsProjectFolder(choice.Path)))
        {
            try
            {
                projects.Add(ObfuscationProject.Open(choice.Path, VaultProtectors.CreateDefault()));
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                // Coffre illisible : il ne peut de toute façon rien restaurer.
            }
        }

        return projects;
    }

    /// <summary>
    /// Retrouve, parmi des projets ouverts, celui d'un dossier donné.
    /// </summary>
    /// <param name="projects">Projets ouverts</param>
    /// <param name="path">Dossier recherché</param>
    /// <returns>Le projet, ou <c>null</c></returns>
    private static ObfuscationProject? FindCandidate(List<ObfuscationProject> projects, string? path)
    {
        return path is null ? null : projects.Find(project => SameFolder(project.RootPath, path));
    }

    // ══════════════════════════════ Traitement ══════════════════════════════

    /// <summary>
    /// Pseudonymise le texte du presse-papiers avec un projet.
    ///
    /// Les correspondances sont enregistrées au coffre avant que le texte ne
    /// soit rendu : c'est ce qui permettra de restaurer la réponse. Si des
    /// valeurs masquées par les règles subsistaient, le presse-papiers n'est
    /// pas modifié — mieux vaut ne rien coller que coller une fuite.
    /// </summary>
    /// <param name="project">Projet dont le coffre et les règles servent</param>
    /// <param name="owner">Fenêtre propriétaire du dépôt</param>
    /// <returns>L'issue de l'action</returns>
    private static ClipboardOutcome ObfuscateClipboard(ObfuscationProject project, nint owner)
    {
        (ClipboardOutcome outcome, ClipboardContent? result) = Pseudonymize(project, ClipboardBridge.Read());
        return result is null ? outcome : outcome with { Stamp = ClipboardBridge.Write(result, owner) };
    }

    /// <summary>
    /// Pseudonymise un contenu de presse-papiers, sans toucher au
    /// presse-papiers lui-même.
    /// </summary>
    /// <param name="project">Projet dont le coffre et les règles servent</param>
    /// <param name="content">Contenu lu, ou <c>null</c> s'il n'y a pas de texte</param>
    /// <returns>L'issue, et le contenu à déposer — <c>null</c> s'il ne faut rien déposer</returns>
    private static (ClipboardOutcome Outcome, ClipboardContent? Result) Pseudonymize(ObfuscationProject project, ClipboardContent? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Text))
        {
            return (new ClipboardOutcome("Le presse-papiers ne contient pas de texte : copiez d'abord le texte à pseudonymiser.", null), null);
        }

        TextObfuscation result = new ObfuscatePresenter(project).RunOnText(content.Text);
        if (!result.IsClean)
        {
            return (new ClipboardOutcome(
                "Pseudonymisation refusée : des valeurs masquées par vos règles subsisteraient dans le texte. Le presse-papiers n'a pas été modifié ; passez par l'aperçu d'un projet pour voir ce qui manque.",
                false), null);
        }

        string? unmasked = DescribeUnmasked(project, result.Candidates);
        if (result.Replacements == 0)
        {
            string idle = $"Rien à pseudonymiser dans ce texte avec les règles de « {project.DisplayName} » : le presse-papiers est inchangé.";
            return (new ClipboardOutcome(unmasked is null ? idle : $"{idle} {unmasked}", null) { Caution = unmasked is not null }, null);
        }

        string families = string.Join(", ", result.Applied
            .GroupBy(span => span.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => CategoryLabels.Label(group.Key), StringComparer.CurrentCulture)
            .Select(group => string.Create(CultureInfo.CurrentCulture, $"{CategoryLabels.Label(group.Key)} ×{group.Count()}")));

        StringBuilder message = new(string.Create(
            CultureInfo.CurrentCulture,
            $"{result.Replacements} valeur(s) pseudonymisée(s) avec le coffre « {project.DisplayName} » ({families}). Le presse-papiers contient le texte à envoyer, relu et vérifié."));

        int known = result.KnownInClear.Sum(value => value.Occurrences);
        if (known > 0)
        {
            message.Append(CultureInfo.CurrentCulture, $" Attention : {known} occurrence(s) de valeurs masquées autrefois restent en clair, vos règles ne les couvrant plus.");
        }

        if (unmasked is not null)
        {
            message.Append(' ').Append(unmasked);
        }

        bool caution = known > 0 || unmasked is not null;
        ClipboardOutcome outcome = new(message.ToString(), caution ? null : true, result.Replacements) { Caution = caution };
        return (outcome, new ClipboardContent(result.Text, null));
    }

    /// <summary>
    /// Signale ce qui reste en clair faute de règle : tous les noms quand le
    /// coffre n'a pas de vocabulaire, et les termes qui ressemblent à des noms
    /// propres ou à des identifiants internes.
    /// </summary>
    /// <param name="project">Projet dont les règles ont servi</param>
    /// <param name="candidates">Termes restés en clair</param>
    /// <returns>Le signalement, ou <c>null</c> s'il n'y a rien à signaler</returns>
    private static string? DescribeUnmasked(ObfuscationProject project, IReadOnlyList<VocabularyCandidate> candidates)
    {
        List<string> parts = [];
        if (project.Vocabulary.Terms.Count == 0)
        {
            parts.Add($"Le coffre « {project.DisplayName} » n'a aucun terme de vocabulaire : les noms — personnes, lieux, clients — restent en clair. Pour les masquer, choisissez un projet qui les connaît, ou ouvrez celui-ci pour les ajouter.");
        }

        if (candidates.Count > 0)
        {
            string shown = string.Join(", ", candidates.Take(CandidatesShown).Select(candidate => candidate.Text));
            string more = candidates.Count > CandidatesShown
                ? string.Create(CultureInfo.CurrentCulture, $" (+{candidates.Count - CandidatesShown})")
                : string.Empty;
            parts.Add($"Restés en clair, à vérifier : {shown}{more}.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Restaure le texte du presse-papiers — et sa version mise en forme, s'il
    /// y en a une — avec celui des projets qui en reconnaît le plus de
    /// pseudonymes, le projet préféré l'emportant à égalité.
    /// </summary>
    /// <param name="candidates">Projets susceptibles d'avoir produit les pseudonymes</param>
    /// <param name="preferred">Projet choisi par l'utilisateur</param>
    /// <param name="owner">Fenêtre propriétaire du dépôt</param>
    /// <returns>L'issue de l'action</returns>
    private static ClipboardOutcome DeobfuscateClipboard(List<ObfuscationProject> candidates, ObfuscationProject preferred, nint owner)
    {
        (ClipboardOutcome outcome, ClipboardContent? result) = Restore(candidates, preferred, ClipboardBridge.Read());
        return result is null ? outcome : outcome with { Stamp = ClipboardBridge.Write(result, owner) };
    }

    /// <summary>
    /// Restaure un contenu de presse-papiers, sans toucher au presse-papiers
    /// lui-même.
    /// </summary>
    /// <param name="candidates">Projets susceptibles d'avoir produit les pseudonymes</param>
    /// <param name="preferred">Projet choisi par l'utilisateur</param>
    /// <param name="content">Contenu lu, ou <c>null</c> s'il n'y a pas de texte</param>
    /// <returns>L'issue, et le contenu à déposer — <c>null</c> s'il ne faut rien déposer</returns>
    private static (ClipboardOutcome Outcome, ClipboardContent? Result) Restore(
        List<ObfuscationProject> candidates,
        ObfuscationProject preferred,
        ClipboardContent? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Text))
        {
            return (new ClipboardOutcome("Le presse-papiers ne contient pas de texte : copiez d'abord la réponse de l'IA.", null), null);
        }

        // Le coffre choisi est essayé en premier : il l'emporte à égalité.
        List<(ObfuscationProject Project, DeobfuscationReport Report)> attempts =
        [
            .. candidates
                .OrderBy(candidate => ReferenceEquals(candidate, preferred) ? 0 : 1)
                .Select(candidate => (candidate, new DeobfuscatePresenter(candidate).RunOnText(content.Text))),
        ];

        (ObfuscationProject project, DeobfuscationReport report) = attempts[0];
        foreach ((ObfuscationProject candidate, DeobfuscationReport attempt) in attempts.Skip(1))
        {
            if (Evidence(attempt).CompareTo(Evidence(report)) > 0)
            {
                (project, report) = (candidate, attempt);
            }
        }

        // Sans jeton signé, rien ne dit que ce coffre-là est le bon si un autre
        // reconnaît aussi les adresses ou les courriels : il faut le dire.
        bool ambiguous = Evidence(report).Signed == 0
            && attempts.Count(attempt => attempt.Report.Substitutions.Count > 0) > 1;

        if (report.Substitutions.Count == 0)
        {
            ClipboardOutcome nothing = candidates.Count == 1
                ? new ClipboardOutcome(
                    $"Aucun pseudonyme de ce projet dans le presse-papiers : le texte vient-il bien de « {preferred.DisplayName} » (jetons {preferred.Format.Prefix}{preferred.Format.Nonce}) ? Le presse-papiers n'a pas été modifié.",
                    false)
                : new ClipboardOutcome(
                    $"Aucun pseudonyme connu dans le presse-papiers : aucun des coffres proposés ({string.Join(", ", candidates.Select(candidate => $"« {candidate.DisplayName} »"))}) ne le reconnaît. Le presse-papiers n'a pas été modifié.",
                    false);

            return (nothing, null);
        }

        string? html = content.Html is null ? null : new DeobfuscatePresenter(project).RunOnHtml(content.Html).Text;

        int inferred = report.Substitutions.Count(substitution => substitution.Inferred);
        string details = string.Concat(
            candidates.Count > 1 ? $" avec le coffre « {project.DisplayName} »" : string.Empty,
            ReferenceEquals(project, preferred) ? string.Empty : ", reconnu à ses pseudonymes",
            inferred == 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $", dont {inferred} adresse(s) déduite(s) de leur sous-réseau"),
            html is null ? string.Empty : ", mise en forme conservée",
            report.UnknownTokens.Count == 0
                ? string.Empty
                : string.Create(CultureInfo.CurrentCulture, $". {report.UnknownTokens.Count} jeton(s) inconnu(s) laissé(s) en place — le modèle les a inventés"));

        string message = string.Create(CultureInfo.CurrentCulture, $"{report.Substitutions.Count} pseudonyme(s) restitué(s){details}. Le presse-papiers contient le texte réel, relu et vérifié.");
        if (ambiguous)
        {
            message += $" Attention : seules des adresses ou des courriels ont été reconnus, et d'autres coffres les connaissent aussi ; vérifiez que le texte vient bien de « {project.DisplayName} ».";
        }

        return (new ClipboardOutcome(message, true) { Caution = ambiguous }, new ClipboardContent(report.Text, html));
    }

    /// <summary>
    /// Mesure ce qu'un coffre reconnaît d'un texte : d'abord les jetons signés
    /// de son nonce, qui le désignent à coup sûr, puis l'ensemble des
    /// pseudonymes, adresses et courriels compris.
    /// </summary>
    /// <param name="report">Compte rendu d'une restauration</param>
    /// <returns>Le nombre de jetons signés, puis le nombre total de pseudonymes restitués</returns>
    private static (int Signed, int Total) Evidence(DeobfuscationReport report)
    {
        return (report.Substitutions.Count(substitution => substitution.ProvesOrigin), report.Substitutions.Count);
    }

    // ═══════════════════════════════ Compte rendu ═══════════════════════════════

    /// <summary>
    /// Affiche l'issue d'une action sur le presse-papiers : dans le bandeau
    /// d'état, et sous les boutons de l'accueil quand c'est de là qu'elle vient.
    /// Un dépôt est ensuite contrôlé une seconde fois, peu après.
    /// </summary>
    /// <param name="outcome">Issue de l'action</param>
    private void ReportClipboard(ClipboardOutcome outcome)
    {
        SetStatus(outcome.Message, outcome.Caution ? null : outcome.Success);
        if (outcome.Stamp is not null)
        {
            WatchClipboard(outcome.Stamp);
        }

        if (_project is not null)
        {
            WelcomeClipboardResult.Visibility = Visibility.Collapsed;
            return;
        }

        WelcomeClipboardResult.Visibility = Visibility.Visible;
        WelcomeClipboardResult.Text = outcome.Message;
        WelcomeClipboardResult.Foreground = Themed<Brush>(outcome.Caution
            ? "Warning"
            : outcome.Success switch
            {
                true => "Success",
                false => "Danger",
                _ => "TextSecondary",
            });

        // Le coffre a pu être créé ou enrichi par l'action.
        ShowClipboardProjectHint();
    }

    /// <summary>
    /// Contrôle, peu après un dépôt, qu'aucune autre application ne l'a
    /// remplacé. Un outil qui surveille le presse-papiers peut y remettre
    /// l'ancien contenu : sans ce contrôle, l'écran annoncerait un texte
    /// pseudonymisé qui n'y est plus.
    /// </summary>
    /// <param name="stamp">Trace du dépôt</param>
    private void WatchClipboard(ClipboardStamp stamp)
    {
        _clipboardWatch?.Stop();

        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = ClipboardWatchDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ClipboardBridge.Intruder(stamp) is string intruder)
            {
                ReportClipboard(new ClipboardOutcome(
                    $"Attention : juste après, {intruder} a remplacé le contenu du presse-papiers, qui ne contient plus le texte déposé. Relancez l'opération ; si cela se répète, fermez l'outil qui surveille le presse-papiers.",
                    false));
            }
        };

        _clipboardWatch = timer;
        timer.Start();
    }
}
