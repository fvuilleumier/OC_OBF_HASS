using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Verdict du contrôle d'isolement.
/// </summary>
/// <param name="Blocking">Ce qui interdit tout échange : rien n'est écrit tant que cela dure</param>
/// <param name="Warnings">Ce qui mérite d'être su sans rien interdire</param>
public sealed record IsolationReport(IReadOnlyList<string> Blocking, IReadOnlyList<string> Warnings)
{
    /// <summary>Vrai si aucun échange n'est interdit.</summary>
    public bool IsSafe => Blocking.Count == 0;
}

/// <summary>
/// Vérifie que rien ne relie, sur le disque, le dossier où travaille l'IA au
/// dossier en clair ou au projet.
///
/// Le projet relie les deux dossiers par ses réglages, et lui seul. Une IA qui
/// travaille dans son dossier ne doit pouvoir atteindre aucun document en
/// clair : ni parce que son dossier serait dans le dossier en clair, ou
/// l'inverse ; ni par un lien symbolique ou une jonction ; ni par git, dont les
/// métadonnées peuvent renvoyer vers un autre dépôt — un dépôt distant qui est
/// le dossier en clair, des objets empruntés à son historique, un arbre de
/// travail qui en partage les objets. Un simple <c>git fetch</c> donnerait alors
/// à l'IA tout l'historique en clair.
///
/// Les métadonnées git sont lues comme du texte et jamais exécutées : lancer git
/// dans un dossier où l'IA écrit exécuterait les programmes que sa
/// configuration désigne, par l'application même qui tient le coffre. Le
/// contrôle, qui tourne de lui-même, ne le fait donc jamais ; seuls les boutons
/// « Commit » et « Push », à la demande de l'utilisateur, lancent git, comme il
/// le ferait dans la console.
/// </summary>
public static class IsolationGuard
{
    /// <summary>
    /// Mène le contrôle.
    /// </summary>
    /// <param name="projectRoot">Dossier du projet</param>
    /// <param name="clearFolder">Dossier en clair lié, ou <c>null</c></param>
    /// <param name="aiFolder">Dossier de l'IA lié, ou <c>null</c></param>
    /// <param name="aiLinks">Liens rencontrés en parcourant le dossier de l'IA, s'il a été parcouru</param>
    /// <returns>Le verdict</returns>
    public static IsolationReport Check(string projectRoot, string? clearFolder, string? aiFolder, IReadOnlyList<SkippedLink>? aiLinks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        List<string> blocking = [];
        List<string> warnings = [];

        Place project = new("le projet", projectRoot);
        Place? clear = PlaceOf("le dossier en clair", clearFolder, blocking);
        Place? ai = PlaceOf("le dossier de l'IA", aiFolder, blocking);

        CheckSeparated(project, clear, blocking);
        CheckSeparated(project, ai, blocking);
        CheckSeparated(clear, ai, blocking);

        if (ai is not null && Directory.Exists(ai.Path))
        {
            List<Place> protectedPlaces = [project];
            if (clear is not null)
            {
                protectedPlaces.Add(clear);
            }

            CheckLinks(ai, aiLinks ?? [], protectedPlaces, blocking, warnings);
            CheckGit(ai, protectedPlaces, blocking, warnings);
        }

        if (clear is not null && ai is not null && Directory.Exists(clear.Path))
        {
            CheckGitDirectory(clear, [ai], blocking);
        }

        return new IsolationReport(blocking, warnings);
    }

    /// <summary>
    /// Prépare un dossier lié au contrôle, s'il existe.
    /// </summary>
    /// <param name="label">Nom du dossier dans les messages</param>
    /// <param name="folder">Chemin du dossier, ou <c>null</c></param>
    /// <param name="blocking">Interdictions, complétées si le dossier est introuvable</param>
    /// <returns>Le dossier, ou <c>null</c> s'il n'est pas lié</returns>
    private static Place? PlaceOf(string label, string? folder, List<string> blocking)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        Place place = new(label, folder);
        if (!Directory.Exists(place.Path))
        {
            blocking.Add($"Dossier introuvable : {Capitalize(label)} « {place.Path} » n'existe pas ou n'est pas accessible.");
        }

