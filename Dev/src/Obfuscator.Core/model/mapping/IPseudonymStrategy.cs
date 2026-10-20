using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Mapping;

/// <summary>
/// Élément de contexte fourni à une stratégie pour fabriquer un pseudonyme.
/// </summary>
/// <param name="Category">Nature de la valeur à remplacer</param>
/// <param name="Canonical">Forme canonique de la valeur réelle</param>
/// <param name="Original">Graphie observée dans le fichier source</param>
/// <param name="Index">Numéro d'ordre réservé dans la famille de la catégorie</param>
/// <param name="Format">Format des jetons du projet</param>
public readonly record struct PseudonymRequest(
    SpanCategory Category,
    string Canonical,
    string Original,
    int Index,
    TokenFormat Format);

/// <summary>
/// Fabrique le pseudonyme d'une catégorie de valeurs.
///
/// L'outil combine deux styles de pseudonymes, d'où cette abstraction : un
/// jeton neutre là où la forme n'apporte rien au raisonnement du modèle
/// (secrets, identités), et une valeur du même type là où elle porte du sens
/// exploitable — une adresse IP doit rester une adresse IP pour qu'un masque de
/// sous-réseau reste vérifiable et qu'un fichier de configuration reste valide.
/// </summary>
public interface IPseudonymStrategy
{
    /// <summary>
    /// Indique si cette stratégie prend en charge une catégorie donnée.
    /// </summary>
    /// <param name="category">Catégorie à traiter</param>
    /// <returns><c>true</c> si la stratégie sait produire un pseudonyme pour cette catégorie</returns>
    bool CanHandle(SpanCategory category);

    /// <summary>
    /// Fabrique le pseudonyme correspondant à une valeur.
    /// </summary>
    /// <param name="request">Contexte de fabrication</param>
    /// <returns>Le pseudonyme à substituer dans les sorties</returns>
    string Create(PseudonymRequest request);

    /// <summary>
    /// Prend connaissance d'un texte source avant toute attribution, pour que
    /// les pseudonymes évitent les valeurs réelles qu'il contient. Seules les
    /// stratégies dont les pseudonymes ressemblent à des valeurs réelles — une
    /// adresse privée de substitution est une adresse privée — en ont l'usage.
    /// </summary>
    /// <param name="sourceText">Contenu d'un fichier du lot</param>
    void Observe(string sourceText)
    {
    }
}

/// <summary>
/// Stratégie par défaut : un jeton neutre et explicitement marqué, de la forme
/// <c>TBXA7_SECRET_0001</c>. Elle sert de filet pour toute catégorie qu'aucune
/// stratégie spécialisée ne revendique, et reste le choix délibéré pour les
/// secrets et les identités — on veut que le modèle sache qu'il s'agit d'un
/// substitut et qu'il ne raisonne pas sur son contenu.
/// </summary>
public sealed class NeutralTokenStrategy : IPseudonymStrategy
{
    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => true;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        return request.Format.Compose(request.Category.FamilyCode(), request.Index);
    }
}
