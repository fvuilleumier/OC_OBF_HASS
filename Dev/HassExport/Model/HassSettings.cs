namespace HassExport.Model;

public class HassSettings
{
    public string Url { get; set; } = "";
    public string OutputFolder { get; set; } = "Export";

    /// <summary>Sous-dossier (de OutputFolder) des .jsonc issus de hass</summary>
    public string ElementsFolder { get; set; } = "Elements";
    public string ProfileFolder { get; set; } = ".playwright-profile";
    public bool Headless { get; set; }

    /// <summary>Clés de document.querySelector('home-assistant').hass à exporter en .jsonc</summary>
    public List<string> Keys { get; set; } = [];

    /// <summary>Dossier config de HA via Samba, ex : \\192.168.x.x\config</summary>
    public string DefaultHassPath { get; set; } = "";

    /// <summary>Fichiers copiés à la racine de l'export</summary>
    public List<FileCopy> Elements { get; set; } = [];

    public FolderCopy Themes { get; set; } = new();
    public FolderCopy Z2m { get; set; } = new();
    public DashboardSettings Dashboards { get; set; } = new();
}

/// <summary>Source relative à DefaultHassPath (ou chemin absolu), Destination relative à OutputFolder.</summary>
public class FileCopy
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}

public class FolderCopy
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>Fichiers à copier ; vide = tout le dossier (récursif)</summary>
    public List<string> Files { get; set; } = [];
}

public class DashboardSettings
{
    public string Destination { get; set; } = "Dashboards";
    public List<DashboardItem> Items { get; set; } = [];
}

public class DashboardItem
{
    /// <summary>url_path du dashboard ; "lovelace" = dashboard par défaut</summary>
    public string UrlPath { get; set; } = "";
    public string FileName { get; set; } = "";
}
