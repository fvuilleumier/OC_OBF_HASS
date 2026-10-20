using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Obfuscator.Core.Model.Exchange;

/// <summary>
/// Résultat d'une commande git.
/// </summary>
/// <param name="Success">Vrai si la commande a abouti</param>
/// <param name="Summary">Ce qui s'est passé, en une phrase</param>
/// <param name="Output">Sortie de git, telle qu'elle s'afficherait dans la console</param>
public sealed record GitResult(bool Success, string Summary, string Output)
{
    /// <summary>Vrai quand il n'y avait rien à faire : rien à commiter.</summary>
    public bool NothingToDo { get; init; }
}

/// <summary>Accord entre la branche courante et sa branche distante.</summary>
public enum GitSync
{
    /// <summary>Indéterminé : tête détachée, dépôt vide, arbre de travail lié.</summary>
    Unknown,

    /// <summary>La branche n'a pas de branche distante.</summary>
    NoUpstream,

    /// <summary>La branche distante n'a jamais reçu cette branche.</summary>
    NeverPushed,

    /// <summary>La branche et sa branche distante pointent sur le même commit.</summary>
    InSync,

    /// <summary>Elles diffèrent : des commits à pousser, ou à tirer.</summary>
    Differs,
}

/// <summary>
/// État d'un dépôt, lu dans ses fichiers.
/// </summary>
/// <param name="Branch">Branche courante, vide si elle est inconnue</param>
/// <param name="Upstream">Branche distante suivie (<c>origin/main</c>), ou <c>null</c></param>
/// <param name="Sync">Accord entre les deux</param>
public sealed record GitState(string Branch, string? Upstream, GitSync Sync);

/// <summary>
/// Commit et push d'un dossier lié, comme on les taperait dans la console de
/// ce dossier : avec la configuration git de l'utilisateur et son compte ssh,
/// sans que l'application gère le moindre identifiant.
///
/// Git est lancé directement, jamais par un interpréteur de commandes : le
/// message de commit est un argument, pas un bout de ligne de commande. Aucune
/// question ne peut être posée — il n'y a pas de console —, et une commande qui
/// attendrait une réponse est interrompue au bout d'un délai plutôt que de
/// figer l'application.
/// </summary>
public static class GitRepository
{
    /// <summary>Message de commit proposé par défaut.</summary>
    public const string DefaultMessage = "Document updated";

