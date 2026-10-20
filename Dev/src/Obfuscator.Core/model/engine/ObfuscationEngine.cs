using Obfuscator.Core.Model.Detectors;
using Obfuscator.Core.Model.Documents;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Engine;

/// <summary>
/// Résultat de l'obfuscation d'un document.
/// </summary>
/// <param name="Text">Texte obfusqué</param>
/// <param name="Applied">Spans effectivement remplacés</param>
/// <param name="Evictions">Arbitrages entre règles concurrentes</param>
/// <param name="Residuals">Valeurs réelles retrouvées malgré tout dans la sortie</param>
/// <param name="PseudonymsByStart">
/// Pseudonyme effectivement substitué, indexé par la position de début du span
/// remplacé. L'aperçu côte à côte en a besoin pour reconstituer le volet de
/// droite sans rejouer l'attribution, qui n'est pas idempotente vis-à-vis des
/// compteurs du coffre.
/// </param>
public sealed record ObfuscationResult(
    string Text,
    IReadOnlyList<DetectedSpan> Applied,
    IReadOnlyList<SpanEviction> Evictions,
    IReadOnlyList<ResidualLiteral> Residuals,
    IReadOnlyDictionary<int, string> PseudonymsByStart)
{
    /// <summary>
    /// Vrai si le contrôle de vérification n'a retrouvé aucune valeur réelle
    /// dans la sortie. Un résultat négatif doit bloquer l'envoi du fichier.
    /// </summary>
    public bool IsClean => Residuals.Count == 0;

    /// <summary>
    /// Place de chaque pseudonyme dans le texte produit, en regard de la valeur
    /// qu'il remplace dans le texte source, par position croissante.
    /// </summary>
    public IReadOnlyList<TextSubstitution> Substitutions { get; init; } = [];

    /// <summary>
    /// Ramène une position du texte produit à la position correspondante du
    /// texte source. Hors des pseudonymes, les deux textes sont identiques et
    /// ne diffèrent que d'un décalage ; dans un pseudonyme, la position est
    /// celle du début de la valeur remplacée.
    /// </summary>
    /// <param name="outputPosition">Position dans le texte produit</param>
    /// <returns>La position dans le texte source</returns>
    public int SourcePositionOf(int outputPosition)
    {
        int shift = 0;
        foreach (TextSubstitution substitution in Substitutions)
        {
            if (outputPosition < substitution.OutputStart)
            {
                break;
            }

            if (outputPosition < substitution.OutputStart + substitution.OutputLength)
            {
                return substitution.SourceStart;
            }

            shift = substitution.SourceStart + substitution.SourceLength - (substitution.OutputStart + substitution.OutputLength);
        }

        return outputPosition + shift;
    }
}

/// <summary>
/// Pseudonyme écrit dans le texte produit, en regard de la valeur remplacée.
/// </summary>
/// <param name="SourceStart">Début de la valeur dans le texte source</param>
/// <param name="SourceLength">Longueur de la valeur</param>
/// <param name="OutputStart">Début du pseudonyme dans le texte produit</param>
/// <param name="OutputLength">Longueur du pseudonyme</param>
public readonly record struct TextSubstitution(int SourceStart, int SourceLength, int OutputStart, int OutputLength);

/// <summary>
/// Assemble la chaîne de traitement d'un document : détection, réécriture, puis
/// contrôle de vérification.
///
/// L'ordre de ces phases est le cœur de la garantie offerte par l'outil et ne
/// doit pas être réarrangé : geler avant de détecter assure l'idempotence,
/// arbitrer avant de réécrire assure qu'aucune valeur n'est remplacée
/// partiellement, et vérifier après avoir réécrit rattrape les erreurs
/// d'offset qui auraient traversé tout le reste.
/// </summary>
public sealed class ObfuscationEngine
{
    private readonly MappingStore _store;
    private readonly PseudonymFactory _factory;
    private readonly DetectionPipeline _pipeline;
    private readonly DetectionPipeline _namePipeline;

