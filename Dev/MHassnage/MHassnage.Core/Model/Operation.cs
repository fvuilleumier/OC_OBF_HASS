namespace MHassnage.Core.Model;

/// <summary>Opérations proposées à l'accueil ; chacune a son panel.</summary>
public enum Operation
{
    /// <summary>Accueil : état des dépôts, du jeton, dernier export.</summary>
    Status,

    /// <summary>Modification faite dans Hass (web, smartphone) : export puis commit privé.</summary>
    BackupHass,

    /// <summary>Export, publication pseudonymisée vers le dépôt public.</summary>
    PrepareForAi,

    /// <summary>Rapatriement du travail de l'IA vers le dépôt privé.</summary>
    RetrieveAiWork,

    /// <summary>Écriture du dépôt privé dans Hass, après diff et validation.</summary>
    ApplyToHass,
}
