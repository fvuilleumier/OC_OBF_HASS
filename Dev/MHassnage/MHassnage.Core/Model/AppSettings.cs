namespace MHassnage.Core.Model;

/// <summary>Racine de la configuration (appsettings.json + user-secrets).</summary>
public sealed class AppSettings
{
    public HassSettings Hass { get; set; } = new();
    public RepositoriesSettings Repositories { get; set; } = new();
    public ObfuscatorSettings Obfuscator { get; set; } = new();
    public GitSettings Git { get; set; } = new();
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
}

public sealed class RepositorySettings
{
    public string Path { get; set; } = "";

    /// <summary>Sous-dossier du dépôt qui reçoit l'export (vide = racine).</summary>
    public string ExportFolder { get; set; } = "";

    /// <summary>Sous-dossier des éléments JSON (states, devices…), relatif à l'export.</summary>
    public string ElementsFolder { get; set; } = "Elements";

    public string ExportPath => System.IO.Path.Combine(Path, ExportFolder);
}

public sealed class ObfuscatorSettings
{
    /// <summary>Chemin de tebiobf.exe.</summary>
    public string CliPath { get; set; } = "";

    /// <summary>Chemin de TebiObfuscator.exe (revue, conflits avancés).</summary>
    public string WpfPath { get; set; } = "";

    /// <summary>Dossier du projet obfuscator (celui qui contient .tebiobf).</summary>
    public string ProjectFolder { get; set; } = "";
}

public sealed class GitSettings
{
    public string ExecutablePath { get; set; } = "git";
}
