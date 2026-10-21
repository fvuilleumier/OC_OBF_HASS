namespace MHassnage.Core.Model;

/// <summary>Point de contrôle affiché dans l'état (« Zigbee2MQTT 2.1 → 2.2 »).</summary>
public sealed record HealthItem(string Text, string? Detail = null, bool Serious = false);

/// <summary>Instantané de santé et de sécurité de Home Assistant (lecture seule).</summary>
public sealed class HassHealth
{
    public string Version { get; set; } = "";
    public int Entities { get; set; }
    public int Devices { get; set; }
    public int Areas { get; set; }
    public int Automations { get; set; }
    public List<(string Domain, int Count)> Domains { get; } = [];

    /// <summary>Mises à jour disponibles (entités update.* à « on »).</summary>
    public List<HealthItem> Updates { get; } = [];

    /// <summary>Problèmes signalés par HA (Paramètres → Réparations), hors ignorés.</summary>
    public List<HealthItem> Repairs { get; } = [];

    /// <summary>Notifications persistantes non lues.</summary>
    public List<HealthItem> Notifications { get; } = [];

    public List<HealthItem> LowBatteries { get; } = [];
    public List<HealthItem> Unavailable { get; } = [];
    public List<HealthItem> AutomationsOff { get; } = [];

    /// <summary>Points de sécurité : comptes admin, MFA, jetons, accès externe.</summary>
    public List<HealthItem> Security { get; } = [];

    /// <summary>Dernière sauvegarde HA réussie ; null si inconnue ou aucune.</summary>
    public DateTimeOffset? LastBackup { get; set; }
    public int BackupCount { get; set; }

    /// <summary>Commandes non disponibles sur cette version de HA (ignorées sans erreur).</summary>
    public List<string> Skipped { get; } = [];
}