    /// <summary>Délai accordé à un commit.</summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Délai accordé à un push, réseau compris.</summary>
    private static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Trouve le dépôt git qui contient un dossier : le dossier lui-même ou
    /// l'un de ses parents, porteur d'un <c>.git</c> (dossier, ou fichier d'un
    /// arbre de travail).
    /// </summary>
    /// <param name="folder">Dossier examiné</param>
    /// <returns>La racine du dépôt, ou <c>null</c> s'il n'y en a pas</returns>
    public static string? RootOf(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        for (DirectoryInfo? current = new(Path.GetFullPath(folder)); current is not null; current = current.Parent)
        {
            string marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Lit l'état d'un dépôt dans ses fichiers — tête, références, branche
    /// suivie —, comme le contrôle d'isolement lit ses métadonnées : sans
    /// lancer git, qui pourrait exécuter ce que sa configuration désigne. Il se
    /// lit donc à chaque rafraîchissement de l'écran, sans risque ni attente.
    /// </summary>
    /// <param name="root">Racine du dépôt</param>
    /// <returns>L'état ; indéterminé si les fichiers ne le disent pas simplement</returns>
    public static GitState Describe(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        string git = Path.Combine(root, ".git");
        try
        {
            // Un arbre de travail lié (« .git » est un fichier) ou une tête
            // détachée ne se décrivent pas sans git.
            const string HeadPrefix = "ref: refs/heads/";
            string? head = Directory.Exists(git) ? FirstLine(Path.Combine(git, "HEAD")) : null;
            if (head is null || !head.StartsWith(HeadPrefix, StringComparison.Ordinal))
            {
                return new GitState(string.Empty, null, GitSync.Unknown);
            }

            string branch = head[HeadPrefix.Length..].Trim();
            string? local = Reference(git, "refs/heads/" + branch);
            (string? remote, string? merge) = TrackedBranch(git, branch);
            if (remote is null || merge is null || !merge.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                return new GitState(branch, null, local is null ? GitSync.Unknown : GitSync.NoUpstream);
            }

            string upstream = $"{remote}/{merge["refs/heads/".Length..]}";
            string? distant = Reference(git, "refs/remotes/" + upstream);
            GitSync sync = (local, distant) switch
            {
                (null, _) => GitSync.Unknown,
                (_, null) => GitSync.NeverPushed,
                _ when string.Equals(local, distant, StringComparison.OrdinalIgnoreCase) => GitSync.InSync,
                _ => GitSync.Differs,
            };

            return new GitState(branch, upstream, sync);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new GitState(string.Empty, null, GitSync.Unknown);
        }
    }

    /// <summary>
    /// Ajoute tout ce qui a changé dans le dossier, puis commite avec le
    /// message donné.
    /// </summary>
    /// <param name="folder">Dossier lié, dans un dépôt git</param>
    /// <param name="message">Message du commit ; le message par défaut s'il est vide</param>
    /// <returns>Le résultat</returns>
    public static GitResult Commit(string folder, string? message)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string text = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message.Trim();
        StringBuilder output = new();

        // « -- . » limite l'ajout au dossier lié : dans un dépôt plus large,
        // le reste de l'arbre n'est pas touché.
        (int addCode, string added) = Run(folder, CommitTimeout, "add", "-A", "--", ".");
        output.Append(added);
        if (addCode != 0)
        {
            return new GitResult(false, "git add a échoué : rien n'a été commité.", output.ToString());
        }

        // Rien d'indexé : git commit échouerait, sans que ce soit une erreur.
        (int diffCode, _) = Run(folder, CommitTimeout, "diff", "--cached", "--quiet");
        if (diffCode == 0)
        {
            return new GitResult(true, "Rien à commiter : le dépôt est déjà à jour.", output.ToString()) { NothingToDo = true };
        }

        (int commitCode, string committed) = Run(folder, CommitTimeout, "commit", "-m", text);
        output.Append(committed);
        return commitCode == 0
            ? new GitResult(true, $"Commit fait : « {text} ».", output.ToString())
            : new GitResult(false, "git commit a échoué.", output.ToString());
    }

    /// <summary>
    /// Pousse la branche courante vers son dépôt distant, avec le compte ssh
    /// de l'utilisateur.
    /// </summary>
    /// <param name="folder">Dossier lié, dans un dépôt git</param>
    /// <returns>Le résultat</returns>
    public static GitResult Push(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        (int code, string output) = Run(folder, PushTimeout, "push");
        return code == 0
            ? new GitResult(true, "Push fait.", output)
            : new GitResult(false, "git push a échoué.", output);
    }

    /// <summary>
    /// Lance git dans un dossier et attend sa fin, ou le délai.
    /// </summary>
    /// <param name="folder">Dossier de travail</param>
    /// <param name="timeout">Délai au-delà duquel git est interrompu</param>
    /// <param name="arguments">Arguments, passés un à un</param>
    /// <returns>Le code de sortie, et les sorties standard et d'erreur réunies</returns>
    private static (int Code, string Output) Run(string folder, TimeSpan timeout, params string[] arguments)
    {
        string? git = ResolveGit();
        if (git is null)
        {
            return (-1, "git introuvable : il doit être installé et accessible depuis la console (variable PATH).");
        }

        ProcessStartInfo start = new(git)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Pas de console : une question d'identifiants échoue au lieu
        // d'attendre une réponse qui ne viendra jamais.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        StringBuilder output = new();
        object gate = new();
        try
        {
            using Process process = new() { StartInfo = start };
            process.OutputDataReceived += (_, e) => Append(output, gate, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, gate, e.Data);
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                Append(output, gate, string.Create(CultureInfo.CurrentCulture, $"Interrompu au bout de {timeout.TotalSeconds:0} s : git attendait peut-être une réponse (phrase de passe ssh, hôte inconnu…). Essayez la même commande dans la console."));
                return (-1, output.ToString());
            }

            process.WaitForExit();
            return (process.ExitCode, output.ToString());
        }
        catch (Win32Exception exception)
        {
            return (-1, $"git introuvable : {exception.Message}. Git doit être installé et accessible depuis la console.");
        }
    }

