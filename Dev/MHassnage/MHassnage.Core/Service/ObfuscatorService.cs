using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MHassnage.Core.Model;

namespace MHassnage.Core.Service;

/// <summary>
/// Pilote l'Obfuscator sans en dépendre au niveau du code :
///   - obfcli publish|return|status --json [--dry-run] : « est-ce que tout passe ? », puis application ;
///   - Obfuscator.exe &lt;projet&gt; --publish|--return : l'utilisateur tranche dans la WPF.
/// MHassnage ne lit que le rapport JSON (valeurs masquées, chemins pseudonymisés).
/// </summary>
public sealed class ObfuscatorService(ObfuscatorSettings settings, IProgress<string>? log = null)
{
    private static readonly ObfuscatorJsonContext Json = new(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    public Task<ObfuscatorReport> PublishAsync(bool dryRun, CancellationToken ct = default) =>
        RunCliAsync("publish", dryRun, ct);

    public Task<ObfuscatorReport> ReturnAsync(bool dryRun, CancellationToken ct = default) =>
        RunCliAsync("return", dryRun, ct);

    public Task<ObfuscatorReport> StatusAsync(CancellationToken ct = default) =>
        RunCliAsync("status", dryRun: false, ct);

    /// <summary>Ouvre la WPF sur le projet et le dialogue voulu (instance unique côté Obfuscator).</summary>
    public void OpenWpf(ObfuscatorDialog dialog)
    {
        EnsureFile(settings.WpfPath, "Obfuscator:WpfPath");
        var info = new ProcessStartInfo(settings.WpfPath) { UseShellExecute = false };
        info.ArgumentList.Add(settings.ProjectFolder);
        if (dialog == ObfuscatorDialog.Publish) info.ArgumentList.Add("--publish");
        if (dialog == ObfuscatorDialog.Return) info.ArgumentList.Add("--return");
        Process.Start(info);
        Log($"Obfuscator ouvert ({dialog}) sur {settings.ProjectFolder}");
    }

    private async Task<ObfuscatorReport> RunCliAsync(string command, bool dryRun, CancellationToken ct)
    {
        EnsureFile(settings.CliPath, "Obfuscator:CliPath");

        var info = new ProcessStartInfo(settings.CliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(settings.ProjectFolder);
        info.ArgumentList.Add("--json");
        if (dryRun) info.ArgumentList.Add("--dry-run");

        Log($"obfcli {command}{(dryRun ? " --dry-run" : "")}");
        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"Impossible de lancer {settings.CliPath}.");

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string json = (await output).Trim();
        string stderr = (await error).Trim();

        ObfuscatorReport? report = null;
        try
        {
            if (json.Length > 0) report = JsonSerializer.Deserialize(json, Json.ObfuscatorReport);
        }
        catch (JsonException) { /* rapport illisible : traité ci-dessous */ }

        report ??= new ObfuscatorReport
        {
            Command = command,
            DryRun = dryRun,
            ExitCode = (int)ObfuscatorExitCode.Error,
            Errors = [stderr.Length > 0 ? stderr : $"Rapport JSON illisible (code {process.ExitCode})."],
        };
        // Le code du processus fait foi s'il diverge du rapport
        if (process.ExitCode != report.ExitCode) report.ExitCode = process.ExitCode;

        Log(Describe(report));
        foreach (string e in report.Errors) Log($"ERREUR  {e}");
        return report;
    }

    /// <summary>Ligne de journal « OK  publish : update 5 · create 2 », etc.</summary>
    public static string Describe(ObfuscatorReport r)
    {
        string what = $"{r.Command}{(r.DryRun ? " (simulation)" : "")}";
        string summary = r.SummaryText.Length > 0 ? r.SummaryText : "rien à faire";
        return r.Code switch
        {
            ObfuscatorExitCode.Ok => $"OK  {what} : {summary}{(r.Applied ? " — appliqué" : "")}",
            ObfuscatorExitCode.PendingDecisions => $"ABSENT  {what} : {r.Pending.Count} élément(s) à trancher dans l'Obfuscator",
            ObfuscatorExitCode.Conflicts => $"ABSENT  {what} : {r.Conflicts.Count} conflit(s) ou choix à faire dans l'Obfuscator",
            ObfuscatorExitCode.Blocked => $"ERREUR  {what} : bloqué (fuite, refus, isolation ou fichier modifié par l'IA)",
            _ => $"ERREUR  {what} : erreur technique",
        };
    }

    private static void EnsureFile(string path, string key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{key} introuvable : « {path} ». À corriger dans {SettingsService.SettingsPath}.");
    }

    private void Log(string message) => log?.Report(message);
}

public enum ObfuscatorDialog { None, Publish, Return }

/// <summary>Sérialisation générée à la compilation (aucune réflexion).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(ObfuscatorReport))]
internal sealed partial class ObfuscatorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
