using System.Diagnostics;
using System.Text;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;

namespace MHassnage.Core.Service;

/// <summary>
/// Accès à un dépôt via le git en ligne de commande : la configuration de l'utilisateur
/// (identité, includeIf par dossier, gestionnaire d'identifiants, profil de push) est respectée.
/// </summary>
public sealed class GitService(string repositoryPath, string gitExecutable = "git", IProgress<string>? log = null)
{
    public string RepositoryPath { get; } = repositoryPath;

    // ---------------------------------------------------------------- Lecture

    public async Task<RepoState> GetStateAsync(CancellationToken ct = default)
    {
        string root = (await RunChecked(["rev-parse", "--show-toplevel"], ct: ct)).Output.Trim();

        var branchResult = await RunAsync(["symbolic-ref", "--short", "-q", "HEAD"], ct: ct);
        string branch = branchResult.Success ? branchResult.Output.Trim() : "(HEAD détachée)";

        var upstreamResult = await RunAsync(["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct: ct);
        string? upstream = upstreamResult.Success ? upstreamResult.Output.Trim() : null;

        int ahead = 0, behind = 0;
        if (upstream is not null)
        {
            var counts = await RunAsync(["rev-list", "--left-right", "--count", "HEAD...@{u}"], ct: ct);
            var parts = counts.Output.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (counts.Success && parts.Length == 2)
            {
                ahead = int.Parse(parts[0]);
                behind = int.Parse(parts[1]);
            }
        }

        var status = await RunChecked(["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct: ct);
        return new RepoState(root, branch, upstream, ahead, behind, ParseStatus(status.Output));
    }

    /// <summary>Contenu d'un fichier au dernier commit ; "" s'il n'existe pas dans HEAD.</summary>
    public async Task<string> ReadAtHeadAsync(string path, CancellationToken ct = default)
    {
        var result = await RunAsync(["show", $"HEAD:{path}"], ct: ct);
        return result.Success ? result.Output : "";
    }

    /// <summary>Contenu actuel dans la copie de travail ; "" si supprimé.</summary>
    public static string ReadWorking(string root, string path)
    {
        string full = Path.Combine(root, path);
        return File.Exists(full) ? File.ReadAllText(full) : "";
    }

    // ---------------------------------------------------------------- Écriture

    /// <summary>
    /// Commit des seuls fichiers donnés : l'index est d'abord vidé, puis ces fichiers
    /// (ajouts, modifications et suppressions) y sont placés.
    /// </summary>
    public async Task CommitAsync(IReadOnlyCollection<FileChange> files, string message, CancellationToken ct = default)
    {
        if (files.Count == 0) throw new InvalidOperationException("Aucun fichier sélectionné.");
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Message de commit vide.");

        // Index remis à HEAD (sans toucher aux fichiers) ; dépôt neuf sans HEAD : rien à faire
        if ((await RunAsync(["rev-parse", "--verify", "-q", "HEAD"], ct: ct)).Success)
            await RunChecked(["reset", "-q"], ct: ct);

        // Chemins relatifs à la racine du dépôt, pris littéralement (crochets, étoiles…)
        var paths = files.SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath });
        string pathspec = string.Concat(paths.Select(p => $":(top,literal){p}\0"));
        await RunChecked(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], pathspec, ct);

        await RunChecked(["commit", "-F", "-"], message, ct);
        Log($"OK  commit : {message.Split('\n')[0]} ({files.Count} fichier(s))");
    }

    public async Task PushAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        string[] args = state.Upstream is null ? ["push", "-u", "origin", state.Branch] : ["push"];
        await RunChecked(args, ct: ct);
        Log("OK  push");
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        await RunChecked(["pull", "--ff-only"], ct: ct);
        Log("OK  pull");
    }

    // ---------------------------------------------------------------- Processus

    public async Task<GitResult> RunAsync(IEnumerable<string> args, string? input = null, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(gitExecutable)
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // Chemins non échappés (accents) et sorties en UTF-8, quelle que soit la config locale
        foreach (string a in (string[])["-c", "core.quotepath=off", "-c", "i18n.logOutputEncoding=UTF-8"])
            info.ArgumentList.Add(a);
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"Impossible de lancer {gitExecutable}.");

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct);
        return new GitResult(process.ExitCode, await output, await error);
    }

    private async Task<GitResult> RunChecked(string[] args, string? input = null, CancellationToken ct = default)
    {
        var result = await RunAsync(args, input, ct);
        if (!result.Success) throw new GitException(args[0], result);
        return result;
    }

    /// <summary>« XY chemin\0 » ; renommage : « XY nouveau\0ancien\0 ».</summary>
    private static List<FileChange> ParseStatus(string output)
    {
        var changes = new List<FileChange>();
        string[] entries = output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4) continue;

            char x = entry[0], y = entry[1];
            string path = entry[3..];

            if (x == 'R' || x == 'C')
            {
                string old = i + 1 < entries.Length ? entries[++i] : "";
                changes.Add(new FileChange(path, ChangeKind.Renamed, old));
                continue;
            }

            var kind = (x, y) switch
            {
                ('?', '?') => ChangeKind.Untracked,
                ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => ChangeKind.Conflicted,
                ('D', _) or (_, 'D') => ChangeKind.Deleted,
                ('A', _) => ChangeKind.Added,
                _ => ChangeKind.Modified,
            };
            changes.Add(new FileChange(path, kind));
        }
        return changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Log(string message) => log?.Report(message);
}
