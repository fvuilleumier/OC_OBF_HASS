using System.Security.Cryptography;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Chiffrement bas du coffre : une clé propre à l'application, la même sur
/// tous les postes où elle est installée.
///
/// C'est ce qui rend un coffre transportable. Copié avec son projet sur un
/// autre poste, ou retrouvé après la réinstallation du sien, il s'ouvre dès que
/// l'application est là — sans phrase de passe, sans lien avec un compte
/// Windows. La contrepartie est à connaître, et c'est elle qui fait de ce
/// chiffrement le niveau « bas » : la clé voyage avec l'application. Le coffre
/// reste illisible pour qui trouve le fichier seul — sauvegarde, copie de
/// dossier, synchronisation, commit par erreur —, pas pour qui dispose aussi de
/// l'application ou de ses sources. Pour un coffre que même eux n'ouvrent pas,
/// il y a le chiffrement élevé, par DPAPI.
///
/// Le chiffrement est authentifié (AES-256-GCM) : un coffre altéré ou tronqué
/// est refusé, jamais relu de travers. Chaque écriture dérive sa propre clé
/// (HKDF-SHA256) d'un sel tiré au hasard, et tire un nonce neuf.
///
/// Format : sel (16 octets) | nonce (12) | étiquette (16) | texte chiffré.
/// </summary>
public sealed class ApplicationKeyMappingProtector : IMappingProtector
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int OverheadSize = SaltSize + NonceSize + TagSize;

    /// <summary>
    /// Contexte de dérivation : il réserve la clé dérivée à ce seul usage.
    /// </summary>
    private static ReadOnlySpan<byte> DerivationContext => "TebiObfuscator/mapping-vault/application-key/v1"u8;

    /// <summary>
    /// Matériau de la clé de l'application.
    ///
    /// Il est écrit ici, en clair, et c'est voulu : le chiffrement bas se
    /// définit précisément par le fait que l'application sait ouvrir le coffre
    /// sur n'importe quel poste. Ce n'est donc pas un secret, et il ne protège
    /// rien de ce que protège le chiffrement élevé.
    ///
    /// Il ne doit jamais changer : tous les coffres en chiffrement bas
    /// deviendraient illisibles. Une nouvelle clé prendrait un nouveau code
    /// dans l'en-tête du coffre, l'ancienne restant connue pour la lecture.
    /// </summary>
    private static ReadOnlySpan<byte> KeyMaterial =>
    [
        0xB2, 0x71, 0x15, 0x3E, 0x32, 0x4D, 0x4D, 0xE6,
        0x07, 0x9F, 0x64, 0x46, 0xB4, 0xE8, 0x73, 0x39,
        0xDD, 0x15, 0xD2, 0xCB, 0x58, 0xA1, 0x0E, 0x4B,
        0xA3, 0xCE, 0x68, 0x20, 0x5E, 0x16, 0xF3, 0x37,
    ];

    /// <inheritdoc />
    public string Name => "Clé de l'application (AES-256-GCM)";

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureSupported();

        byte[] payload = new byte[OverheadSize + plaintext.Length];
        Span<byte> salt = payload.AsSpan(0, SaltSize);
        Span<byte> nonce = payload.AsSpan(SaltSize, NonceSize);
        Span<byte> tag = payload.AsSpan(SaltSize + NonceSize, TagSize);
        Span<byte> ciphertext = payload.AsSpan(OverheadSize);

        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            DeriveKey(salt, key);
            using AesGcm cipher = new(key, TagSize);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            // La clé dérivée ne doit pas traîner en mémoire au-delà de l'usage.
            CryptographicOperations.ZeroMemory(key);
        }

        return payload;
    }

    /// <inheritdoc />
    /// <exception cref="CryptographicException">Si le coffre est tronqué, altéré, ou n'a pas été chiffré par cette application</exception>
    public byte[] Unprotect(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureSupported();

        if (payload.Length < OverheadSize)
        {
            throw new CryptographicException("Coffre tronqué : il est plus court que son seul en-tête de chiffrement.");
        }

        ReadOnlySpan<byte> salt = payload.AsSpan(0, SaltSize);
        ReadOnlySpan<byte> nonce = payload.AsSpan(SaltSize, NonceSize);
        ReadOnlySpan<byte> tag = payload.AsSpan(SaltSize + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = payload.AsSpan(OverheadSize);
        byte[] plaintext = new byte[ciphertext.Length];

        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            DeriveKey(salt, key);
            using AesGcm cipher = new(key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            // Un contenu à moitié déchiffré ne doit jamais servir.
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException(
                "Coffre illisible : il a été altéré, ou n'a pas été chiffré par TebiObfuscator.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Dérive la clé de chiffrement d'une écriture à partir de son sel.
    /// </summary>
    /// <param name="salt">Sel de l'écriture</param>
    /// <param name="key">Clé dérivée, à remplir</param>
    private static void DeriveKey(ReadOnlySpan<byte> salt, Span<byte> key)
    {
        HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyMaterial, key, salt, DerivationContext);
    }

    /// <summary>
    /// Vérifie que la plateforme sait chiffrer en AES-GCM : c'est le cas de
    /// Windows, pas de toutes les plateformes où .NET s'exécute.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Si AES-GCM n'est pas disponible</exception>
    private static void EnsureSupported()
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("Le chiffrement AES-GCM n'est pas disponible sur cette plateforme.");
        }
    }
}
