using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Change le chiffrement du coffre d'un projet, et dit ce que chaque niveau
/// permet.
///
/// Deux niveaux, choisis par projet. Bas, par défaut : l'application ouvre le
/// coffre sur tout poste où elle est installée — on peut emporter un projet,
/// ou réinstaller son poste, sans perdre de quoi restaurer les réponses de
/// l'IA. Élevé : seul le compte Windows qui a chiffré le coffre, sur son poste,
/// peut l'ouvrir — même avec l'application, un autre poste n'en tire rien.
///
/// Les libellés sont partagés par la fenêtre, la ligne de commande et le
/// rapport, pour que les trois disent la même chose avec les mêmes mots.
/// </summary>
/// <param name="project">Projet dont le coffre est concerné</param>
public sealed class VaultPresenter(ObfuscationProject project)
{
    /// <summary>Chiffrement choisi pour le coffre du projet.</summary>
    public VaultProtection Protection => project.Settings.VaultProtection;

    /// <summary>
    /// Nomme un niveau de chiffrement.
    /// </summary>
    /// <param name="protection">Niveau de chiffrement</param>
    /// <returns>Le nom, en minuscules : « chiffrement bas » ou « chiffrement élevé »</returns>
    public static string Label(VaultProtection protection)
    {
        return protection == VaultProtection.High ? "chiffrement élevé" : "chiffrement bas";
    }

    /// <summary>
    /// Dit qui peut ouvrir un coffre à ce niveau de chiffrement.
    /// </summary>
    /// <param name="protection">Niveau de chiffrement</param>
    /// <returns>La portée du chiffrement, en une proposition</returns>
    public static string Reach(VaultProtection protection)
    {
        return protection == VaultProtection.High
            ? "seul votre compte Windows, sur ce poste, peut ouvrir le coffre"
            : "l'application ouvre le coffre sur tout poste où elle est installée";
    }

    /// <summary>
    /// Change le chiffrement du coffre et le rechiffre aussitôt.
    /// </summary>
    /// <param name="protection">Chiffrement voulu</param>
    /// <returns><c>true</c> si le coffre a été rechiffré, <c>false</c> s'il l'était déjà ainsi</returns>
    public bool ChangeProtection(VaultProtection protection)
    {
        if (protection == project.Settings.VaultProtection && protection == project.StoredProtection)
        {
            return false;
        }

        project.ChangeVaultProtection(protection);
        return true;
    }

    /// <summary>
    /// Rechiffre le coffre dans le chiffrement choisi, s'il est encore
    /// enregistré sous un autre. C'est le cas, une seule fois, des projets
    /// créés avant le choix du chiffrement : leur coffre est en chiffrement
    /// élevé, et le réglage par défaut est bas.
    /// </summary>
    /// <returns>Le chiffrement qu'avait le coffre s'il a été rechiffré, <c>null</c> sinon</returns>
    public VaultProtection? AlignProtection()
    {
        VaultProtection before = project.StoredProtection;
        return project.AlignVaultProtection() ? before : null;
    }

    /// <summary>
    /// Fait entrer au coffre les attributions qu'une analyse a faites dans son
    /// instantané, puis l'enregistre.
    ///
    /// C'est ce qui rend restaurable un texte copié depuis l'aperçu : ses
    /// pseudonymes ont été attribués sur une copie du coffre, et une réponse de
    /// l'IA qui les reprend ne pourrait pas être restituée s'ils n'y entraient
    /// pas. Les attributions étant celles qu'écrirait l'obfuscation, les adopter
    /// ne change rien à ce qu'elle produira. Si le coffre a changé depuis
    /// l'analyse — une autre attribution a pris un de ces pseudonymes, ou la
    /// même valeur en a reçu un autre —, rien n'est adopté : le texte copié ne
    /// correspondrait plus au coffre.
    /// </summary>
    /// <param name="entries">Attributions de l'instantané, absentes du coffre au moment de l'analyse</param>
    /// <returns>Le nombre d'attributions adoptées, et celui des conflits qui ont tout empêché</returns>
    public (int Adopted, int Conflicts) Adopt(IEnumerable<MappingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Adopt(new VaultDelta([.. entries], []));
    }

    /// <summary>
    /// Fait entrer au coffre ce qu'un traitement a ajouté à son instantané :
    /// les attributions nouvelles, et les graphies nouvelles des attributions
    /// anciennes. Même règle que ci-dessus : au moindre conflit, rien n'est
    /// adopté. Le coffre n'est enregistré que s'il a changé.
    /// </summary>
    /// <param name="delta">Ajouts faits dans l'instantané</param>
    /// <returns>Le nombre d'attributions adoptées, et celui des conflits qui ont tout empêché</returns>
    public (int Adopted, int Conflicts) Adopt(VaultDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        List<MappingEntry> missing = [];
        int conflicts = 0;

        foreach (MappingEntry entry in delta.NewEntries)
        {
            if (project.Store.TryGetByValue(entry.Category, entry.Canonical, out MappingEntry existing))
            {
                conflicts += string.Equals(existing.Pseudonym, entry.Pseudonym, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                continue;
            }

            if (project.Store.IsPseudonymTaken(entry.Pseudonym))
            {
                conflicts++;
                continue;
            }

            missing.Add(entry);
        }

        if (conflicts > 0)
        {
            return (0, conflicts);
        }

        foreach (MappingEntry entry in missing)
        {
            MappingEntry stored = project.Store.Add(entry.Category, entry.Canonical, entry.Original, entry.Index, entry.Pseudonym);
            foreach (string variant in entry.Variants)
            {
                stored.RecordVariant(variant);
            }
        }

        // Une graphie nouvelle d'une attribution ancienne se rattache à son
        // pseudonyme, que le coffre connaît forcément : il existait avant le
        // traitement.
        bool changed = missing.Count > 0;
        foreach ((string pseudonym, string variant) in delta.NewVariants)
        {
            if (project.Store.TryGetByPseudonym(pseudonym, out MappingEntry existing)
                && existing.Original != variant
                && !existing.Variants.Contains(variant))
            {
                existing.RecordVariant(variant);
                changed = true;
            }
        }

        if (changed)
        {
            project.Save();
        }

        return (missing.Count, 0);
    }
}
