using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Model.Mapping;

/// <summary>
/// Valeur réelle retrouvée dans un fichier censé être obfusqué.
/// </summary>
/// <param name="Category">Nature de la valeur</param>
/// <param name="MaskedLiteral">Graphie retrouvée, masquée pour le rapport</param>
/// <param name="Pseudonym">Pseudonyme qui aurait dû la remplacer</param>
/// <param name="FirstIndex">Position de la première occurrence</param>
/// <param name="Occurrences">Nombre d'occurrences retrouvées</param>
public sealed record ResidualLiteral(
    SpanCategory Category,
    string MaskedLiteral,
    string Pseudonym,
    int FirstIndex,
    int Occurrences)
{
    /// <summary>
    /// Position et longueur de chaque occurrence retrouvée. Elles permettent de
    /// distinguer une fuite d'un choix de l'utilisateur — une occurrence laissée
    /// en clair à un endroit précis — sans jamais transporter la valeur.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Locations { get; init; } = [];

    /// <summary>
    /// Numéros des lignes où figurent les occurrences, quand ils sont connus :
    /// c'est ce qui permet de retrouver la valeur sans que le rapport la cite.
    /// </summary>
    public IReadOnlyList<int> Lines { get; init; } = [];
}

/// <summary>
/// Contrôle de dernier rideau : après réécriture, aucune des valeurs réelles
/// connues du coffre ne doit subsister dans la sortie.
///
/// Ce contrôle coûte peu et détecte exactement la classe de bugs la plus grave
/// de l'outil — une erreur d'offset, un span tronqué, un détecteur qui décrit
/// mal ce qu'il a vu — c'est-à-dire précisément les cas où une donnée fuit alors
/// que tout semble avoir fonctionné. Il tourne donc systématiquement à chaque
/// obfuscation, et pas seulement sur demande.
/// </summary>
public static class MappingVerifier
{
    /// <summary>
    /// Recherche dans un texte les valeurs réelles connues du coffre.
    ///
    /// La comparaison suit la nature de chaque valeur (voir
    /// <see cref="KnownValueScanner"/>) : un nom est retrouvé sans ses accents
    /// ni sa casse, un secret seulement à l'identique, un port seulement en
    /// position de port. Le résultat est brut : il revient à l'appelant de
    /// distinguer une fuite d'une valeur que l'utilisateur a choisi de laisser
    /// en clair.
    ///
    /// Sans savoir où les pseudonymes ont été écrits — un fichier relu sur
    /// disque —, une occurrence qui est elle-même un pseudonyme du coffre n'est
    /// pas comptée : une adresse privée de substitution peut coïncider avec
    /// l'adresse réelle d'une autre machine, et y voir une fuite serait une
    /// fausse alerte.
    /// </summary>
    /// <param name="output">Texte obfusqué à contrôler</param>
    /// <param name="store">Coffre du projet</param>
    /// <returns>Les valeurs retrouvées, une ligne par entrée du coffre</returns>
    public static IReadOnlyList<ResidualLiteral> FindResiduals(string output, MappingStore store)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(store);

        return Summarize(
            new KnownValueScanner(store.Entries)
                .Scan(output)
                .Where(occurrence => !store.IsPseudonymTaken(output.Substring(occurrence.Start, occurrence.Length))),
            output);
    }

    /// <summary>
    /// Recherche dans un texte produit les valeurs réelles connues du coffre,
    /// en écartant les occurrences qui touchent un pseudonyme que l'outil vient
    /// d'y écrire : ce qu'il a écrit n'est pas une fuite, même s'il ressemble à
    /// une valeur réelle.
    /// </summary>
    /// <param name="output">Texte obfusqué à contrôler</param>
    /// <param name="store">Coffre du projet</param>
    /// <param name="written">Place des pseudonymes écrits, par position croissante</param>
    /// <returns>Les valeurs retrouvées, une ligne par entrée du coffre</returns>
    public static IReadOnlyList<ResidualLiteral> FindResiduals(
        string output,
        MappingStore store,
        IReadOnlyList<(int Start, int Length)> written)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(written);

        List<KnownValueOccurrence> kept = [];
        int cursor = 0;

        // Les occurrences et les pseudonymes sont triés : un curseur suffit.
        foreach (KnownValueOccurrence occurrence in new KnownValueScanner(store.Entries).Scan(output))
        {
            while (cursor < written.Count && written[cursor].Start + written[cursor].Length <= occurrence.Start)
            {
                cursor++;
            }

            bool touchesPseudonym = cursor < written.Count && written[cursor].Start < occurrence.Start + occurrence.Length;
            if (!touchesPseudonym)
            {
                kept.Add(occurrence);
            }
        }

        return Summarize(kept, output);
    }

    /// <summary>
    /// Regroupe des occurrences par entrée du coffre.
    /// </summary>
    /// <param name="occurrences">Occurrences retrouvées</param>
    /// <param name="text">Texte dans lequel elles ont été retrouvées</param>
    /// <returns>Une ligne par entrée, valeur masquée</returns>
    public static IReadOnlyList<ResidualLiteral> Summarize(IEnumerable<KnownValueOccurrence> occurrences, string text)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(text);

        List<ResidualLiteral> residuals = [];
        foreach (IGrouping<MappingEntry, KnownValueOccurrence> group in occurrences.GroupBy(occurrence => occurrence.Entry))
        {
            List<(int Start, int Length)> locations = [.. group.Select(occurrence => (occurrence.Start, occurrence.Length))];
            residuals.Add(new ResidualLiteral(
                group.Key.Category,
                SensitiveText.Mask(text.Substring(locations[0].Start, locations[0].Length)),
                group.Key.Pseudonym,
                locations[0].Start,
                locations.Count)
            {
                Locations = locations,
            });
        }

        return residuals;
    }
}
