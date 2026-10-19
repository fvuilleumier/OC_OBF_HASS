using System.Globalization;
using System.Runtime.Versioning;

namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Les deux chiffrements possibles d'un coffre, et l'en-tête qui dit, sur
/// disque, lequel a servi.
///
/// Le chiffrement d'écriture est celui que demande le projet ; celui de
/// lecture est celui qu'indique le fichier. C'est ce qui permet de changer de
/// niveau sans rien perdre : un coffre se relit toujours, quel que soit le
/// réglage du moment, puis se réécrit dans le nouveau.
///
/// En-tête : « TBXV » | version du format (1) | code du chiffrement (1 = bas,
/// 2 = élevé), suivi du contenu chiffré. Un coffre sans en-tête date d'avant
/// le choix du chiffrement : c'est un bloc DPAPI brut, donc un chiffrement
/// élevé.
/// </summary>
/// <param name="low">Protection du chiffrement bas</param>
/// <param name="high">Protection du chiffrement élevé</param>
public sealed class VaultProtectors(IMappingProtector low, IMappingProtector high)
{
    private const byte FormatVersion = 1;
    private const byte LowCode = 1;
    private const byte HighCode = 2;
    private const int HeaderLength = 6;

    private static ReadOnlySpan<byte> Magic => "TBXV"u8;

    /// <summary>
    /// Assemble les chiffrements de l'application : sa propre clé pour le
    /// chiffrement bas, DPAPI pour le chiffrement élevé.
    /// </summary>
    /// <returns>Les protections à confier aux projets</returns>
    [SupportedOSPlatform("windows")]
    public static VaultProtectors CreateDefault()
    {
        return new VaultProtectors(new ApplicationKeyMappingProtector(), new DpapiMappingProtector());
    }

    /// <summary>
    /// Donne la protection qui écrit un coffre dans le chiffrement demandé et
    /// relit un coffre quel que soit le sien.
    /// </summary>
    /// <param name="protection">Chiffrement à appliquer à l'écriture</param>
    /// <returns>La protection à passer au coffre</returns>
    public IMappingProtector For(VaultProtection protection)
    {
        return new Envelope(this, protection);
    }

    /// <summary>
    /// Lit, dans l'en-tête d'un fichier de coffre, le chiffrement qui a servi
    /// à l'écrire.
    /// </summary>
    /// <param name="path">Chemin du coffre</param>
    /// <returns>Le chiffrement du coffre</returns>
    /// <exception cref="InvalidDataException">Si l'en-tête est tronqué ou d'un format inconnu</exception>
    public static VaultProtection Identify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Span<byte> header = stackalloc byte[HeaderLength];
        using FileStream stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        return Identify(header[..read]);
    }

    /// <summary>
    /// Lit, dans l'en-tête d'un contenu de coffre, le chiffrement qui a servi
    /// à l'écrire.
    /// </summary>
    /// <param name="payload">Contenu du coffre, ou au moins son début</param>
    /// <returns>Le chiffrement du coffre</returns>
    /// <exception cref="InvalidDataException">Si l'en-tête est tronqué ou d'un format inconnu</exception>
    public static VaultProtection Identify(ReadOnlySpan<byte> payload)
    {
        // Un coffre écrit avant le choix du chiffrement est un bloc DPAPI brut,
        // qui ne peut pas commencer par l'en-tête : il débute par le numéro de
        // version DPAPI, 01 00 00 00.
        if (!payload.StartsWith(Magic))
        {
            return VaultProtection.High;
        }

        if (payload.Length < HeaderLength)
        {
            throw new InvalidDataException("Coffre tronqué : son en-tête est incomplet.");
        }

        if (payload[4] != FormatVersion)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.CurrentCulture,
                $"Coffre d'un format inconnu (version {payload[4]}) : il a sans doute été écrit par une version plus récente de TebiObfuscator."));
        }

        return payload[5] switch
        {
            LowCode => VaultProtection.Low,
            HighCode => VaultProtection.High,
            _ => throw new InvalidDataException(string.Create(
                CultureInfo.CurrentCulture,
                $"Coffre au chiffrement inconnu (code {payload[5]}) : il a sans doute été écrit par une version plus récente de TebiObfuscator.")),
        };
    }

    /// <summary>
    /// Donne la protection d'un niveau de chiffrement.
    /// </summary>
    /// <param name="protection">Niveau de chiffrement</param>
    /// <returns>La protection correspondante</returns>
    private IMappingProtector Of(VaultProtection protection)
    {
        return protection == VaultProtection.High ? high : low;
    }

    /// <summary>
    /// Protection vue par le coffre : elle écrit l'en-tête puis délègue au
    /// chiffrement demandé, et relit l'en-tête pour choisir le déchiffrement.
    /// </summary>
    /// <param name="protectors">Chiffrements disponibles</param>
    /// <param name="protection">Chiffrement à appliquer à l'écriture</param>
    private sealed class Envelope(VaultProtectors protectors, VaultProtection protection) : IMappingProtector
    {
        /// <inheritdoc />
        public string Name => protectors.Of(protection).Name;

        /// <inheritdoc />
        public byte[] Protect(byte[] plaintext)
        {
            byte[] body = protectors.Of(protection).Protect(plaintext);
            byte[] payload = new byte[HeaderLength + body.Length];

            Magic.CopyTo(payload);
            payload[4] = FormatVersion;
            payload[5] = protection == VaultProtection.High ? HighCode : LowCode;
            body.CopyTo(payload, HeaderLength);

            return payload;
        }

        /// <inheritdoc />
        public byte[] Unprotect(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            VaultProtection stored = Identify(payload);
            byte[] body = payload.AsSpan().StartsWith(Magic) ? payload[HeaderLength..] : payload;
            return protectors.Of(stored).Unprotect(body);
        }
    }
}
