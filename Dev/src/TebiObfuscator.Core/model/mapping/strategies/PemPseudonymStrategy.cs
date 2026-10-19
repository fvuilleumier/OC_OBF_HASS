using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TebiObfuscator.Core.Model.Engine;

namespace TebiObfuscator.Core.Model.Mapping.Strategies;

/// <summary>
/// Remplace un bloc de clé ou de certificat par un bloc de même forme.
///
/// Un jeton neutre à la place d'un bloc PEM ferait perdre au modèle une
/// information qui compte : qu'il y a là une clé privée RSA, ou un certificat,
/// et à quel endroit du fichier. Le pseudonyme conserve donc l'en-tête, le
/// pied, le nombre de lignes et leur longueur, et ne remplace que le corps.
///
/// Le corps de substitution n'utilise que des lettres. C'est délibéré : un
/// corps contenant quatre chiffres consécutifs pourrait être confondu avec un
/// jeton neutre par le reconnaisseur tolérant de la désobfuscation, qui
/// restituerait alors une sous-partie du bloc au lieu du bloc entier.
/// </summary>
public sealed class PemPseudonymStrategy : IPseudonymStrategy
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Longueur du préfixe encodant le numéro d'ordre dans le corps.</summary>
    private const int MarkerLength = 6;

    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.KeyBlock;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string original = request.Original;

        if (original.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            return CreatePemBlock(original, request);
        }

        if (original.StartsWith("ssh-", StringComparison.Ordinal))
        {
            return CreateSshPublicKey(original, request);
        }

        return request.Format.Compose(SpanCategory.KeyBlock.FamilyCode(), request.Index);
    }

    /// <summary>
    /// Reconstruit un bloc PEM de même enveloppe et de même gabarit de lignes.
    /// </summary>
    /// <param name="original">Bloc d'origine</param>
    /// <param name="request">Contexte de fabrication</param>
    /// <returns>Le bloc de substitution</returns>
    private static string CreatePemBlock(string original, PseudonymRequest request)
    {
        string[] lines = original.Split('\n');
        if (lines.Length < 3)
        {
            return request.Format.Compose(SpanCategory.KeyBlock.FamilyCode(), request.Index);
        }

        string lineEnding = lines[0].EndsWith('\r') ? "\r\n" : "\n";
        string header = lines[0].TrimEnd('\r');
        string footer = lines[^1].TrimEnd('\r');

        // L'indentation du corps est reprise de la première ligne de contenu :
        // dans un YAML, un bloc PEM est indenté, et le perdre casserait le
        // document.
        string firstBodyLine = lines[1].TrimEnd('\r');
        string indentation = firstBodyLine[..(firstBodyLine.Length - firstBodyLine.TrimStart().Length)];

        // Le corps de substitution est produit d'un bloc puis découpé selon le
        // gabarit d'origine : on connaît d'avance le nombre exact de caractères
        // nécessaires, ce qui évite une suite infinie.
        int[] lineLengths = new int[lines.Length - 2];
        for (int line = 1; line < lines.Length - 1; line++)
        {
            lineLengths[line - 1] = lines[line].Trim().Length;
        }

        string filler = CreateFiller(request, lineLengths.Sum());
        StringBuilder builder = new(original.Length);
        builder.Append(header).Append(lineEnding);

        int cursor = 0;
        foreach (int length in lineLengths)
        {
            builder.Append(indentation).Append(filler, cursor, length).Append(lineEnding);
            cursor += length;
        }

        builder.Append(footer);
        return builder.ToString();
    }

    /// <summary>
    /// Reconstruit une clé publique SSH de même longueur.
    /// </summary>
    /// <param name="original">Clé d'origine</param>
    /// <param name="request">Contexte de fabrication</param>
    /// <returns>La clé de substitution</returns>
    private static string CreateSshPublicKey(string original, PseudonymRequest request)
    {
        int separator = original.IndexOf(' ', StringComparison.Ordinal);
        if (separator < 0)
        {
            return request.Format.Compose(SpanCategory.KeyBlock.FamilyCode(), request.Index);
        }

        string algorithm = original[..separator];
        int bodyLength = original.Length - separator - 1;

        return string.Concat(algorithm, " ", CreateFiller(request, bodyLength));
    }

    /// <summary>
    /// Produit une suite de lettres déterministe et propre à cette attribution.
    /// Le numéro d'ordre est encodé en tête, ce qui garantit que deux clés
    /// distinctes reçoivent des corps distincts et donc que la restitution
    /// reste non ambiguë.
    /// </summary>
    /// <param name="request">Contexte de fabrication</param>
    /// <param name="length">Nombre de caractères à produire</param>
    /// <returns>Une suite de lettres de la longueur demandée</returns>
    private static string CreateFiller(PseudonymRequest request, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] body = new char[length];
        int written = 0;

        // Le chiffre de poids faible du numéro d'ordre est émis en premier afin
        // que deux attributions consécutives diffèrent dès le premier
        // caractère : un bloc au corps très court resterait sinon identique
        // d'une clé à l'autre, et la restitution deviendrait ambiguë.
        int remaining = request.Index;
        while (written < length && written < MarkerLength)
        {
            body[written++] = (char)('A' + (remaining % 26));
            remaining /= 26;
        }

        string seed = string.Create(CultureInfo.InvariantCulture, $"{request.Format.Nonce}|pem|{request.Index}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));

        int cursor = 0;
        while (written < length)
        {
            body[written++] = Alphabet[digest[cursor] % Alphabet.Length];
            cursor++;

            // Le condensat est rechaîné pour alimenter des corps plus longs que
            // ses trente-deux octets.
            if (cursor == digest.Length)
            {
                digest = SHA256.HashData(digest);
                cursor = 0;
            }
        }

        return new string(body);
    }
}
