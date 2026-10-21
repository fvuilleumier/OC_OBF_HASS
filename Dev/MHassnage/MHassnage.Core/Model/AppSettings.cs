namespace MHassnage.Core.Model;

/// <summary>Racine de la configuration (appsettings.json + user-secrets).</summary>
public sealed class AppSettings
{
    public HassSettings Hass { get; set; } = new();
    public RepositoriesSettings Repositories { get; set; } = new();
    public ObfuscatorSettings Obfuscator { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public HealthSettings Health { get; set; } = new();
}

/// <summary>Seuils du panel « État ».</summary>
public sealed class HealthSettings
{
    /// <summary>Batterie signalée en dessous de ce pourcentage.</summary>
    public int BatteryWarningPercent { get; set; } = 20;

    /// <summary>Batterie en rouge en dessous de ce pourcentage.</summary>
    public int BatteryCriticalPercent { get; set; } = 10;

    /// <summary>Sauvegarde HA signalée au-delà de ce nombre de jours.</summary>
    public int BackupMaxAgeDays { get; set; } = 7;

    /// <summary>Jeton longue durée inutilisé depuis plus de ce nombre de jours : signalé.</summary>
    public int StaleTokenDays { get; set; } = 180;

    /// <summary>Nombre d'administrateurs au-delà duquel on alerte.</summary>
    public int MaxAdmins { get; set; } = 2;
}

/// <summary>Accès à Home Assistant et contenu à exporter.</summary>
public sealed class HassSettings
{
    public string Url { get; set; } = "";

    /// <summary>Jeton longue durée : chiffré dans %APPDATA%\MHassnage\appsettings.json, en clair en mémoire seulement.</summary>
    public string Token { get; set; } = "";

    /// <summary>Partage Samba du dossier /config de HA.</summary>
    public string DefaultSambaHassPath { get; set; } = "";

    public List<string> Keys { get; set; } = [];
    public List<FileCopy> Elements { get; set; } = [];
    public FolderCopy Themes { get; set; } = new();
    public FolderCopy Z2m { get; set; } = new();
    public DashboardSettings Dashboards { get; set; } = new();

    /// <summary>
    /// « States » : volumineux et très changeants, exportés seulement sur demande.
    /// Clés d'éléments (Keys) et noms de fichiers (Themes/Z2m/Elements) concernés.
    /// </summary>
    public List<string> StateKeys { get; set; } = ["states"];
    public List<string> StateFiles { get; set; } = ["state.json"];

    /// <summary>
    /// Sauvegardes prises avant chaque « Appliquer dans HA » (version qui marchait),
    /// pour « Restaurer la version précédente ». Vide = %APPDATA%\MHassnage\HassBackups.
    /// </summary>
    public string ApplyBackupFolder { get; set; } = "";

    /// <summary>Nombre de sauvegardes d'application conservées (les plus anciennes sont supprimées).</summary>
    public int ApplyBackupKeep { get; set; } = 30;

    public string ApplyBackupPath => ApplyBackupFolder.Length > 0
        ? ApplyBackupFolder
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MHassnage", "HassBackups");
}

public sealed class FileCopy
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}

public sealed class FolderCopy
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>Vide : tout le dossier est copié récursivement.</summary>
    public List<string> Files { get; set; } = [];
}

public sealed class DashboardSettings
{
    public string Destination { get; set; } = "Dashboards";
    public List<DashboardItem> Items { get; set; } = [];
}

public sealed class DashboardItem
{
    /// <summary>Vide ou null : dashboard par défaut (lovelace).</summary>
    public string? UrlPath { get; set; }

    public string FileName { get; set; } = "";
}

public sealed class RepositoriesSettings
{
    /// <summary>Dépôt privé (OREE_DOUS_HASS) : valeurs réelles, jamais exposé à une IA.</summary>
    public RepositorySettings Private { get; set; } = new();

    /// <summary>Dépôt public (OC_OBF_HASS) : contenu pseudonymisé.</summary>
    public RepositorySettings Public { get; set; } = new();

    /// <summary>
    /// Projet de l'Obfuscator (OBF_HASS) vu comme dépôt git ; seul Excluded est lu ici,
    /// le chemin est Obfuscator:ProjectFolder.
    /// </summary>
    public RepositorySettings Obfuscator { get; set; } = new();

    /// <summary>« Private », « Public » ou « Obfuscator ».</summary>
    public RepositorySettings Get(string key) => key switch
    {
        "Public" => Public,
        "Obfuscator" => Obfuscator,
        _ => Private,
    };
}

public sealed class RepositorySettings
{
    public string Path { get; set; } = "";

    /// <summary>Sous-dossier du dépôt qui reçoit l'export (vide = racine).</summary>
    public string ExportFolder { get; set; } = "";

    /// <summary>Sous-dossier des éléments JSON (states, devices…), relatif à l'export.</summary>
    public string ElementsFolder { get; set; } = "Elements";

    public string ExportPath => System.IO.Path.Combine(Path, ExportFolder);

    /// <summary>
    /// Fichiers et dossiers masqués des listes de modifications (jamais commités par MHassnage).
    /// « Dev/ » = dossier ; « *.log » ou « Dev/*.md » = motif ; sinon chemin exact. Relatif à la racine du dépôt.
    /// </summary>
    public List<string> Excluded { get; set; } = [];
}

/// <summary>Correspondance chemin ↔ règle d'exclusion.</summary>
public static class Exclusions
{
    public static bool IsExcluded(string path, IEnumerable<string> rules) => rules.Any(rule => Matches(path, rule));

    public static bool Matches(string path, string rule)
    {
        rule = rule.Replace('\\', '/').Trim();
        if (rule.Length == 0) return false;
        if (rule.EndsWith('/')) return path.StartsWith(rule, StringComparison.OrdinalIgnoreCase);
        if (rule.Contains('*') || rule.Contains('?'))
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(rule, path, ignoreCase: true);
        return string.Equals(path, rule, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ObfuscatorSettings
{
    /// <summary>Chemin de obfcli.exe.</summary>
    public string CliPath { get; set; } = "";

    /// <summary>Chemin de TebiObfuscator.exe (revue, conflits avancés).</summary>
    public string WpfPath { get; set; } = "";

    /// <summary>Dossier du projet obfuscator (celui qui contient .tebiobf).</summary>
    public string ProjectFolder { get; set; } = "";

    /// <summary>Copie complète de ProjectFolder (hors git), refaite après chaque commit / push du rapatriement.</summary>
    public string BackupFolder { get; set; } = @"c:\tmp\Backups\ObfHass";
}

public sealed class GitSettings
{
    public string ExecutablePath { get; set; } = "git";
}
