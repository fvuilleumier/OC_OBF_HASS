using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Model.Reporting;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Base des objets affichés : notifie le moteur de liaison des changements de
/// propriété.
/// </summary>
public abstract class NotifyingObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Signale un changement de propriété au moteur de liaison.
    /// </summary>
    /// <param name="propertyName">Nom de la propriété modifiée</param>
    protected void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Base des lignes affichées avec une case à cocher, seul état que
/// l'utilisateur modifie directement.
///
/// Deux notifications distinctes coexistent, et c'est le point important. La
/// notification de propriété sert à l'affichage et part à chaque changement ;
/// l'événement <see cref="SelectionChanged"/> ne part que lorsque la case est
/// basculée par la liaison, c'est-à-dire par l'utilisateur. L'écran peut ainsi
/// réaligner une case sur l'état du projet sans que ce réalignement soit pris
/// pour une décision — et rejoué.
/// </summary>
public abstract class NotifyingRow : NotifyingObject
{
    private bool _isSelected;

    /// <summary>Levé lorsque l'utilisateur coche ou décoche la ligne.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Vrai si la ligne est cochée.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            NotifyPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Fixe l'état coché pour l'affichage seulement, sans lever
    /// <see cref="SelectionChanged"/> : un rafraîchissement ne doit pas se
    /// faire passer pour une décision de l'utilisateur.
    /// </summary>
    /// <param name="value">État à appliquer</param>
    public void SetSelectedSilently(bool value)
    {
        if (_isSelected == value)
        {
            return;
        }

        _isSelected = value;
        NotifyPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Nature d'une règle de masquage affichée à l'utilisateur.</summary>
public enum MaskTargetKind
{
    /// <summary>Famille de valeurs que le moteur repère seul.</summary>
    Category,

    /// <summary>Terme nommé que l'utilisateur a retenu.</summary>
    Term,
}

/// <summary>
/// Ligne de la liste « Ce qui sera masqué ».
///
/// Les deux leviers de l'outil y cohabitent délibérément. Une catégorie est une
/// famille que le moteur sait reconnaître seul ; un terme est un mot que seul
/// l'utilisateur peut désigner, parce qu'aucune règle ne distingue un nom de
/// client d'un mot ordinaire. Les séparer dans deux écrans obligeait à
/// comprendre cette différence avant de pouvoir agir ; les réunir sous une
/// seule question — qu'est-ce qui part masqué — rend l'écran lisible.
/// </summary>
public sealed class MaskTargetRow : NotifyingRow
{
    /// <summary>
    /// Construit une ligne pour une catégorie détectée.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <param name="occurrences">Nombre d'occurrences dans le lot</param>
    /// <param name="distinctValues">Nombre de valeurs distinctes</param>
    /// <param name="selected">État initial de la case</param>
    /// <returns>La ligne correspondante</returns>
    public static MaskTargetRow ForCategory(SpanCategory category, int occurrences, int distinctValues, bool selected)
    {
        MaskTargetRow row = new()
        {
            Kind = MaskTargetKind.Category,
            Category = category,
            Label = CategoryLabels.Label(category),
            Occurrences = occurrences,
            Detail = string.Create(
                CultureInfo.CurrentCulture,
                $"{distinctValues} valeur(s) · {occurrences} occurrence(s)"),
            Tooltip = string.Create(
                CultureInfo.CurrentCulture,
                $"Détecté automatiquement : {CategoryLabels.Description(category)}.\nDécochez pour laisser ces valeurs en clair."),
        };

        row.SetSelectedSilently(selected);
        return row;
    }

    /// <summary>
    /// Construit une ligne pour une catégorie que l'utilisateur a désactivée,
    /// et qui ne produit donc plus aucune valeur à compter.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <returns>La ligne correspondante, décochée</returns>
    public static MaskTargetRow ForDisabledCategory(SpanCategory category)
    {
        MaskTargetRow row = new()
        {
            Kind = MaskTargetKind.Category,
            Category = category,
            Label = CategoryLabels.Label(category),
            Detail = "laissée en clair · cochez pour la masquer",
            Tooltip = string.Create(
                CultureInfo.CurrentCulture,
                $"Famille reconnue par le moteur, mais laissée en clair : {CategoryLabels.Description(category)}.\nCochez pour la masquer ; l'effet apparaîtra à la prochaine analyse."),
        };

        row.SetSelectedSilently(false);
        return row;
    }

    /// <summary>
    /// Construit une ligne pour un terme retenu par l'utilisateur.
    /// </summary>
    /// <param name="term">Libellé du terme</param>
    /// <param name="category">Catégorie attribuée</param>
    /// <returns>La ligne correspondante, cochée</returns>
    public static MaskTargetRow ForTerm(string term, SpanCategory category)
    {
        MaskTargetRow row = new()
        {
            Kind = MaskTargetKind.Term,
            Category = category,
            Term = term,
            Label = term,
            Detail = CategoryLabels.Label(category),
            Tooltip = string.Create(
                CultureInfo.CurrentCulture,
                $"Terme que vous avez retenu. Il sera masqué partout où il apparaît, dans tous les fichiers du projet.\nDécochez pour le retirer des règles ; clic droit pour changer sa nature."),
        };

        row.SetSelectedSilently(true);
        return row;
    }

    /// <summary>Nature de la règle.</summary>
    public MaskTargetKind Kind { get; private init; }

    /// <summary>Catégorie concernée.</summary>
    public SpanCategory Category { get; private init; }

    /// <summary>Terme, pour une règle de type terme.</summary>
    public string? Term { get; private init; }

    /// <summary>Libellé affiché.</summary>
    public string Label { get; private init; } = string.Empty;

    /// <summary>Précision affichée sous le libellé.</summary>
    public string Detail { get; private init; } = string.Empty;

    /// <summary>Explication affichée au survol.</summary>
    public string Tooltip { get; private init; } = string.Empty;

    /// <summary>Nombre d'occurrences, pour le tri.</summary>
    public int Occurrences { get; private init; }

    /// <summary>Pastille distinguant une catégorie d'un terme.</summary>
    public string KindLabel => Kind == MaskTargetKind.Category ? "auto" : "vous";
}

/// <summary>
/// Ligne de la grille des valeurs repérées. Les valeurs y sont affichées en
/// clair : c'est la raison d'être de l'écran, et le seul endroit du produit où
/// elles apparaissent sans masquage.
/// </summary>
/// <param name="finding">Valeur repérée par le relevé</param>
public sealed class FindingRow(ScanFinding finding) : NotifyingRow
{
    /// <summary>Catégorie technique, conservée pour les décisions.</summary>
    public SpanCategory Category { get; } = finding.Category;

    /// <summary>Libellé lisible de la catégorie.</summary>
    public string CategoryLabel { get; } = CategoryLabels.Label(finding.Category);

    /// <summary>Valeur réelle.</summary>
    public string Value { get; } = finding.Value;

    /// <summary>Nombre d'occurrences dans le lot.</summary>
    public int Occurrences { get; } = finding.Occurrences;

    /// <summary>Fichiers où la valeur apparaît.</summary>
    public string Files { get; } = string.Join(", ", finding.Files);

    /// <summary>Règle l'ayant repérée.</summary>
    public string RuleId { get; } = finding.RuleId;
}

/// <summary>
/// Décision prise sur une proposition du moteur.
///
/// Les trois états sont nécessaires, et le troisième surtout. Sans « écartée »,
/// une chaîne d'apparence aléatoire mais parfaitement anodine — une empreinte
/// de paquet, un identifiant de compilation — reparaîtrait à chaque analyse et
/// finirait par faire ignorer tous les signalements, y compris les bons.
/// </summary>
public enum SuggestionState
{
    /// <summary>Proposée, sans décision prise.</summary>
    Proposed,

    /// <summary>Validée : le terme rejoint les règles du projet.</summary>
    Accepted,

    /// <summary>Gardée en clair partout : elle rejoint la liste blanche, le moteur cesse de la proposer et de la signaler.</summary>
    Ignored,
}

/// <summary>
/// Ligne de la liste blanche du projet : une valeur gardée en clair partout.
/// </summary>
/// <param name="value">Valeur gardée en clair</param>
public sealed class WhitelistRow(string value)
{
    /// <summary>Valeur gardée en clair.</summary>
    public string Value { get; } = value;
}

/// <summary>
/// Ligne des occurrences gardées en clair à un endroit précis.
/// </summary>
/// <param name="exemption">Exception enregistrée au projet</param>
public sealed class ExemptionRow(LineExemption exemption)
{
    /// <summary>Exception enregistrée au projet.</summary>
    public LineExemption Exemption { get; } = exemption;

    /// <summary>
    /// Fichier, par son chemin dans la source. Une exception posée avant que
    /// le chemin ne soit retenu vaut pour tout fichier de ce nom, et le dit.
    /// </summary>
    public string File => Exemption.File.StartsWith(LineExemption.PathKeyMark)
        ? LineExemption.DisplayFile(Exemption.File)
        : $"{Exemption.File} (tout fichier de ce nom)";

    /// <summary>Ligne, et rang de l'occurrence sur la ligne quand la ligne en compte plusieurs.</summary>
    public string Place => Exemption.Occurrence > 1
        ? string.Create(CultureInfo.CurrentCulture, $"{Exemption.Line} · {Exemption.Occurrence}e occurrence")
        : Exemption.Line.ToString(CultureInfo.CurrentCulture);

    /// <summary>Valeur gardée en clair, sur une ligne.</summary>
    public string Value => Exemption.Value.ReplaceLineEndings(" ⏎ ");
}

/// <summary>Origine d'une suggestion.</summary>
public enum SuggestionOrigin
{
    /// <summary>Mot récurrent repéré par l'heuristique de vocabulaire.</summary>
    Vocabulary,

    /// <summary>Chaîne signalée par l'analyse de résidus.</summary>
    Residual,
}

/// <summary>
/// Ligne de la liste des suggestions.
///
/// Elle réunit deux familles que l'utilisateur traite pourtant du même geste :
/// les mots récurrents que l'heuristique propose, et les chaînes que l'analyse
/// de résidus signale sans les masquer. Dans les deux cas la question est la
/// même — faut-il masquer ceci ? — et la cocher ajoute le terme aux règles du
/// projet.
/// </summary>
public sealed class SuggestionRow : NotifyingRow
{
    /// <summary>
    /// Construit une suggestion issue de l'heuristique de vocabulaire.
    /// </summary>
    /// <param name="candidate">Terme proposé</param>
    /// <returns>La ligne correspondante</returns>
    public static SuggestionRow ForCandidate(VocabularyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new SuggestionRow
        {
            Term = candidate.Text,
            Origin = SuggestionOrigin.Vocabulary,
            OriginLabel = "Mot récurrent",
            Occurrences = candidate.Occurrences,
            SuggestedCategory = candidate.SuggestedCategory,
            Context = candidate.SampleLine,
            Why = "Ce mot revient plusieurs fois et ne ressemble à aucun terme technique connu : c'est peut-être un nom de client, de personne ou de machine.",
        };
    }

    /// <summary>
    /// Construit une suggestion issue de l'analyse de résidus.
    /// </summary>
    /// <param name="finding">Élément signalé</param>
    /// <param name="filePath">Fichier concerné</param>
    /// <returns>La ligne correspondante</returns>
    public static SuggestionRow ForResidual(ResidualFinding finding, string filePath)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new SuggestionRow
        {
            Term = finding.Value,
            Origin = SuggestionOrigin.Residual,
            OriginLabel = finding.Kind,
            Occurrences = 1,
            SuggestedCategory = finding.SuggestedCategory,
            Context = string.Create(
                CultureInfo.CurrentCulture,
                $"{Path.GetFileName(filePath)} ligne {finding.Line}"),
            Why = finding.Advice,
            Score = finding.Score,
            FilePath = filePath,
            Line = finding.Line,
        };
    }

    private SuggestionState _state;

    /// <summary>
    /// Levé lorsque l'utilisateur change la décision, jamais lors d'un
    /// réalignement de l'affichage.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Terme réel, tel qu'il sera ajouté aux règles.</summary>
    public string Term { get; private init; } = string.Empty;

    /// <summary>Décision prise sur cette proposition.</summary>
    public SuggestionState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(IsAccepted));
            NotifyPropertyChanged(nameof(IsIgnored));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Vrai si la proposition est validée : le terme sera masqué.</summary>
    public bool IsAccepted
    {
        get => _state == SuggestionState.Accepted;
        set => State = value ? SuggestionState.Accepted : SuggestionState.Proposed;
    }