    /// <summary>
    /// Construit le moteur pour un projet.
    /// </summary>
    /// <param name="format">Format des jetons du projet</param>
    /// <param name="store">Coffre du projet</param>
    /// <param name="factory">Fabrique de pseudonymes adossée au coffre</param>
    /// <param name="detectors">Règles de détection activées</param>
    /// <param name="policy">Politique d'obfuscation : catégories actives, listes blanche et noire</param>
    public ObfuscationEngine(
        TokenFormat format,
        MappingStore store,
        PseudonymFactory factory,
        IEnumerable<IDetector> detectors,
        ObfuscationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(detectors);

        List<IDetector> rules = [.. detectors];
        ObfuscationPolicy effective = policy ?? new ObfuscationPolicy();

        _store = store;
        _factory = factory;
        _pipeline = new DetectionPipeline(format, rules, effective);

        // Les noms de fichiers et de dossiers passent par les mêmes règles que
        // le contenu, moins celles qui supposent un document : un nom n'a ni
        // clés, ni structure, ni bloc PEM.
        _namePipeline = new DetectionPipeline(format, rules.Where(rule => rule.AppliesToNames), effective);
    }

    /// <summary>
    /// Obfusque un texte source.
    /// </summary>
    /// <param name="text">Contenu du fichier source</param>
    /// <param name="fileName">Nom du fichier, pour les rapports</param>
    /// <returns>Le texte obfusqué et le compte rendu de l'opération</returns>
    public ObfuscationResult Obfuscate(string text, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Verified(Rewrite(_pipeline, new DetectionContext(text, fileName), CancellationToken.None));
    }

