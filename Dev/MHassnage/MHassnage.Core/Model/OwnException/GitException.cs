namespace MHassnage.Core.Model.OwnException;

/// <summary>Une commande git a échoué.</summary>
public sealed class GitException(string command, GitResult result)
    : Exception($"git {command} a échoué ({result.ExitCode}) : {(result.Error.Length > 0 ? result.Error : result.Output).Trim()}")
{
    public GitResult Result { get; } = result;
}
