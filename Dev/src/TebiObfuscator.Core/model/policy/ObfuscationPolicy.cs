using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Traitement réservé aux adresses IPv4 privées.
/// </summary>
public enum PrivateIpPolicy
{
    /// <summary>
    /// Les conserver telles quelles. Le plan d'adressage interne part alors en
    /// clair : à réserver aux lots où il n'apprend rien sur l'organisation.
    /// </summary>
    Keep,

    /// <summary>
    /// Les remplacer par d'autres adresses privées de la même plage. Défaut :
    /// le plan d'adressage d'une organisation la décrit — ses sites, ses
    /// réseaux —, mais une adresse privée doit rester privée pour que le modèle
    /// raisonne juste. Une adresse de 192.168.0.0/16 en reçoit donc une autre de
    /// 192.168.0.0/16, les sous-réseaux et la partie hôte étant préservés.
    /// </summary>
    Pseudonymize,
}

/// <summary>
/// Décide, pour chaque valeur repérée, si elle doit être obfusquée.
///
/// C'est l'objet que pilote la grille à cocher de l'interface : activer ou
/// désactiver une catégorie, conserver ou non les adresses privées, ajouter un
/// terme à la liste blanche ou à la liste noire.
///
/// La liste noire l'emporte toujours sur la liste blanche : la raison sociale
/// ou un nom de client doivent disparaître même s'ils ressemblent à un nom de
/// produit connu.
/// </summary>
public sealed class ObfuscationPolicy
{
    private readonly HashSet<SpanCategory> _disabledCategories = [];
    private readonly HashSet<string> _alwaysObfuscate = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lineExceptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exemptedLines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoredSuggestions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Liste blanche appliquée après détection.</summary>
    public Allowlist Allowlist { get; } = new();

    /// <summary>Traitement des adresses IPv4 privées.</summary>
    public PrivateIpPolicy PrivateIp { get; set; } = PrivateIpPolicy.Pseudonymize;

    /// <summary>
    /// Coffre consulté pour reconnaître les pseudonymes déjà produits, et ne
    /// pas les obfusquer une seconde fois.
    /// </summary>
    public IPseudonymRegistry? Pseudonyms { get; set; }

    /// <summary>
    /// Identifiants d'entités gardés en clair, sauf les mots explicitement
    /// demandés qu'ils contiennent (voir <see cref="EntityIdentifiers"/>).
    /// </summary>
    public EntityIdentifiers Entities { get; set; } = EntityIdentifiers.None;

    /// <summary>
    /// Indique si une portion repérée l'a été parce qu'on a explicitement
    /// demandé de la masquer — terme du vocabulaire, valeur de la liste noire —,
    /// et non par une règle générale. C'est ce qui la fait remplacer jusque
    /// dans un identifiant d'entité gardé en clair.
    /// </summary>
    /// <param name="span">Portion repérée</param>
    /// <returns><c>true</c> si la portion vient d'une demande explicite</returns>
    public bool IsExplicitlyMasked(DetectedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return span.Category == SpanCategory.ProtectedToken
            || span.RuleId.StartsWith("vocabulary", StringComparison.Ordinal)
            || _alwaysObfuscate.Contains(span.RawValue.Trim());
    }

    /// <summary>Catégories explicitement désactivées par l'utilisateur.</summary>
    public IReadOnlyCollection<SpanCategory> DisabledCategories => _disabledCategories;

    /// <summary>
    /// Désactive une catégorie de détection.
    /// </summary>
    /// <param name="category">Catégorie à ne plus obfusquer</param>
    public void Disable(SpanCategory category) => _disabledCategories.Add(category);

    /// <summary>
    /// Réactive une catégorie de détection.
    /// </summary>
    /// <param name="category">Catégorie à obfusquer de nouveau</param>
    public void Enable(SpanCategory category) => _disabledCategories.Remove(category);

    /// <summary>
    /// Ajoute un terme qui doit toujours être obfusqué, quelle que soit la
    /// liste blanche.
    /// </summary>
    /// <param name="term">Terme à masquer systématiquement</param>
    public void AlwaysObfuscate(string term)
    {
        if (!string.IsNullOrWhiteSpace(term))
        {
            _alwaysObfuscate.Add(term.Trim());
        }
    }

    /// <summary>
    /// Pose une exception sur une occurrence précise.
    /// </summary>
    /// <param name="exception">Occurrence à laisser en clair</param>
    public void Except(LineExemption exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _lineExceptions.Add(exception.Key);
        _exemptedLines.Add(LineExemption.KeyOf(exception.File, exception.Line, exception.Value));
    }

    /// <summary>
    /// Indique si une exception, quel que soit son rang, porte sur une valeur à
    /// une ligne donnée. C'est le test rapide qui évite de calculer le rang
    /// d'une occurrence que rien ne concerne.
    /// </summary>
    /// <param name="file">Clé du fichier (voir <see cref="LineExemption.KeyFor"/>)</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <returns><c>true</c> si au moins une occurrence de la valeur est exceptée sur la ligne</returns>
    public bool HasExemptionOn(string file, int line, string value)
    {
        if (_exemptedLines.Count == 0)
        {
            return false;
        }

        // Une exception posée avant que le chemin ne soit retenu ne porte que
        // le nom du fichier : elle vaut pour tout fichier de ce nom.
        string? legacy = LineExemption.LegacyKeyOf(file);
        return _exemptedLines.Contains(LineExemption.KeyOf(file, line, value))
            || (legacy is not null && _exemptedLines.Contains(LineExemption.KeyOf(legacy, line, value)));
    }

