using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Applique les décisions prises à l'écran de revue.
///
/// La revue est le moment où l'utilisateur reprend la main : décocher une
/// catégorie entière parce que, dans ce lot, les adresses privées font partie
/// du contexte utile ; décocher une valeur particulière qu'une règle a prise à
/// tort pour un secret ; ou au contraire promouvoir dans le vocabulaire un nom
/// de client qu'aucune règle ne pouvait deviner.
///
/// Toutes ces décisions atterrissent dans les réglages et le vocabulaire du
/// projet, et non dans un état transitoire : elles doivent valoir pour les
/// exécutions suivantes, sans quoi il faudrait les reprendre à chaque fois.
/// </summary>
/// <param name="project">Projet à modifier</param>
public sealed class ReviewPresenter(ObfuscationProject project)
{
    /// <summary>
    /// Désactive une catégorie de détection.
    /// </summary>
    /// <param name="category">Catégorie à ne plus masquer</param>
    public void DisableCategory(SpanCategory category)
    {
        string name = category.ToString();
        if (!project.Settings.DisabledCategories.Contains(name))
        {
            project.Settings.DisabledCategories.Add(name);
        }
    }

    /// <summary>
    /// Réactive une catégorie de détection.
    /// </summary>
    /// <param name="category">Catégorie à masquer de nouveau</param>
    public void EnableCategory(SpanCategory category)
    {
        project.Settings.DisabledCategories.Remove(category.ToString());
    }

    /// <summary>
    /// Ajoute une valeur à la liste blanche du projet : elle reste en clair dans
    /// toutes ses occurrences, dans tous les fichiers, et n'est plus ni masquée,
    /// ni signalée, ni proposée.
    /// </summary>
    /// <param name="value">Valeur à ne plus masquer</param>
    public void KeepValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();
        if (!project.Settings.AllowKeep.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            project.Settings.AllowKeep.Add(trimmed);
        }

