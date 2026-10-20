namespace Obfuscator.Core.Presenter;

/// <summary>Geste demandé au lancement de l'application graphique.</summary>
public enum LaunchGesture
{
    /// <summary>Aucun : le projet est seulement ouvert.</summary>
    None,

    /// <summary>Ouvrir la liste à confirmer de la publication.</summary>
    Publish,

    /// <summary>Ouvrir la liste à confirmer du rapatriement.</summary>
    Return,
}

/// <summary>
/// Demande faite à l'application graphique par ses arguments de lancement :
/// <c>Obfuscator.exe [dossierProjet] [--publish | --return]</c>. C'est ainsi
/// qu'une autre application lui passe la main quand un échange demande une
/// décision de l'utilisateur.
///
/// La même demande voyage vers une instance déjà ouverte : son chemin y est
/// donc absolu, résolu dans le dossier courant de celui qui l'a formulée.
/// </summary>
/// <param name="ProjectPath">Dossier du projet, absolu ; <c>null</c> sans projet</param>
/// <param name="Gesture">Geste à ouvrir une fois le projet ouvert</param>
/// <param name="Error">Ce qui rend les arguments inutilisables, ou <c>null</c></param>
public sealed record LaunchRequest(string? ProjectPath, LaunchGesture Gesture, string? Error)
{
    /// <summary>Option de la publication.</summary>
    public const string PublishOption = "--publish";

    /// <summary>Option du rapatriement.</summary>
    public const string ReturnOption = "--return";

    /// <summary>Demande vide : un lancement sans argument.</summary>
    public static readonly LaunchRequest Empty = new(null, LaunchGesture.None, null);

    /// <summary>Vrai pour un lancement sans argument, qui garde le comportement habituel.</summary>
    public bool IsEmpty => ProjectPath is null && Gesture == LaunchGesture.None && Error is null;

    /// <summary>
    /// Lit les arguments de lancement.
    /// </summary>
    /// <param name="args">Arguments, tels que le système les transmet</param>
    /// <param name="currentDirectory">Dossier où résoudre un chemin relatif</param>
    /// <returns>La demande ; son <see cref="Error"/> dit pourquoi elle est inutilisable</returns>
    public static LaunchRequest Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        string? path = null;
        LaunchGesture gesture = LaunchGesture.None;

        foreach (string argument in args.Where(argument => !string.IsNullOrWhiteSpace(argument)))
        {
            LaunchGesture? asked = argument.Trim().ToLowerInvariant() switch
            {
                PublishOption => LaunchGesture.Publish,
                ReturnOption => LaunchGesture.Return,
                _ => null,
            };

            if (asked is LaunchGesture option)
            {
                // Les deux gestes ne s'enchaînent pas : l'un ou l'autre.
                if (gesture != LaunchGesture.None && gesture != option)
                {
                    return Invalid($"Arguments contradictoires : « {PublishOption} » et « {ReturnOption} » ne se combinent pas.");
                }

                gesture = option;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                return Invalid($"Option inconnue : « {argument} ». Usage : Obfuscator.exe [dossierProjet] [{PublishOption} | {ReturnOption}].");
            }

            if (path is not null)
            {
                return Invalid("Un seul dossier de projet peut être donné au lancement.");
            }

            path = argument.Trim().Trim('"');
        }

        if (path is null && gesture == LaunchGesture.None)
        {
            return Empty;
        }

        if (path is null)
        {
            string option = gesture == LaunchGesture.Publish ? PublishOption : ReturnOption;
            return Invalid($"« {option} » demande le dossier du projet.");
        }

        // Le chemin est résolu ici : la demande peut partir vers une instance
        // déjà ouverte, dont le dossier courant n'est pas le même.
        try
        {
            return new LaunchRequest(Path.GetFullPath(path, currentDirectory), gesture, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid($"Chemin de projet invalide : « {path} ».");
        }
    }

    /// <summary>
    /// Compose une demande inutilisable.
    /// </summary>
    /// <param name="error">Ce qui la rend inutilisable</param>
    /// <returns>La demande</returns>
    private static LaunchRequest Invalid(string error)
    {
        return new LaunchRequest(null, LaunchGesture.None, error);
    }
}