    /// <summary>
    /// Lit l'empreinte d'une référence : son fichier, sinon sa ligne dans
    /// <c>packed-refs</c>.
    /// </summary>
    /// <param name="git">Dossier <c>.git</c></param>
    /// <param name="name">Nom complet de la référence (<c>refs/heads/main</c>)</param>
    /// <returns>L'empreinte, ou <c>null</c> si la référence n'existe pas</returns>
    private static string? Reference(string git, string name)
    {
        string loose = Path.Combine(git, name.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose))
        {
            return FirstLine(loose)?.Trim();
        }

        string packed = Path.Combine(git, "packed-refs");
        if (!File.Exists(packed))
        {
            return null;
        }

        foreach (string line in File.ReadLines(packed))
        {
            string[] parts = line.Split(' ', 2);
            if (parts.Length == 2 && string.Equals(parts[1].Trim(), name, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Lit, dans la configuration du dépôt, la branche distante que suit une
    /// branche : <c>[branch "main"]</c>, clés <c>remote</c> et <c>merge</c>.
    /// </summary>
    /// <param name="git">Dossier <c>.git</c></param>
    /// <param name="branch">Branche locale</param>
    /// <returns>Le dépôt distant et la référence suivie, ou <c>null</c></returns>
    private static (string? Remote, string? Merge) TrackedBranch(string git, string branch)
    {
        string config = Path.Combine(git, "config");
        if (!File.Exists(config))
        {
            return (null, null);
        }

        string section = $"[branch \"{branch}\"]";
        bool inside = false;
        string? remote = null;
        string? merge = null;

        foreach (string raw in File.ReadLines(config))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inside = string.Equals(line, section, StringComparison.Ordinal);
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inside || equals < 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (string.Equals(key, "remote", StringComparison.OrdinalIgnoreCase))
            {
                remote = value;
            }
            else if (string.Equals(key, "merge", StringComparison.OrdinalIgnoreCase))
            {
                merge = value;
            }
        }

        return (remote, merge);
    }

    /// <summary>
    /// Lit la première ligne d'un fichier.
    /// </summary>
    /// <param name="path">Fichier</param>
    /// <returns>La ligne, ou <c>null</c> si le fichier est absent ou vide</returns>
    private static string? FirstLine(string path)
    {
        return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() : null;
    }

    /// <summary>
    /// Trouve l'exécutable git dans les dossiers absolus de la variable PATH,
    /// comme la console. Un nom nu serait aussi cherché dans le dossier courant
    /// et celui de l'application : un <c>git.exe</c> déposé là — par exemple
    /// par une IA dans son dossier — serait exécuté à la place du vrai.
    /// </summary>
    /// <returns>Le chemin absolu de git, ou <c>null</c> s'il est introuvable</returns>
    private static string? ResolveGit()
    {
        string name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Ajoute une ligne de sortie, les deux flux pouvant écrire en même temps.
    /// </summary>
    /// <param name="output">Sortie réunie</param>
    /// <param name="gate">Verrou</param>
    /// <param name="line">Ligne reçue, ou <c>null</c> en fin de flux</param>
    private static void Append(StringBuilder output, object gate, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            output.AppendLine(line);
        }
    }
}
