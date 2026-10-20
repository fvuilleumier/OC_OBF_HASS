using System.Text.Json;

namespace MHassnage.Core.Model;

/// <summary>Code de retour de obfcli publish / return / status.</summary>
public enum ObfuscatorExitCode
{
    Ok = 0,
    Error = 1,

    /// <summary>Fuite, refus, isolation non sûre, fichier bloqué.</summary>
    Blocked = 2,

    /// <summary>Éléments à trancher (publication).</summary>
    PendingDecisions = 3,

    /// <summary>Conflits ou choix requis (rapatriement).</summary>
    Conflicts = 4,
}

/// <summary>
/// Rapport JSON de obfcli (--json). Seuls les champs utiles à MHassnage sont lus ;
/// les valeurs sont déjà masquées et les chemins pseudonymisés par l'Obfuscator.
/// </summary>
public sealed class ObfuscatorReport
{
    public string Command { get; set; } = "";
    public bool DryRun { get; set; }
    public int ExitCode { get; set; }
    public bool Applied { get; set; }
    public string? Project { get; set; }
    public string? SourceFolder { get; set; }
    public string? TargetFolder { get; set; }
    public bool CanCommit { get; set; }
    public ObfuscatorIsolation Isolation { get; set; } = new();
    public Dictionary<string, int> Summary { get; set; } = [];
    public List<ObfuscatorChange> Changes { get; set; } = [];
    public List<ObfuscatorPending> Pending { get; set; } = [];
    public List<ObfuscatorConflict> Conflicts { get; set; } = [];
    public ObfuscatorOutcome? Outcome { get; set; }
    public List<string> Errors { get; set; } = [];
    public JsonElement? Status { get; set; }

    public ObfuscatorExitCode Code => Enum.IsDefined(typeof(ObfuscatorExitCode), ExitCode)
        ? (ObfuscatorExitCode)ExitCode
        : ObfuscatorExitCode.Error;

    /// <summary>Résumé sans zéros : « update 5 · create 2 ».</summary>
    public string SummaryText => string.Join(" · ",
        Summary.Where(s => s.Value > 0 && s.Key != "identical").Select(s => $"{s.Key} {s.Value}"));
}

public sealed class ObfuscatorIsolation
{
    public bool Safe { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ObfuscatorChange
{
    public string Action { get; set; } = "";
    public string Path { get; set; } = "";
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Conflicts { get; set; }
    public List<ObfuscatorWarning> Warnings { get; set; } = [];
}

public sealed class ObfuscatorWarning
{
    public string Kind { get; set; } = "";
    public bool Serious { get; set; }
    public int Line { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ObfuscatorPending
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public int Occurrences { get; set; }
    public string? Rule { get; set; }
}

public sealed class ObfuscatorConflict
{
    public string Path { get; set; } = "";
    public int Count { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ObfuscatorOutcome
{
    public int Created { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public List<string> Skipped { get; set; } = [];
}
