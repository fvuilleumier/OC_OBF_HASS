using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Fichier d'un lot : où le lire, sous quelle clé il est désigné par ses
/// exceptions ponctuelles, et sous quel chemin il est rangé dans la source.
/// </summary>
/// <param name="Path">Chemin absolu du fichier</param>
/// <param name="Key">Clé du fichier pour les exceptions ponctuelles (voir <see cref="LineExemption.KeyFor"/>)</param>
/// <param name="RelativePath">Chemin relatif à la source, avec des barres obliques ; le seul nom pour un fichier hors de la source</param>
public sealed record BatchItem(string Path, string Key, string RelativePath)
{
    /// <summary>
    /// Décrit un fichier d'un lot dont on connaît la source.
    /// </summary>
    /// <param name="path">Chemin du fichier</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <returns>Le fichier du lot</returns>
    public static BatchItem Of(string path, string? sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string key = LineExemption.KeyFor(path, sourceRoot);
        string relative = key.Length > 0 && key[0] == LineExemption.PathKeyMark
            ? key[1..]
            : System.IO.Path.GetFileName(path);

        return new BatchItem(System.IO.Path.GetFullPath(path), key, relative);
    }
}

/// <summary>
/// Chemin d'un fichier une fois réécrit.
/// </summary>
/// <param name="RelativePath">Chemin relatif réécrit, avec des barres obliques</param>
/// <param name="Segments">Éléments réécrits : le texte produit et le compte rendu de chacun</param>
/// <param name="Problem">Raison pour laquelle le chemin produit ne peut pas être écrit, ou <c>null</c></param>
public sealed record RewrittenPath(string RelativePath, IReadOnlyList<ObfuscationResult> Segments, string? Problem)
{
    /// <summary>Valeurs remplacées dans le chemin, tous éléments confondus.</summary>
    public IEnumerable<DetectedSpan> Applied => Segments.SelectMany(segment => segment.Applied);
}

/// <summary>
/// Fichier d'un lot une fois réécrit : son contenu et son chemin.
/// </summary>
/// <param name="Item">Fichier du lot</param>
/// <param name="Document">Document source, avec son encodage</param>
/// <param name="Content">Contenu réécrit</param>
/// <param name="Name">Chemin réécrit</param>
internal sealed record RewrittenFile(BatchItem Item, SourceDocument Document, ObfuscationResult Content, RewrittenPath Name);

/// <summary>
/// Boucle de réécriture d'un lot, partagée par l'analyse et l'obfuscation.
///
/// Les pseudonymes sont attribués dans l'ordre où les valeurs se présentent :
/// pour que l'aperçu montre exactement ce que l'obfuscation écrira, les deux
/// doivent rencontrer les mêmes valeurs dans le même ordre. Tout le lot est
/// donc d'abord lu et montré au moteur — contenus puis chemins —, puis chaque
/// fichier est réécrit, son contenu puis son chemin. Écrire cette boucle une
/// seule fois est la garantie qu'elles ne divergent pas.
/// </summary>
internal static class BatchRewriter
{
    /// <summary>
    /// Réécrit un lot.
    /// </summary>
    /// <param name="engine">Moteur lié au coffre — celui d'un instantané pour l'analyse</param>
    /// <param name="items">Fichiers du lot, dans l'ordre de traitement</param>
    /// <param name="simulate">Vrai pour l'aperçu : sans le contrôle final</param>
    /// <param name="report">Avancement : rang du fichier et étape</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Les fichiers réécrits, dans l'ordre du lot</returns>
    public static List<RewrittenFile> Run(
        ObfuscationEngine engine,
        IReadOnlyList<BatchItem> items,
        bool simulate,
        Action<int, string>? report,
        CancellationToken cancellationToken)
    {
        List<SourceDocument> documents = new(items.Count);

        // Tout le lot est lu et montré au moteur avant la première attribution :
        // un réseau privé de substitution tiré pour le premier fichier évite
        // ainsi les réseaux réels du dernier, noms compris.
        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(index, "lecture");

            SourceDocument document = SourceDocument.Load(File.ReadAllBytes(items[index].Path));
            documents.Add(document);
            engine.Observe(document.Text);
        }

        foreach (BatchItem item in items)
        {
            engine.Observe(item.RelativePath);
        }

        List<RewrittenFile> rewritten = new(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(index, "détection");

            BatchItem item = items[index];
            ObfuscationResult content = simulate
                ? engine.Simulate(documents[index].Text, item.Key, cancellationToken)
                : engine.Obfuscate(documents[index].Text, item.Key);

            rewritten.Add(new RewrittenFile(item, documents[index], content, RewriteName(engine, item.RelativePath, simulate, cancellationToken)));
        }

        return rewritten;
    }

    /// <summary>
    /// Réécrit un chemin élément par élément. D'un fichier, seul le radical
    /// est réécrit : son extension dit au système et à l'IA ce qu'il contient,
    /// et la garder telle quelle évite qu'une règle ne la prenne pour la fin
    /// d'un nom de domaine.
    /// </summary>
    /// <param name="engine">Moteur</param>
    /// <param name="relative">Chemin relatif, avec des barres obliques</param>
    /// <param name="simulate">Vrai pour l'aperçu</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le chemin réécrit</returns>
    private static RewrittenPath RewriteName(ObfuscationEngine engine, string relative, bool simulate, CancellationToken cancellationToken)
    {
        string[] segments = RelativePaths.Segments(relative);
        List<string> output = new(segments.Length);
        List<ObfuscationResult> results = [];

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            string extension = index == segments.Length - 1 ? System.IO.Path.GetExtension(segment) : string.Empty;
            string stem = segment[..^extension.Length];

            // « .env » n'a pas de radical : rien à réécrire.
            if (stem.Length == 0)
            {
                output.Add(segment);
                continue;
            }

            ObfuscationResult result = simulate ? engine.SimulateName(stem, cancellationToken) : engine.ObfuscateName(stem);
            results.Add(result);
            output.Add(result.Text + extension);
        }

        string path = string.Join('/', output);
        return new RewrittenPath(path, results, RelativePaths.Problem(path));
    }
}
