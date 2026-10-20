namespace Obfuscator.Core.Model.Documents;

/// <summary>
/// Nature d'un fichier d'entrée, telle que la devine l'outil.
/// </summary>
public enum FileKind
{
    /// <summary>Format non reconnu : seul le traitement textuel s'applique.</summary>
    PlainText,

    /// <summary>JSON, avec ou sans commentaires.</summary>
    Json,

    /// <summary>YAML.</summary>
    Yaml,

    /// <summary>XML, y compris les fichiers de configuration .NET.</summary>
    Xml,

    /// <summary>Configuration d'équipement Cisco IOS.</summary>
    Cisco,

    /// <summary>Configuration d'équipement Fortinet.</summary>
    Fortinet,

    /// <summary>Fichier de propriétés, INI ou variables d'environnement.</summary>
    KeyValue,

    /// <summary>Document Markdown.</summary>
    Markdown,
}

/// <summary>
/// Devine la nature d'un fichier à partir de son nom puis de son contenu.
///
/// La reconnaissance ne peut jamais échouer : tout fichier non reconnu est
/// traité comme du texte, pour lequel l'outil dispose déjà de règles complètes.
/// L'analyse structurelle n'est donc qu'une couche d'enrichissement, jamais un
/// prérequis — un export d'équipement, un journal ou une note manuscrite doivent
/// être traités aussi sérieusement qu'un JSON bien formé.
/// </summary>
public static class FormatSniffer
{
    private static readonly Dictionary<string, FileKind> KindsByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = FileKind.Json,
        [".jsonc"] = FileKind.Json,
        [".json5"] = FileKind.Json,
        [".yaml"] = FileKind.Yaml,
        [".yml"] = FileKind.Yaml,
        [".xml"] = FileKind.Xml,
        [".config"] = FileKind.Xml,
        [".csproj"] = FileKind.Xml,
        [".props"] = FileKind.Xml,
        [".md"] = FileKind.Markdown,
        [".markdown"] = FileKind.Markdown,
        [".ini"] = FileKind.KeyValue,
        [".env"] = FileKind.KeyValue,
        [".properties"] = FileKind.KeyValue,
        [".toml"] = FileKind.KeyValue,
        [".cfg"] = FileKind.KeyValue,
    };

    /// <summary>
    /// Détermine la nature d'un fichier.
    /// </summary>
    /// <param name="fileName">Nom du fichier, s'il est connu</param>
    /// <param name="text">Contenu du fichier</param>
    /// <returns>La nature retenue</returns>
    public static FileKind Detect(string? fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // L'extension prime quand elle est explicite : elle traduit l'intention
        // de celui qui a nommé le fichier.
        if (!string.IsNullOrEmpty(fileName)
            && KindsByExtension.TryGetValue(Path.GetExtension(fileName), out FileKind byExtension))
        {
            return byExtension;
        }

        string head = text.TrimStart();
        if (head.Length == 0)
        {
            return FileKind.PlainText;
        }

        if (head[0] is '{' or '[')
        {
            return FileKind.Json;
        }

        if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head[0] == '<')
        {
            return FileKind.Xml;
        }

        if (head.StartsWith("---", StringComparison.Ordinal))
        {
            return FileKind.Yaml;
        }

        // Les exports d'équipements se reconnaissent à leurs premières
        // directives, bien plus sûrement qu'à leur extension — un fichier
        // « running-config » n'en a souvent aucune.
        if (head.StartsWith("config system", StringComparison.OrdinalIgnoreCase)
            || head.Contains("\nconfig system", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Fortinet;
        }

        if (head.StartsWith('!') || head.Contains("\nhostname ", StringComparison.Ordinal))
        {
            return FileKind.Cisco;
        }

        return FileKind.PlainText;
    }
}