        // Une valeur qu'on décide de conserver ne peut pas rester dans la liste
        // noire, qui a la priorité, ni parmi les termes à masquer, que la liste
        // des règles montrerait encore : les deux se contrediraient en silence.
        RemoveAll(project.Settings.AlwaysObfuscate, trimmed);
        project.Vocabulary.Remove(trimmed);
    }

    /// <summary>
    /// Retire une valeur de la liste blanche du projet : les règles décident de
    /// nouveau de son sort.
    /// </summary>
    /// <param name="value">Valeur à reconsidérer</param>
    public void ReleaseValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();
        RemoveAll(project.Settings.AllowKeep, trimmed);
        RemoveAll(project.Settings.IgnoredSuggestions, trimmed);
    }

    /// <summary>
    /// Ajoute une valeur à la liste noire du projet, qui l'emporte sur la liste
    /// blanche.
    /// </summary>
    /// <param name="value">Valeur à toujours masquer</param>
    public void AlwaysObfuscate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string trimmed = value.Trim();
        if (!project.Settings.AlwaysObfuscate.Contains(trimmed))
        {
            project.Settings.AlwaysObfuscate.Add(trimmed);
        }

        ReleaseValue(trimmed);
    }

    /// <summary>
    /// Promeut un terme candidat dans le vocabulaire de l'organisation.
    /// </summary>
    /// <param name="term">Terme à retenir</param>
    /// <param name="category">Nature à lui attribuer</param>
    /// <returns><c>true</c> si le terme a été retenu</returns>
    public bool PromoteToVocabulary(string term, SpanCategory category)
    {
        return project.Vocabulary.Add(term, category);
    }

    /// <summary>
    /// Ajoute un terme que l'utilisateur veut masquer, qu'il vienne d'une
    /// suggestion, d'un signalement de résidu ou d'une saisie directe.
    ///
    /// Le terme rejoint le vocabulaire, qui lui donne un pseudonyme stable de
    /// la bonne famille, et la liste noire, qui garantit qu'il sera masqué même
    /// s'il ressemble à une valeur de la liste blanche. Sans ce second geste, un
    /// terme comme <c>Exchange</c> ou <c>Azure</c> demandé explicitement par
    /// l'utilisateur resterait en clair, et l'écran mentirait sur ce qu'il fait.
    /// </summary>
    /// <param name="term">Terme à masquer</param>
    /// <param name="category">Nature à lui attribuer</param>
    /// <returns><c>true</c> si le terme a été retenu ; <c>false</c> s'il est trop court, ou s'il est donné pour une adresse sans en avoir la forme</returns>
    public bool AddMaskedTerm(string term, SpanCategory category)
    {
        if (category == SpanCategory.IpV4 && !IsAddressLike(term))
        {
            return false;
        }

        if (!project.Vocabulary.Add(term, NatureOf(term, category)))
        {
            return false;
        }

        AlwaysObfuscate(term);
        return true;
    }

    /// <summary>
    /// Indique si un terme a la forme d'une adresse IPv4 ou d'un début
    /// d'adresse : <c>192.168.1.25</c>, <c>192.168.1</c>, <c>10.10</c>. Seul un
    /// tel terme peut être traité comme une adresse : son pseudonyme en est une.
    /// </summary>
    /// <param name="term">Terme examiné</param>
    /// <returns><c>true</c> si le terme peut recevoir un pseudonyme d'adresse</returns>
    public static bool IsAddressLike(string term)
    {
        return !string.IsNullOrWhiteSpace(term) && IpAddressTools.TryParseAddressOrPrefix(term.Trim(), out _, out _);
    }

    /// <summary>
    /// Retire un terme de ceux que l'utilisateur veut masquer.
    /// </summary>
    /// <param name="term">Terme à ne plus masquer</param>
    public void RemoveMaskedTerm(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        project.Vocabulary.Remove(term);
        RemoveAll(project.Settings.AlwaysObfuscate, term.Trim());
    }

    /// <summary>
    /// Change la nature attribuée à un terme déjà retenu.
    ///
    /// La catégorie décide de la famille de pseudonyme : un nom pris pour une
    /// société alors qu'il désigne une machine reçoit un jeton de la mauvaise
    /// famille, et le modèle en tire une conclusion fausse. Le terme est donc
    /// réinscrit sous sa nouvelle nature, sans passer par un retrait suivi d'un
    /// ajout qui perdrait sa place dans la liste.
    /// </summary>
    /// <param name="term">Terme concerné</param>
    /// <param name="category">Nouvelle nature</param>
    public void ChangeTermCategory(string term, SpanCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        if (category != SpanCategory.IpV4 || IsAddressLike(term))
        {
            project.Vocabulary.Add(term, NatureOf(term, category));
        }
    }

    /// <summary>
    /// Donne la nature sous laquelle un terme est retenu. Une adresse IPv4 ou
    /// son début — <c>192.168.1</c>, <c>10.10</c> — est une adresse, quelle que
    /// soit la nature choisie : le moteur la traite ainsi de toute façon, et la
    /// liste des règles doit le dire.
    /// </summary>
    /// <param name="term">Terme retenu</param>
    /// <param name="category">Nature choisie</param>
    /// <returns>La nature à enregistrer</returns>
    private static SpanCategory NatureOf(string term, SpanCategory category)
    {
        return IsAddressLike(term) ? SpanCategory.IpV4 : category;
    }

    /// <summary>
    /// Laisse une occurrence précise en clair, sans toucher aux autres
    /// occurrences de la même valeur — ni ailleurs dans le fichier, ni sur la
    /// même ligne.
    /// </summary>
    /// <param name="file">Clé du fichier concerné : son chemin dans la source, précédé d'une barre (voir <see cref="LineExemption.KeyFor"/>)</param>
    /// <param name="line">Numéro de ligne, à partir de 1</param>
    /// <param name="value">Valeur à laisser en clair</param>
    /// <param name="occurrence">Rang de l'occurrence sur la ligne, à partir de 1 ; zéro pour toutes celles de la ligne</param>
    public void ExemptOccurrence(string file, int line, string value, int occurrence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrEmpty(value);

        string entry = new LineExemption(file, line, value) { Occurrence = Math.Max(0, occurrence) }.ToSetting();
        if (!project.Settings.LineExceptions.Contains(entry))
        {
            project.Settings.LineExceptions.Add(entry);
        }
    }

    /// <summary>
    /// Annule l'exception qui laisse une occurrence en clair : celle qui la vise
    /// elle-même, et celle qui valait pour toute sa ligne. Une exception posée
    /// avant que le chemin ne soit retenu, sous le seul nom du fichier, est
    /// retirée elle aussi : elle valait pour tout fichier de ce nom, et cesse
    /// donc de valoir pour tous.
    /// </summary>
    /// <param name="file">Clé du fichier concerné (voir <see cref="LineExemption.KeyFor"/>)</param>
    /// <param name="line">Numéro de ligne, à partir de 1</param>
    /// <param name="value">Valeur concernée</param>
    /// <param name="occurrence">Rang de l'occurrence sur la ligne, à partir de 1 ; zéro pour toutes celles de la ligne</param>
    public void RestoreOccurrence(string file, int line, string value, int occurrence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrEmpty(value);

        string? legacy = LineExemption.LegacyKeyOf(file);
        foreach (string key in legacy is null ? [file] : new[] { file, legacy })
        {
            project.Settings.LineExceptions.Remove(new LineExemption(key, line, value).ToSetting());
            if (occurrence > 0)
            {
                project.Settings.LineExceptions.Remove(new LineExemption(key, line, value) { Occurrence = occurrence }.ToSetting());
            }
        }
    }

    /// <summary>
    /// Garde une proposition du moteur en clair partout : elle rejoint la liste
    /// blanche du projet, et n'est plus suggérée, plus signalée, plus colorée
    /// dans l'aperçu.
    ///
    /// C'est le pendant indispensable de la validation. Sans lui, une chaîne
    /// d'apparence aléatoire mais parfaitement anodine — une empreinte de
    /// paquet, un identifiant de compilation — reparaîtrait à chaque analyse et
    /// finirait par faire ignorer tous les signalements, y compris les bons.
    /// </summary>
    /// <param name="term">Proposition à garder en clair</param>
    public void IgnoreSuggestion(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        // Garder et masquer sont deux réponses opposées à la même question :
        // la liste blanche retire le terme de ceux à masquer.
        KeepValue(term);
    }

    /// <summary>
    /// Remet une proposition gardée en clair dans le circuit.
    /// </summary>
    /// <param name="term">Proposition à reconsidérer</param>
    public void RestoreSuggestion(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ReleaseValue(term);
    }

    /// <summary>
    /// Retire d'une liste de réglages toutes les graphies d'une valeur,
    /// indépendamment de la casse — celle que la liste blanche ignore.
    /// </summary>
    /// <param name="list">Liste de réglages</param>
    /// <param name="value">Valeur à retirer</param>
    private static void RemoveAll(IList<string> list, string value)
    {
        for (int index = list.Count - 1; index >= 0; index--)
        {
            if (string.Equals(list[index].Trim(), value, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Renomme le projet. Le dossier reste son identité réelle ; le nom n'est
    /// qu'une étiquette d'affichage.
    /// </summary>
    /// <param name="name">Nouveau nom, vide pour revenir au nom du dossier</param>
    public void Rename(string? name)
    {
        project.Settings.Name = name?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Modifie le traitement des adresses IPv4 privées.
    /// </summary>
    /// <param name="policy">Traitement souhaité</param>
    public void SetPrivateIpPolicy(PrivateIpPolicy policy)
    {
        project.Settings.PrivateIp = policy;
    }

    /// <summary>
    /// Enregistre les décisions et reconstruit la politique en conséquence.
    ///
    /// Le coffre n'est pas réécrit : aucune décision de revue n'y touche, et
    /// l'écran en prend plusieurs par minute. Seule l'obfuscation l'enrichit,
    /// et elle l'enregistre elle-même.
    /// </summary>
    public void Commit()
    {
        project.RefreshPolicy();
        project.SaveRules();
    }
}
