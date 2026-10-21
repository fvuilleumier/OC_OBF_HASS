namespace MHassnage.Core.Model;

/// <summary>Nature d'un élément écrit dans Hass.</summary>
public enum ApplyKind
{
    /// <summary>Fichier du dossier /config (Samba).</summary>
    File,

    /// <summary>Dashboard en mode UI (lovelace/config/save).</summary>
    Dashboard,
}

/// <summary>Famille d'un fichier : décide du rechargement après écriture.</summary>
public enum ApplyCategory
{
    /// <summary>configuration.yaml, automations.yaml… : vérification + redémarrage de Hass.</summary>
    Config,

    /// <summary>Thèmes : frontend.reload_themes.</summary>
    Theme,

    /// <summary>Zigbee2MQTT : l'add-on doit être redémarré (non piloté).</summary>
    Z2m,

    Dashboard,
}

public enum ApplyStatus
{
    Identical,
    Modified,

    /// <summary>Absent de Hass : il sera créé.</summary>
    New,
}

/// <summary>Cible dans Hass : chemin relatif à /config (« / »), ou url_path du dashboard ("" = défaut).</summary>
public sealed record ApplyTarget(ApplyKind Kind, string Key)
{
    public override string ToString() => Kind == ApplyKind.File ? Key : $"dashboard « {(Key.Length == 0 ? "lovelace" : Key)} »";
}

/// <summary>Élément candidat : version du dépôt privé face à la version actuelle de Hass.</summary>
public sealed class ApplyItem
{
    public required ApplyTarget Target { get; set; }
    public required ApplyCategory Category { get; init; }

    /// <summary>Chemin relatif au dossier d'export du dépôt privé.</summary>
    public required string RepoPath { get; init; }

    /// <summary>Contenu du dépôt (fins de ligne « \n »).</summary>
    public required string RepoContent { get; init; }

    /// <summary>Contenu actuel dans Hass, même format que le dépôt ; null si absent.</summary>
    public string? HaContent { get; set; }

    /// <summary>Contenu au dernier commit du dépôt privé ; null si inconnu.</summary>
    public string? HeadContent { get; init; }

    /// <summary>Fichier du dépôt sans correspondance dans la configuration : nom côté Hass à confirmer.</summary>
    public bool IsUnmapped { get; init; }

    public ApplyStatus Status => HaContent is null ? ApplyStatus.New
        : SameContent(HaContent) ? ApplyStatus.Identical
        : ApplyStatus.Modified;

    /// <summary>Hass a changé depuis le dernier commit : l'écraser perdrait ces modifications.</summary>
    public bool ChangedInHa => HaContent is not null && HeadContent is not null && !SameContent(HaContent, HeadContent);

    /// <summary>
    /// Même contenu que le dépôt ? Fichier : texte identique (fins de ligne près).
    /// Dashboard : même configuration une fois le YAML relu (guillemets, style de bloc, retours
    /// à la ligne ne comptent pas : Hass ne stocke que du JSON).
    /// </summary>
    public bool SameContent(string? other) => other is not null && SameContent(other, RepoContent);

    private bool SameContent(string a, string b)
    {
        if (Normalize(a) == Normalize(b)) return true;
        if (Target.Kind != ApplyKind.Dashboard) return false;
        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(
                Service.YamlService.FromYaml(a), Service.YamlService.FromYaml(b));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
}

/// <summary>Contenu d'une sauvegarde prise juste avant une écriture dans Hass.</summary>
public sealed class ApplyBackupEntry
{
    public ApplyKind Kind { get; set; }
    public string Key { get; set; } = "";
    public ApplyCategory Category { get; set; }

    /// <summary>Faux : l'élément n'existait pas ; le restaurer = le supprimer.</summary>
    public bool Existed { get; set; }

    /// <summary>Fichier de la sauvegarde qui contient l'ancien contenu (fichier brut, ou config JSON du dashboard).</summary>
    public string File { get; set; } = "";
}

public sealed class ApplyBackupManifest
{
    public DateTimeOffset Created { get; set; }
    public string Label { get; set; } = "";
    public string HaVersion { get; set; } = "";
    public List<ApplyBackupEntry> Entries { get; set; } = [];
}

/// <summary>Sauvegarde listée dans l'interface.</summary>
public sealed record ApplyBackup(string Folder, ApplyBackupManifest Manifest)
{
    public string Text => $"{Manifest.Created.LocalDateTime:dd.MM.yyyy HH:mm:ss} · {Manifest.Label} · {Manifest.Entries.Count} élément(s)";
}

/// <summary>Résultat de la vérification de configuration de Hass.</summary>
public sealed record ConfigCheck(bool? Valid, string Errors);
