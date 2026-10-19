using System.Text;

namespace TebiObfuscator.Core.Model.Engine;

/// <summary>
/// Fichier texte chargé en mémoire, avec la mémoire de son encodage d'origine.
///
/// Le fichier de sortie doit être identique à l'entrée partout sauf sur les
/// spans remplacés : cela suppose de restituer la marque d'ordre des octets
/// telle qu'elle était, et de ne pas réécrire les fins de ligne. Comme le
/// moteur ne touche jamais aux caractères hors spans, les CRLF/LF d'origine
/// traversent la chaîne de traitement sans être normalisés.
/// </summary>
public sealed class SourceDocument
{
    private readonly Encoding _encoding;
    private readonly bool _hasByteOrderMark;

    /// <summary>
    /// Construit un document à partir de son texte et de son encodage.
    /// </summary>
    /// <param name="text">Contenu textuel décodé</param>
    /// <param name="encoding">Encodage d'origine, à réutiliser à l'écriture</param>
    /// <param name="hasByteOrderMark">Vrai si le fichier d'origine débutait par une marque d'ordre des octets</param>
    private SourceDocument(string text, Encoding encoding, bool hasByteOrderMark)
    {
        Text = text;
        _encoding = encoding;
        _hasByteOrderMark = hasByteOrderMark;
    }

    /// <summary>Contenu textuel du fichier, en unités UTF-16.</summary>
    public string Text { get; }

    /// <summary>Nom de l'encodage détecté, pour les rapports.</summary>
    public string EncodingName => _hasByteOrderMark ? $"{_encoding.WebName} (BOM)" : _encoding.WebName;

    /// <summary>
    /// Charge un document à partir de ses octets bruts en déterminant son
    /// encodage. L'ordre d'essai n'est pas arbitraire : la marque d'ordre des
    /// octets est décisive quand elle existe, sinon on tente un décodage UTF-8
    /// strict, et l'on se rabat en dernier ressort sur Latin-1 — seul encodage
    /// mono-octet qui garantisse un aller-retour sans perte, donc la seule
    /// solution de repli acceptable pour un outil qui doit restituer le fichier
    /// d'origine à l'octet près.
    /// </summary>
    /// <param name="bytes">Contenu brut du fichier</param>
    /// <returns>Le document chargé, prêt pour la détection</returns>
    public static SourceDocument Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (StartsWith(bytes, [0xFF, 0xFE, 0x00, 0x00]))
        {
            return new SourceDocument(DecodeWithoutPreamble(bytes, Encoding.UTF32), Encoding.UTF32, true);
        }

        if (StartsWith(bytes, [0xEF, 0xBB, 0xBF]))
        {
            return new SourceDocument(DecodeWithoutPreamble(bytes, Encoding.UTF8), Encoding.UTF8, true);
        }

        if (StartsWith(bytes, [0xFF, 0xFE]))
        {
            return new SourceDocument(DecodeWithoutPreamble(bytes, Encoding.Unicode), Encoding.Unicode, true);
        }

        if (StartsWith(bytes, [0xFE, 0xFF]))
        {
            return new SourceDocument(DecodeWithoutPreamble(bytes, Encoding.BigEndianUnicode), Encoding.BigEndianUnicode, true);
        }

        try
        {
            // Décodage strict : toute séquence invalide lève, ce qui signe un
            // fichier qui n'est pas de l'UTF-8.
            UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return new SourceDocument(strictUtf8.GetString(bytes), Encoding.UTF8, false);
        }
        catch (DecoderFallbackException)
        {
            return new SourceDocument(Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
        }
    }

    /// <summary>
    /// Réencode un texte avec l'encodage d'origine du document, en restituant
    /// la marque d'ordre des octets si le fichier d'entrée en comportait une.
    /// </summary>
    /// <param name="text">Texte à écrire, typiquement la sortie obfusquée</param>
    /// <returns>Les octets à écrire sur disque</returns>
    public byte[] ToBytes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] content = _encoding.GetBytes(text);
        if (!_hasByteOrderMark)
        {
            return content;
        }

        byte[] preamble = _encoding.GetPreamble();
        byte[] result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// Indique si l'encodage du document sait écrire tout un texte. Seul
    /// Latin-1 a des limites : il ne connaît que 256 caractères, et remplace
    /// sans prévenir chacun des autres par un point d'interrogation — un tiret
    /// cadratin ou une apostrophe typographique ajoutés par une IA seraient
    /// perdus en silence.
    /// </summary>
    /// <param name="text">Texte à écrire</param>
    /// <returns><c>true</c> si aucun caractère ne serait perdu</returns>
    public bool CanEncode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _encoding.CodePage != Encoding.Latin1.CodePage || !text.Any(character => character > 'ÿ');
    }

    /// <summary>
    /// Réencode un texte avec l'encodage d'origine du document s'il sait
    /// l'écrire en entier, en UTF-8 sinon, plutôt que de perdre des caractères.
    /// </summary>
    /// <param name="text">Texte à écrire</param>
    /// <param name="switched">Vrai si le texte a dû passer en UTF-8</param>
    /// <returns>Les octets à écrire sur disque</returns>
    public byte[] ToBytesOrUtf8(string text, out bool switched)
    {
        ArgumentNullException.ThrowIfNull(text);

        switched = !CanEncode(text);
        return switched ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text) : ToBytes(text);
    }

    /// <summary>
    /// Décode des octets en ignorant leur préambule, déjà identifié par l'appelant.
    /// </summary>
    /// <param name="bytes">Contenu brut, préambule compris</param>
    /// <param name="encoding">Encodage identifié</param>
    /// <returns>Le texte décodé, sans le caractère de marque d'ordre des octets</returns>
    private static string DecodeWithoutPreamble(byte[] bytes, Encoding encoding)
    {
        int preambleLength = encoding.GetPreamble().Length;
        return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
    }

    /// <summary>
    /// Teste si un tableau d'octets débute par la séquence fournie.
    /// </summary>
    /// <param name="bytes">Contenu à tester</param>
    /// <param name="prefix">Séquence attendue en tête</param>
    /// <returns><c>true</c> si le contenu commence par la séquence</returns>
    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix)
    {
        return bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }
}