    /// <summary>
    /// Obfusque un nom de fichier ou de dossier — un seul élément de chemin.
    ///
    /// Le nom est lu comme un texte brut, jamais comme un document : un nom
    /// qui commence par un crochet n'est pas du JSON. Les pseudonymes sont ceux
    /// du contenu, tirés du même coffre : <c>chambre_emma</c> devient
    /// <c>chambre_</c> suivi du pseudonyme d'Emma, et un renvoi qui cite le
    /// fichier dans un autre fichier est réécrit de la même façon.
    /// </summary>
    /// <param name="name">Nom à obfusquer, sans séparateur de dossier</param>
    /// <returns>Le nom obfusqué et le compte rendu de l'opération, vérification comprise</returns>
    public ObfuscationResult ObfuscateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Verified(Rewrite(_namePipeline, NameContext(name), CancellationToken.None));
    }

    /// <summary>
    /// Rejoue l'obfuscation d'un nom sans le contrôle final, pour l'aperçu.
    /// </summary>
    /// <param name="name">Nom à obfusquer, sans séparateur de dossier</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le nom pseudonymisé, sans résidus vérifiés</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public ObfuscationResult SimulateName(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Rewrite(_namePipeline, NameContext(name), cancellationToken);
    }

    /// <summary>
    /// Prépare le contexte d'analyse d'un nom : du texte brut, signalé comme
    /// nom pour que les règles propres aux contenus s'abstiennent.
    /// </summary>
    /// <param name="name">Nom analysé</param>
    /// <returns>Le contexte</returns>
    private static DetectionContext NameContext(string name)
    {
        return new DetectionContext(name) { Kind = FileKind.PlainText, IsName = true };
    }

    /// <summary>
    /// Complète un résultat par le contrôle final : les valeurs réelles du
    /// coffre retrouvées dans le texte produit.
    /// </summary>
    /// <param name="result">Résultat de la réécriture</param>
    /// <returns>Le résultat, résidus compris</returns>
    private ObfuscationResult Verified(ObfuscationResult result)
    {
        // Ce que l'outil vient d'écrire n'est pas une fuite, même s'il ressemble
        // à une valeur réelle : une adresse privée de substitution peut être,
        // par hasard, l'adresse réelle d'une autre machine du coffre.
        List<(int Start, int Length)> written = [.. result.Substitutions.Select(substitution => (substitution.OutputStart, substitution.OutputLength))];
        return result with { Residuals = MappingVerifier.FindResiduals(result.Text, _store, written) };
    }

    /// <summary>
    /// Fait connaître au moteur un texte du lot avant tout traitement : les
    /// pseudonymes attribués ensuite éviteront les valeurs réelles qu'il
    /// contient — un réseau privé de substitution ne doit pas tomber sur un
    /// réseau réel d'un autre fichier. À appeler pour chaque fichier du lot,
    /// dans le même ordre pour l'analyse et pour l'obfuscation.
    /// </summary>
    /// <param name="text">Contenu d'un fichier du lot</param>
    public void Observe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _factory.RegisterSource(text);
    }

    /// <summary>
    /// Rejoue le traitement d'un texte sans le contrôle de vérification final,
    /// pour l'aperçu.
    ///
    /// Le texte produit est exactement celui qu'écrirait l'obfuscation : les
    /// mêmes phases s'exécutent, dans le même ordre, avec le même coffre. Seule
    /// la vérification est omise. Elle ne modifie rien à la sortie — elle la
    /// contrôle — et son coût croît avec le produit de la taille du coffre par
    /// celle du texte : c'est l'étape la plus lente du moteur, et un aperçu n'a
    /// rien à faire de son verdict, puisque rien n'est encore écrit.
    ///
    /// À n'appeler que sur un moteur construit à partir d'un instantané du
    /// projet : les pseudonymes attribués ici sont inscrits dans son coffre.
    /// </summary>
    /// <param name="text">Contenu du fichier source</param>
    /// <param name="fileName">Nom du fichier, pour les rapports</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le texte pseudonymisé et le compte rendu, sans résidus vérifiés</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public ObfuscationResult Simulate(string text, string? fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Rewrite(_pipeline, new DetectionContext(text, fileName), cancellationToken);
    }

    /// <summary>
    /// Enchaîne gel, détection, arbitrage et réécriture.
    /// </summary>
    /// <param name="pipeline">Phases de détection à appliquer : celles des contenus ou celles des noms</param>
    /// <param name="context">Contexte d'analyse, texte source compris</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Le texte réécrit, sans résidus vérifiés</returns>
    private ObfuscationResult Rewrite(DetectionPipeline pipeline, DetectionContext context, CancellationToken cancellationToken)
    {
        string text = context.Text;

        // Les chaînes ayant déjà la forme d'un jeton sont retirées du stock
        // disponible avant toute attribution, sinon la désobfuscation
        // remplacerait une chaîne d'origine par une valeur sans rapport.
        _factory.RegisterSource(text);

        SpanResolution resolution = pipeline.Run(context, cancellationToken);
        IReadOnlyList<DetectedSpan> replacements = resolution.Replacements;

        // Le pseudonyme retenu est mémorisé au passage : le recalculer ensuite
        // consommerait de nouveaux numéros et donnerait un résultat différent
        // de celui qui a réellement été écrit.
        Dictionary<int, string> pseudonyms = [];
        List<TextSubstitution> substitutions = new(replacements.Count);
        int shift = 0;

        // Les spans arrivent dans l'ordre du texte : le décalage accumulé
        // donne la place de chaque pseudonyme dans le texte produit.
        string output = TextRewriter.Rewrite(text, replacements, span =>
        {
            string pseudonym = _factory.GetOrCreate(span);
            pseudonyms[span.Start] = pseudonym;
            substitutions.Add(new TextSubstitution(span.Start, span.Length, span.Start + shift, pseudonym.Length));
            shift += pseudonym.Length - span.Length;
            return pseudonym;
        });

        return new ObfuscationResult(output, replacements, resolution.Evictions, [], pseudonyms)
        {
            Substitutions = substitutions,
        };
    }
}