    /// <summary>
    /// Indique si toutes les occurrences d'une valeur sur une ligne sont
    /// exceptées, comme le sont les exceptions posées avant que leur rang ne
    /// soit retenu.
    /// </summary>
    /// <param name="file">Nom du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <returns><c>true</c> si l'occurrence doit rester en clair</returns>
    public bool IsExcepted(string file, int line, string value)
    {
        return IsExcepted(file, line, value, 0);
    }

    /// <summary>
    /// Indique si une occurrence est exceptée : par une exception qui la vise
    /// elle-même, ou par une exception qui vaut pour toute sa ligne.
    /// </summary>
    /// <param name="file">Nom du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <param name="occurrence">Rang de l'occurrence sur sa ligne (voir <see cref="LineExemption.OccurrenceAt"/>)</param>
    /// <returns><c>true</c> si l'occurrence doit rester en clair</returns>
    public bool IsExcepted(string file, int line, string value, int occurrence)
    {
        if (!HasExemptionOn(file, line, value))
        {
            return false;
        }

        string? legacy = LineExemption.LegacyKeyOf(file);
        return IsExceptedUnder(file, line, value, occurrence)
            || (legacy is not null && IsExceptedUnder(legacy, line, value, occurrence));
    }

    /// <summary>
    /// Indique si une occurrence est exceptée sous une clé de fichier précise.
    /// </summary>
    /// <param name="file">Clé du fichier</param>
    /// <param name="line">Numéro de ligne</param>
    /// <param name="value">Valeur concernée</param>
    /// <param name="occurrence">Rang de l'occurrence sur sa ligne</param>
    /// <returns><c>true</c> si l'occurrence doit rester en clair</returns>
    private bool IsExceptedUnder(string file, int line, string value, int occurrence)
    {
        return _lineExceptions.Contains(LineExemption.KeyOf(file, line, value))
            || (occurrence > 0 && _lineExceptions.Contains(LineExemption.KeyOf(file, line, value, occurrence)));
    }

    /// <summary>
    /// Indique si l'occurrence d'une valeur qui commence à une position donnée
    /// d'un texte est exceptée. Le rang n'est calculé que si une exception
    /// porte sur la valeur à cette ligne.
    /// </summary>
    /// <param name="file">Nom du fichier</param>
    /// <param name="line">Numéro de la ligne où commence l'occurrence</param>
    /// <param name="text">Texte source</param>
    /// <param name="position">Début de l'occurrence</param>
    /// <param name="value">Valeur concernée</param>
    /// <returns><c>true</c> si l'occurrence doit rester en clair</returns>
    public bool IsExceptedAt(string file, int line, string text, int position, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);

        return HasExemptionOn(file, line, value)
            && IsExcepted(file, line, value, LineExemption.OccurrenceAt(text, position, value));
    }

    /// <summary>Vrai si au moins une exception est posée.</summary>
    public bool HasLineExemptions => _lineExceptions.Count > 0;

    /// <summary>
    /// Marque une suggestion comme écartée : le moteur cesse de la proposer et
    /// de la signaler.
    /// </summary>
    /// <param name="term">Terme écarté</param>
    public void Ignore(string term)
    {
        if (!string.IsNullOrWhiteSpace(term))
        {
            _ignoredSuggestions.Add(term.Trim());
        }
    }

    /// <summary>
    /// Indique si une suggestion a été écartée par l'utilisateur : gardée en
    /// clair partout, par la liste blanche du projet ou, pour un projet
    /// antérieur à celle-ci, par une proposition écartée.
    /// </summary>
    /// <param name="term">Terme proposé</param>
    /// <returns><c>true</c> si le terme ne doit plus être proposé</returns>
    public bool IsIgnored(string term)
    {
        return IsKeptEverywhere(term);
    }

    /// <summary>
    /// Indique si l'utilisateur a demandé de garder une valeur en clair dans
    /// toutes ses occurrences : elle n'est alors ni masquée, ni signalée, ni
    /// proposée.
    /// </summary>
    /// <param name="value">Valeur examinée</param>
    /// <returns><c>true</c> si la valeur figure dans la liste blanche du projet</returns>
    public bool IsKeptEverywhere(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (Allowlist.IsProjectTerm(value) || _ignoredSuggestions.Contains(value.Trim()));
    }

    /// <summary>
    /// Détermine si un span repéré doit effectivement être remplacé.
    /// </summary>
    /// <param name="span">Span produit par un détecteur</param>
    /// <returns><c>true</c> si la valeur doit être obfusquée</returns>
    public bool ShouldObfuscate(DetectedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        // Les zones gelées ne sont pas des candidats : elles traversent la
        // politique sans être filtrées, sinon l'idempotence tomberait.
        if (span.Category == SpanCategory.ProtectedToken)
        {
            return true;
        }

        string value = span.RawValue.Trim();
        if (_alwaysObfuscate.Contains(value))
        {
            return true;
        }

        if (_disabledCategories.Contains(span.Category))
        {
            return false;
        }

        // Une valeur déjà produite par l'outil traverse le traitement intacte :
        // c'est ce qui rend l'obfuscation idempotente pour les pseudonymes qui
        // ne vivent pas dans une plage réservée.
        if (Pseudonyms?.IsPseudonym(value) == true)
        {
            return false;
        }

        if (span.Category == SpanCategory.IpV4
            && PrivateIp == PrivateIpPolicy.Keep
            && IpAddressTools.TryParse(value, out uint address)
            && IpAddressTools.IsPrivate(address))
        {
            return false;
        }

        return !Allowlist.Contains(span.Category, value);
    }
}
