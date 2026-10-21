using System.Security.Cryptography;
using System.Text;

namespace MHassnage.Core.Service;

/// <summary>
/// Chiffre les secrets de la configuration (jeton Hass…) avec une clé propre à
/// l'application : la valeur se relit sur n'importe quel poste où MHassnage est
/// installé, mais reste illisible pour qui trouve le fichier seul (sauvegarde,
/// copie, synchronisation).
///
/// Limite à connaître : la clé voyage avec l'application. Qui dispose de
/// l'exécutable ou des sources peut déchiffrer.
///
/// AES-256-GCM, clé dérivée par HKDF-SHA256 d'un sel aléatoire, nonce neuf.
/// Format : « enc:v1: » + base64( sel 16 | nonce 12 | étiquette 16 | chiffré ).
/// </summary>
public static class SecretProtector
{
    public const string Prefix = "enc:v1:";

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Overhead = SaltSize + NonceSize + TagSize;

    private static ReadOnlySpan<byte> Context => "MHassnage/settings-secret/v1"u8;

    /// <summary>Ne doit jamais changer : les secrets déjà chiffrés deviendraient illisibles.</summary>
    private static ReadOnlySpan<byte> KeyMaterial =>
    [
        0x31, 0x48, 0x4B, 0x72, 0x98, 0xB1, 0x2A, 0x90,
        0x2F, 0xD3, 0x92, 0x95, 0x74, 0xB5, 0x09, 0xA3,
        0x55, 0xFE, 0x51, 0x84, 0xB5, 0x7D, 0x4C, 0x8A,
        0x9F, 0x28, 0xA1, 0x3A, 0xEE, 0x4F, 0xC3, 0xBB,
    ];

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plaintext)
    {
        byte[] clear = Encoding.UTF8.GetBytes(plaintext);
        byte[] payload = new byte[Overhead + clear.Length];
        Span<byte> salt = payload.AsSpan(0, SaltSize);
        Span<byte> nonce = payload.AsSpan(SaltSize, NonceSize);
        Span<byte> tag = payload.AsSpan(SaltSize + NonceSize, TagSize);

        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyMaterial, key, salt, Context);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, clear, payload.AsSpan(Overhead), tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }

        return Prefix + Convert.ToBase64String(payload);
    }

    /// <exception cref="CryptographicException">Valeur altérée ou chiffrée par une autre clé.</exception>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;

        byte[] payload = Convert.FromBase64String(value[Prefix.Length..]);
        if (payload.Length < Overhead) throw new CryptographicException("Secret tronqué.");

        byte[] clear = new byte[payload.Length - Overhead];
        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyMaterial, key, payload.AsSpan(0, SaltSize), Context);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(payload.AsSpan(SaltSize, NonceSize), payload.AsSpan(Overhead),
                        payload.AsSpan(SaltSize + NonceSize, TagSize), clear);
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }
}