    /// <summary>Vrai si la proposition est écartée : le moteur n'en parlera plus.</summary>
    public bool IsIgnored
    {
        get => _state == SuggestionState.Ignored;
        set => State = value ? SuggestionState.Ignored : SuggestionState.Proposed;
    }

    /// <summary>
    /// Fixe la décision sans prévenir personne, pour qu'un rafraîchissement de
    /// l'écran ne se fasse pas passer pour un choix de l'utilisateur.
    /// </summary>
    /// <param name="state">Décision à refléter</param>
    public void SetStateSilently(SuggestionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        NotifyPropertyChanged(nameof(State));
        NotifyPropertyChanged(nameof(IsAccepted));
        NotifyPropertyChanged(nameof(IsIgnored));
    }

    /// <summary>Origine de la suggestion.</summary>
    public SuggestionOrigin Origin { get; private init; }

    /// <summary>Libellé de l'origine.</summary>
    public string OriginLabel { get; private init; } = string.Empty;

    /// <summary>Nombre d'occurrences connues.</summary>
    public int Occurrences { get; private init; }

    /// <summary>Catégorie proposée, modifiable par l'utilisateur.</summary>
    public SpanCategory SuggestedCategory { get; set; }

    /// <summary>Libellé de la catégorie proposée.</summary>
    public string CategoryLabel => CategoryLabels.Label(SuggestedCategory);

    /// <summary>Contexte d'apparition, pour juger sur pièce.</summary>
    public string Context { get; private init; } = string.Empty;

    /// <summary>Raison de la suggestion, affichée au survol.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Score d'entropie, pour un signalement de résidu.</summary>
    public double Score { get; private init; }

    /// <summary>Fichier concerné, pour un signalement de résidu.</summary>
    public string? FilePath { get; private init; }

    /// <summary>Ligne concernée, pour un signalement de résidu.</summary>
    public int Line { get; private init; }
}
