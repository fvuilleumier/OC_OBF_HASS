using System.Reflection;

namespace TebiObfuscator.Core.Model.Reporting;

/// <summary>
/// Version du produit, telle que la fixe la balise <c>FileVersion</c> de
/// TebiObfuscator.Core.csproj.
///
/// Elle est lue dans les attributs du cœur, là où la compilation l'a inscrite,
/// et non recopiée : la fenêtre, la ligne de commande et le rapport disent
/// ainsi le numéro du fichier réellement livré. Quatre chiffres : la ligne de
/// produit (1.0), la version annoncée (3 pour la V3), puis l'itération dans
/// cette version.
/// </summary>
public static class ProductVersion
{
    /// <summary>Numéro de la version, par exemple <c>1.0.3.9</c>.</summary>
    public static string Current { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";
}
