using System.Security.Cryptography;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Contenus reçus par la cible au dernier échange, rangés sous leur empreinte.
///
/// Ce sont des contenus pseudonymisés — ceux-là mêmes que l'IA a reçus — et
/// ils ne révèlent donc rien. Ils servent de base à la fusion du
/// rapatriement : ce que l'IA a changé se lit par différence entre ce qu'elle
/// a reçu et ce qu'elle rend. Un contenu que plus aucun fichier n'attend est
/// supprimé.
/// </summary>
/// <param name="folder">Dossier des contenus, dans les données du projet</param>
public sealed class ContentStore(string folder)
{
    /// <summary>Longueur d'une empreinte, en caractères hexadécimaux.</summary>
    private const int HashLength = 64;

    /// <summary>
    /// Calcule l'empreinte d'un contenu.
    /// </summary>
    /// <param name="content">Octets du contenu</param>
    /// <returns>L'empreinte SHA-256, en hexadécimal</returns>
    public static string HashOf(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    /// <summary>
    /// Calcule l'empreinte d'un fichier.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <returns>L'empreinte SHA-256, en hexadécimal</returns>
    public static string HashOfFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Range un contenu sous son empreinte, s'il n'y est pas déjà.
    /// </summary>
    /// <param name="content">Octets du contenu</param>
    /// <returns>L'empreinte du contenu</returns>
    public string Put(byte[] content)
    {
        string hash = HashOf(content);
        string path = Path.Combine(folder, hash);
        if (!File.Exists(path))
        {
            AtomicFile.WriteAllBytes(path, content);
        }

        return hash;
    }

    /// <summary>
    /// Relit un contenu rangé.
    /// </summary>
    /// <param name="hash">Empreinte du contenu</param>
    /// <param name="content">Octets du contenu, s'il est trouvé et intact</param>
    /// <returns><c>true</c> si le contenu est disponible</returns>
    public bool TryGet(string hash, out byte[] content)
    {
        ArgumentNullException.ThrowIfNull(hash);

        content = [];
        if (!IsHash(hash))
        {
            return false;
        }

        string path = Path.Combine(folder, hash);
        if (!File.Exists(path))
        {
            return false;
        }

        // Un contenu altéré ne vaut rien comme base : mieux vaut le dire que
        // fusionner contre une base fausse.
        byte[] read = File.ReadAllBytes(path);
        if (!string.Equals(HashOf(read), hash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        content = read;
        return true;
    }

    /// <summary>
    /// Supprime les contenus que plus aucun fichier n'attend.
    /// </summary>
    /// <param name="referenced">Empreintes encore nécessaires</param>
    public void Collect(IEnumerable<string> referenced)
    {
        ArgumentNullException.ThrowIfNull(referenced);

        if (!Directory.Exists(folder))
        {
            return;
        }

        HashSet<string> live = new(referenced, StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(path);
            if (IsHash(name) && !live.Contains(name))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Indique si une chaîne a la forme d'une empreinte : c'est aussi ce qui
    /// garantit qu'elle ne désigne aucun autre fichier que ceux du magasin.
    /// </summary>
    /// <param name="value">Chaîne examinée</param>
    /// <returns><c>true</c> pour 64 caractères hexadécimaux</returns>
    private static bool IsHash(string value)
    {
        return value.Length == HashLength && value.All(char.IsAsciiHexDigit);
    }
}
