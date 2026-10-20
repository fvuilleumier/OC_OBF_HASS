using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Obfuscator.Core.Model.Mapping;

/// <summary>
/// Chiffrement élevé du coffre : DPAPI, lié au compte Windows courant.
///
/// Aucune phrase de passe à saisir à chaque restauration, et un coffre
/// illisible depuis un autre compte ou une autre machine, même pour qui dispose
/// de l'application. La contrepartie assumée est qu'un coffre n'est pas
/// transférable, et qu'il disparaît avec le compte ou le poste : pour emporter
/// un projet, il faut d'abord repasser son coffre en chiffrement bas, sur le
/// poste d'origine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMappingProtector : IMappingProtector
{
    /// <summary>
    /// Entropie additionnelle propre à l'application : elle empêche qu'un autre
    /// programme tournant sous le même compte puisse déchiffrer le coffre en
    /// appelant simplement DPAPI sur le fichier.
    ///
    /// Elle porte l'ancien nom du produit et ne doit jamais changer : les
    /// coffres en chiffrement élevé déjà écrits ne s'ouvriraient plus.
    /// </summary>
    private static readonly byte[] ApplicationEntropy =
        Encoding.UTF8.GetBytes("TebiObfuscator/mapping-vault/v1");

    /// <inheritdoc />
    public string Name => "DPAPI (compte Windows courant)";

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, ApplicationEntropy, DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    /// <exception cref="CryptographicException">Si le coffre a été chiffré pour un autre compte ou un autre poste, ou s'il est endommagé</exception>
    public byte[] Unprotect(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return ProtectedData.Unprotect(payload, ApplicationEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            // Le message de DPAPI — « clé non valide pour une utilisation dans
            // l'état spécifié » — ne dit pas la cause probable : un coffre
            // venu d'un autre poste ou d'un autre compte.
            throw new CryptographicException(
                "Coffre illisible pour ce compte Windows : il est en chiffrement élevé, que seul le compte qui l'a chiffré, sur son poste, peut ouvrir — à moins qu'il ne soit endommagé. Pour emporter un projet, passez d'abord son coffre en chiffrement bas sur le poste d'origine.",
                exception);
        }
    }
}
