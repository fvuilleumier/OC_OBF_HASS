namespace MHassnage.HassExport.Cli;

/// <summary>
/// Sortie console colorée : le préfixe des lignes de journal (OK, ABSENT, ERREUR…)
/// choisit la couleur. Seul le préfixe est coloré, le reste de la ligne reste neutre.
/// </summary>
internal static class ColorConsole
{
    private static readonly (string Prefix, ConsoleColor Color)[] Prefixes =
    [
        ("OK", ConsoleColor.Green),
        ("ABSENT", ConsoleColor.Yellow),
        ("IGNORÉ", ConsoleColor.Yellow),
        ("ERREUR", ConsoleColor.Red),
    ];

    private static readonly Lock Sync = new();

    /// <summary>Ligne de journal : « OK  chemin » → [OK] en vert, etc.</summary>
    public static void Log(string line)
    {
        foreach (var (prefix, color) in Prefixes)
        {
            if (!line.StartsWith(prefix + " ", StringComparison.Ordinal)) continue;

            lock (Sync)
            {
                Write($"[{prefix}]", color);
                Console.WriteLine(" " + line[prefix.Length..].TrimStart());
            }
            return;
        }
        Console.WriteLine(line);
    }

    public static void Title(string text) => WriteLine(text, ConsoleColor.Cyan);
    public static void Info(string text) => WriteLine(text, ConsoleColor.DarkGray);
    public static void Success(string text) => WriteLine(text, ConsoleColor.Green);
    public static void Warning(string text) => WriteLine(text, ConsoleColor.Yellow);

    /// <summary>Erreur sur stderr, en rouge.</summary>
    public static void Error(string text)
    {
        lock (Sync)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(text);
            Console.ResetColor();
        }
    }

    public static void WriteLine(string text, ConsoleColor color)
    {
        lock (Sync)
        {
            Write(text, color);
            Console.WriteLine();
        }
    }

    private static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }
}