        return place;
    }

    /// <summary>
    /// Vérifie que deux dossiers sont distincts et qu'aucun ne contient
    /// l'autre, après résolution des liens et, sous Windows, par identifiant
    /// de dossier — un lecteur substitué ou un chemin réseau désignant le même
    /// dossier ne trompent pas la comparaison.
    /// </summary>
    /// <param name="first">Premier dossier</param>
    /// <param name="second">Second dossier</param>
    /// <param name="blocking">Interdictions, complétées ici</param>
    private static void CheckSeparated(Place? first, Place? second, List<string> blocking)
    {
        if (first is null || second is null)
        {
            return;
        }

        if (first.SameAs(second))
        {
            blocking.Add($"{Capitalize(first.Label)} et {second.Label} sont un même dossier.");
            return;
        }

        if (first.Contains(second))
        {
            blocking.Add($"{Capitalize(second.Label)} se trouve dans {first.Label} : choisissez deux dossiers séparés.");
        }
        else if (second.Contains(first))
        {
            blocking.Add($"{Capitalize(first.Label)} se trouve dans {second.Label} : choisissez deux dossiers séparés.");
        }
    }

    /// <summary>
    /// Examine les liens trouvés dans le dossier de l'IA : un lien vers le
    /// dossier en clair ou vers le projet donnerait à l'IA accès à leur
    /// contenu ; un autre lien n'est que signalé, et n'est jamais suivi.
    /// </summary>
    /// <param name="ai">Dossier de l'IA</param>
    /// <param name="links">Liens rencontrés</param>
    /// <param name="protectedPlaces">Dossiers que l'IA ne doit pas atteindre</param>
    /// <param name="blocking">Interdictions, complétées ici</param>
    /// <param name="warnings">Avertissements, complétés ici</param>
    private static void CheckLinks(Place ai, IReadOnlyList<SkippedLink> links, List<Place> protectedPlaces, List<string> blocking, List<string> warnings)
    {
        foreach (SkippedLink link in links)
        {
            string? target = link.Target is null
                ? null
                : Path.GetFullPath(link.Target, Path.GetDirectoryName(RelativePaths.Resolve(ai.Path, link.RelativePath))!);

            Place? reached = target is null ? null : protectedPlaces.FirstOrDefault(place => place.ContainsPath(target));
            if (reached is not null)
            {
                blocking.Add($"Le lien « {link.RelativePath} » du dossier de l'IA mène dans {reached.Label} : supprimez-le, l'IA y lirait le clair.");
            }
            else
            {
                warnings.Add($"Le lien « {link.RelativePath} » du dossier de l'IA est ignoré : un échange ne suit jamais un lien.");
            }
        }
    }

    /// <summary>
    /// Examine les métadonnées git du dossier de l'IA, et le dépôt qui
    /// l'englobe éventuellement.
    /// </summary>
    /// <param name="ai">Dossier de l'IA</param>
    /// <param name="protectedPlaces">Dossiers que l'IA ne doit pas atteindre</param>
    /// <param name="blocking">Interdictions, complétées ici</param>
    /// <param name="warnings">Avertissements, complétés ici</param>
    private static void CheckGit(Place ai, List<Place> protectedPlaces, List<string> blocking, List<string> warnings)
    {
        bool ownRepository = CheckGitDirectory(ai, protectedPlaces, blocking);

        // Un fichier .gitmodules désigne d'autres dépôts, clonés dans celui-ci.
        string modules = Path.Combine(ai.Path, ".gitmodules");
        if (File.Exists(modules))
        {
            CheckReferences(ai, ReadValues(modules, "url"), ai.Path, protectedPlaces, "un sous-module", blocking);
        }

        // Un dépôt qui englobe le dossier de l'IA et le dossier en clair rend
        // l'historique du clair lisible depuis le dossier de l'IA.
        Place? enclosing = EnclosingRepository(ai);
        if (enclosing is null)
        {
            return;
        }

        Place? reached = protectedPlaces.FirstOrDefault(enclosing.Contains);
        if (reached is null)
        {
            return;
        }

        string message = $"Le dépôt git « {enclosing.Path} » englobe le dossier de l'IA et {reached.Label} : son historique y est lisible.";
        if (ownRepository)
        {
            warnings.Add(message + " Le dossier de l'IA a son propre dépôt, mais une IA qui remonte d'un dossier y accéderait.");
        }
        else
        {
            blocking.Add(message);
        }
    }

    /// <summary>
    /// Examine le dossier git d'un dossier : où il se trouve vraiment, ce
    /// qu'il emprunte, ce vers quoi pointent ses dépôts distants.
    /// </summary>
    /// <param name="place">Dossier examiné</param>
    /// <param name="protectedPlaces">Dossiers que ses métadonnées ne doivent pas désigner</param>
    /// <param name="blocking">Interdictions, complétées ici</param>
    /// <returns><c>true</c> si le dossier est la racine d'un dépôt git</returns>
    private static bool CheckGitDirectory(Place place, List<Place> protectedPlaces, List<string> blocking)
    {
        string dotGit = Path.Combine(place.Path, ".git");
        string? gitDirectory = null;

        if (Directory.Exists(dotGit))
        {
            gitDirectory = dotGit;
        }
        else if (File.Exists(dotGit))
        {
            // Un arbre de travail secondaire ou un sous-module : le fichier
            // .git désigne le vrai dossier git, qui peut être ailleurs.
            string? pointed = ReadValues(dotGit, "gitdir", separator: ':').FirstOrDefault();
            if (pointed is not null)
            {
                gitDirectory = Path.GetFullPath(pointed, place.Path);
                CheckReferences(place, [gitDirectory], place.Path, protectedPlaces, "son dossier git", blocking);
            }
        }

        if (gitDirectory is null || !Directory.Exists(gitDirectory))
        {
            return gitDirectory is not null;
        }

        // Un arbre de travail secondaire partage les objets du dépôt principal.
        string common = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(common))
        {
            CheckReferences(place, ReadLines(common), gitDirectory, protectedPlaces, "son dépôt principal", blocking);
        }

        string config = Path.Combine(gitDirectory, "config");
        if (File.Exists(config))
        {
            CheckReferences(place, ReadValues(config, "url"), place.Path, protectedPlaces, "un dépôt distant", blocking);
            CheckReferences(place, ReadValues(config, "pushurl"), place.Path, protectedPlaces, "un dépôt distant", blocking);
            CheckReferences(place, ReadValues(config, "insteadof"), place.Path, protectedPlaces, "une réécriture d'adresse", blocking);
            CheckReferences(place, ReadValues(config, "path"), gitDirectory, protectedPlaces, "une configuration incluse", blocking);
            CheckReferences(place, ReadValues(config, "worktree"), place.Path, protectedPlaces, "son arbre de travail", blocking);
        }

        // Des objets empruntés à un autre dépôt sont lisibles comme les siens.
        string alternates = Path.Combine(gitDirectory, "objects", "info", "alternates");
        if (File.Exists(alternates))
        {
            CheckReferences(place, ReadLines(alternates), Path.Combine(gitDirectory, "objects"), protectedPlaces, "des objets empruntés", blocking);
        }

        return true;
    }

    /// <summary>
    /// Vérifie qu'aucune valeur lue dans des métadonnées ne désigne un dossier
    /// protégé. Une adresse de dépôt distant n'est un chemin que si elle en a
    /// l'air : chemin absolu, adresse <c>file://</c>, ou chemin relatif.
    /// </summary>
    /// <param name="place">Dossier dont viennent les métadonnées</param>
    /// <param name="values">Valeurs lues</param>
    /// <param name="baseFolder">Dossier à partir duquel un chemin relatif se lit</param>
    /// <param name="protectedPlaces">Dossiers à ne pas désigner</param>
    /// <param name="what">Ce que désigne la valeur, pour le message</param>
    /// <param name="blocking">Interdictions, complétées ici</param>
    private static void CheckReferences(
        Place place,
        IEnumerable<string> values,
        string baseFolder,
        List<Place> protectedPlaces,
        string what,
        List<string> blocking)
    {
        foreach (string value in values)
        {
            string? path = AsLocalPath(value, baseFolder);
            Place? reached = path is null ? null : protectedPlaces.FirstOrDefault(other => other.ContainsPath(path) || new Place(string.Empty, path).Contains(other));
            if (reached is not null)
            {
                blocking.Add($"Les métadonnées git de {place.Label} désignent {reached.Label} ({what}) : l'historique en clair y serait lisible.");
            }
        }
    }

    /// <summary>
    /// Lit une adresse de dépôt comme un chemin local, si elle en est un.
    /// </summary>
    /// <param name="value">Valeur lue</param>
    /// <param name="baseFolder">Dossier à partir duquel un chemin relatif se lit</param>
    /// <returns>Le chemin absolu, ou <c>null</c> pour une adresse distante</returns>
    private static string? AsLocalPath(string value, string baseFolder)
    {
        string trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ? uri.LocalPath : trimmed[7..];
        }
        else if (trimmed.Contains("://", StringComparison.Ordinal) || (trimmed.Contains('@', StringComparison.Ordinal) && trimmed.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(trimmed)))
        {
            // https://, ssh://, git@hôte:chemin : un dépôt distant, pas un chemin.
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed, baseFolder);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cherche le premier dépôt git qui englobe un dossier, en remontant.
    /// </summary>
    /// <param name="place">Dossier de départ, exclu</param>
    /// <returns>La racine du dépôt englobant, ou <c>null</c></returns>
    private static Place? EnclosingRepository(Place place)
    {
        string? current = Path.GetDirectoryName(place.Path);
        while (current is not null)
        {
            string dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return new Place("un dépôt englobant", current);
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Lit les valeurs d'une clé dans un fichier de configuration git ou de
    /// sous-modules : les lignes <c>clé = valeur</c>, sans tenir compte de la
    /// casse de la clé.
    /// </summary>
    /// <param name="path">Fichier lu</param>
    /// <param name="key">Clé recherchée</param>
    /// <param name="separator">Séparateur entre clé et valeur</param>
    /// <returns>Les valeurs</returns>
    private static IEnumerable<string> ReadValues(string path, string key, char separator = '=')
    {
        foreach (string line in ReadLines(path))
        {
            int position = line.IndexOf(separator, StringComparison.Ordinal);
            if (position > 0 && string.Equals(line[..position].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                yield return line[(position + 1)..].Trim();
            }
        }
    }

    /// <summary>
    /// Lit les lignes utiles d'un petit fichier de métadonnées, sans les
    /// commentaires ni les lignes vides. Un fichier démesuré n'est pas lu.
    /// </summary>
    /// <param name="path">Fichier lu</param>
    /// <returns>Les lignes</returns>
    private static List<string> ReadLines(string path)
    {
        const long MaximumBytes = 1024 * 1024;
        FileInfo info = new(path);
        if (!info.Exists || info.Length > MaximumBytes)
        {
            return [];
        }

        try
        {
            return [.. File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Met une majuscule à un libellé.
    /// </summary>
    /// <param name="label">Libellé</param>
    /// <returns>Le libellé, initiale en majuscule</returns>
    private static string Capitalize(string label)
    {
        return label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>
    /// Dossier examiné : son chemin tel qu'écrit, son chemin résolu à travers
    /// les liens, et son identité sur le volume.
    /// </summary>
    private sealed class Place
    {
        private readonly string _resolved;

        /// <summary>
        /// Prépare un dossier.
        /// </summary>
        /// <param name="label">Nom du dossier dans les messages</param>
        /// <param name="path">Chemin du dossier</param>
        public Place(string label, string path)
        {
            Label = label;
            Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            _resolved = Resolve(Path);
        }

        /// <summary>Nom du dossier dans les messages.</summary>
        public string Label { get; }

        /// <summary>Chemin absolu du dossier.</summary>
        public string Path { get; }

        /// <summary>
        /// Indique si deux dossiers sont le même.
        /// </summary>
        /// <param name="other">Autre dossier</param>
        /// <returns><c>true</c> s'ils désignent le même dossier</returns>
        public bool SameAs(Place other)
        {
            return string.Equals(_resolved, other._resolved, StringComparison.OrdinalIgnoreCase)
                || (OperatingSystem.IsWindows() && FolderIdentity.Same(Path, other.Path));
        }

        /// <summary>
        /// Indique si ce dossier contient un autre dossier.
        /// </summary>
        /// <param name="other">Autre dossier</param>
        /// <returns><c>true</c> si l'autre dossier est à l'intérieur de celui-ci</returns>
        public bool Contains(Place other)
        {
            if (IsInside(other._resolved, _resolved))
            {
                return true;
            }

            // Par identité : un des dossiers parents de l'autre est celui-ci.
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string? parent = System.IO.Path.GetDirectoryName(other.Path);
            while (parent is not null)
            {
                if (FolderIdentity.Same(parent, Path))
                {
                    return true;
                }

                parent = System.IO.Path.GetDirectoryName(parent);
            }

            return false;
        }

        /// <summary>
        /// Indique si un chemin quelconque désigne ce dossier ou se trouve à
        /// l'intérieur.
        /// </summary>
        /// <param name="path">Chemin examiné</param>
        /// <returns><c>true</c> si le chemin est dans ce dossier</returns>
        public bool ContainsPath(string path)
        {
            string resolved = Resolve(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)));
            return string.Equals(resolved, _resolved, StringComparison.OrdinalIgnoreCase) || IsInside(resolved, _resolved);
        }

        /// <summary>
        /// Indique si un chemin se trouve sous un dossier, à une frontière de
        /// dossier près.
        /// </summary>
        /// <param name="inner">Chemin intérieur supposé</param>
        /// <param name="outer">Dossier extérieur supposé</param>
        /// <returns><c>true</c> si le premier est sous le second</returns>
        private static bool IsInside(string inner, string outer)
        {
            return inner.StartsWith(outer + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Résout un chemin à travers les liens symboliques et les jonctions de
        /// chacun de ses éléments.
        /// </summary>
        /// <param name="path">Chemin absolu</param>
        /// <returns>Le chemin résolu</returns>
        private static string Resolve(string path)
        {
            string? root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            string current = root;
            foreach (string segment in path[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, segment);
                try
                {
                    DirectoryInfo info = new(current);
                    if (info.Exists && info.LinkTarget is not null)
                    {
                        current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
                    }
                }
                catch (IOException)
                {
                    // Lien illisible : on garde le chemin tel qu'il est écrit.
                }
                catch (UnauthorizedAccessException)
                {
                    // Dossier inaccessible : idem.
                }
            }

            return System.IO.Path.TrimEndingDirectorySeparator(current);
        }
    }

    /// <summary>
    /// Identité d'un dossier sur son volume, sous Windows : le numéro de série
    /// du volume et le numéro du dossier. Deux chemins de même identité
    /// désignent le même dossier, quel que soit le chemin pris pour l'atteindre.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class FolderIdentity
    {
        private const uint FileShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;

        /// <summary>
        /// Indique si deux chemins désignent le même dossier.
        /// </summary>
        /// <param name="first">Premier chemin</param>
        /// <param name="second">Second chemin</param>
        /// <returns><c>true</c> si les deux identités sont connues et égales</returns>
        public static bool Same(string first, string second)
        {
            (uint Volume, ulong Index)? left = Of(first);
            (uint Volume, ulong Index)? right = Of(second);
            return left is not null && right is not null && left.Value == right.Value;
        }

        /// <summary>
        /// Lit l'identité d'un dossier.
        /// </summary>
        /// <param name="path">Chemin du dossier</param>
        /// <returns>L'identité, ou <c>null</c> si elle n'a pas pu être lue</returns>
        private static (uint Volume, ulong Index)? Of(string path)
        {
            using SafeFileHandle handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, BackupSemantics, 0);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out FileInformation information))
            {
                return null;
            }

            return (information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        /// <summary>Ouvre un fichier ou un dossier pour en lire les attributs.</summary>
        /// <param name="fileName">Chemin</param>
        /// <param name="desiredAccess">Accès demandé</param>
        /// <param name="shareMode">Partage accordé</param>
        /// <param name="securityAttributes">Attributs de sécurité</param>
        /// <param name="creationDisposition">Mode d'ouverture</param>
        /// <param name="flagsAndAttributes">Options</param>
        /// <param name="templateFile">Modèle</param>
        /// <returns>Le handle</returns>
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        /// <summary>Lit les informations d'un fichier ouvert.</summary>
        /// <param name="file">Handle du fichier</param>
        /// <param name="information">Informations lues</param>
        /// <returns><c>true</c> en cas de succès</returns>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        /// <summary>Informations d'un fichier, telles que Windows les donne.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
