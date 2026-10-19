using System.Text;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Écrit un fichier d'un seul geste : le contenu est d'abord écrit à côté,
/// sous un nom temporaire, puis mis à la place de l'ancien.
///
/// Un fichier à moitié écrit — coupure, disque plein, application fermée en
/// pleine écriture — est pire qu'un fichier absent : le coffre y perdrait ses
/// correspondances, un fichier de configuration sa fin. Avec un remplacement
/// d'un seul tenant, on lit toujours soit l'ancienne version, soit la nouvelle.
///
/// Le remplacement porte sur l'entrée du dossier elle-même : un lien posé à la
/// place du fichier est remplacé, jamais suivi, et ce qu'il désignait reste
/// intact. Un dossier lien sur le chemin, lui, serait traversé : c'est à
/// l'appelant de les écarter (voir <c>RelativePaths</c>).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Préfixe des fichiers temporaires. Les collectes de fichiers les
    /// écartent : un fichier en cours d'écriture n'est ni une entrée, ni un
    /// travail de l'IA.
    /// </summary>
    public const string TemporaryPrefix = ".~tebiobf-";

    /// <summary>
    /// Écrit des octets dans un fichier, en remplaçant d'un seul geste celui
    /// qui existe déjà.
    /// </summary>
    /// <param name="path">Chemin du fichier à écrire</param>
    /// <param name="content">Contenu complet du fichier</param>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"Chemin sans dossier : {path}", nameof(path));

        Directory.CreateDirectory(folder);

        // Le fichier temporaire est créé dans le même dossier : un déplacement
        // sur un même volume est un renommage, d'un seul tenant.
        string temporary = Path.Combine(folder, $"{TemporaryPrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            // Après un échec, le fichier temporaire n'est qu'un débris.
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Écrit un texte en UTF-8 sans marque d'ordre des octets, d'un seul geste.
    /// </summary>
    /// <param name="path">Chemin du fichier à écrire</param>
    /// <param name="text">Contenu complet du fichier</param>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    }

    /// <summary>
    /// Indique si un nom de fichier est celui d'un fichier temporaire en cours
    /// d'écriture.
    /// </summary>
    /// <param name="fileName">Nom du fichier, sans dossier</param>
    /// <returns><c>true</c> pour un fichier temporaire de l'outil</returns>
    public static bool IsTemporary(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.StartsWith(TemporaryPrefix, StringComparison.Ordinal);
    }
}
