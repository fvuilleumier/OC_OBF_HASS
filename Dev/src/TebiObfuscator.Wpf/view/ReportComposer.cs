using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Model.Reporting;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Wpf.View;

/// <summary>
/// Compose le rapport technique de l'onglet du même nom.
///
/// Ce rapport ne redit pas ce que l'aperçu montre déjà en couleur. Il décrit ce
/// que le moteur a fait et pourquoi : quelles règles étaient actives, quel
/// format et quel encodage ont été devinés, quelle règle a produit combien de
/// remplacements, quels arbitrages ont écarté quelles règles concurrentes, et
/// où en est le coffre. C'est ce qu'on lit quand une valeur n'a pas été masquée
/// et qu'on cherche à comprendre par quel chemin.
///
/// La couleur y sert à repérer, pas à décorer : les titres structurent, les
/// nombres ressortent, et une fuite se voit d'un coup d'œil au milieu de trois
/// écrans de texte.
///
/// Aucune valeur réelle n'y figure : le rapport est destiné à être copié,
/// enregistré ou transmis.
/// </summary>
internal static class ReportComposer
{
    private static readonly Dictionary<string, string> DetectorPurposes = new(StringComparer.Ordinal)
    {
        ["structure.sensitiveKey"] =
            "Analyse le document (JSON, JSONC, YAML) et masque la valeur de toute clé dont le nom désigne un secret, un compte, un port ou un identifiant — nom composé compris, comme wsPassword ou db_user, et toute clé nommée id —, sans juger la valeur elle-même.",
        ["secret"] =
            "Jetons reconnaissables à leur préfixe (AKIA, ghp_, JWT…), mots de passe Cisco de type 7, communautés SNMP, clés IPsec.",
        ["secret.key"] =
            "Valeur de toute clé dont le nom désigne un secret, un compte ou un port, en français, anglais ou allemand, nom composé compris (wsPassword, DB_PASSWORD, mot de passe, mfPort), et de toute clé nommée id : paires clé = valeur, variables d'environnement, notes, réglages XML <add key=… value=…/>, <setting name=…>, éléments <password>…</password>.",
        ["secret.pem"] =
            "Blocs encadrés BEGIN/END, traités d'un seul tenant pour ne jamais remplacer un bloc ligne par ligne.",
        ["identity"] =
            "Adresses de courrier, et identifiants normalisés validés par leur somme de contrôle : AVS, IBAN, IDE, cartes de paiement.",
        ["network"] =
            "Adresses IPv4 et IPv6 (numéros de version exclus), adresses MAC, ports non standard derrière une adresse ou dans une URL, chemins UNC et de profil, et composants identifiants des URL.",
        ["vocabulary"] =
            "Termes du vocabulaire du projet : noms de clients, de personnes, de sites et de machines, que rien ne distingue lexicalement d'un mot ordinaire. Un terme qui est une adresse ou un début d'adresse (192.168.1, 10.10) est traité comme une adresse.",
    };

    private static readonly Dictionary<SpanCategory, string> StrategyNotes = new()
    {
        [SpanCategory.IpV4] = "adresse privée → adresse privée de la même plage, réseau tiré au sort hors des réseaux réels du lot ; adresse publique → plage d'essai 198.18.0.0/15 ; sous-réseaux /24 (et /16 dans 10.x, 172.16–31.x) et partie hôte préservés ; un début d'adresse garde sa longueur",
        [SpanCategory.IpV6] = "plage de documentation 2001:db8::/32",
        [SpanCategory.MacAddress] = "bit « administrée localement » à 1, notation d'origine conservée",
        [SpanCategory.NetworkPort] = "port valide de la plage 41000+, hors des valeurs usuelles",
        [SpanCategory.Email] = "domaine réservé example.invalid",
        [SpanCategory.Fqdn] = "domaine réservé example.invalid",
        [SpanCategory.DomainName] = "domaine réservé example.invalid",
        [SpanCategory.GlobalIdentifier] = "identifiant de version 4 dérivé du nonce du projet",
        [SpanCategory.Identifier] = "nombre de la plage 900000001+ s'il était numérique, jeton neutre sinon",
        [SpanCategory.KeyBlock] = "enveloppe et gabarit de lignes conservés, corps remplacé",
    };

    /// <summary>
    /// Décrit la configuration du moteur, avant toute obfuscation.
    /// </summary>
    /// <param name="project">Projet courant</param>
    /// <param name="host">Élément servant à résoudre les couleurs du thème</param>
    /// <returns>Le document du rapport</returns>
    public static FlowDocument Configuration(ObfuscationProject project, FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(host);

        Palette palette = new(host);
        FlowDocument document = CreateDocument(palette);

        AppendEngine(document, project, palette);
        document.Blocks.Add(Line(palette.Muted, "Lancez une obfuscation pour obtenir le détail par fichier."));

        return document;
    }

    /// <summary>
    /// Décrit la configuration du moteur, puis une obfuscation écrite de la
    /// source vers la cible : ce que chaque fichier est devenu, et le détail du
    /// moteur par fichier. Les fichiers n'y sont nommés que par leur chemin
    /// dans la cible, pseudonymisé : un nom en clair peut porter une valeur
    /// réelle.
    /// </summary>
    /// <param name="project">Projet courant</param>
    /// <param name="plan">Plan confirmé</param>
    /// <param name="selected">Fichiers facultatifs cochés</param>
    /// <param name="outcome">Compte rendu de l'écriture</param>
    /// <param name="host">Élément servant à résoudre les couleurs du thème</param>
    /// <returns>Le document du rapport</returns>
    public static FlowDocument Publication(
        ObfuscationProject project,
        PublicationPlan plan,
        IReadOnlySet<int> selected,
        ExchangeOutcome outcome,
        FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(host);

        Palette palette = new(host);
        FlowDocument document = CreateDocument(palette);

        AppendEngine(document, project, palette);

        document.Blocks.Add(Heading("OBFUSCATION", palette));
        document.Blocks.Add(Field("Cible", plan.TargetRoot, palette));
        document.Blocks.Add(Field("Nature", plan.TargetIsLinked ? "dossier de l'IA lié : noms d'origine pseudonymisés, AGENTS.md à la racine" : "« out » du projet : fichiers _obfusc et légendes", palette));
        AppendOutcome(document, plan.Changes, outcome, palette);

        if (plan.Batch is { } batch)
        {
            int replacements = batch.Files.Sum(file => file.Content.Applied.Count);
            int knownInClear = batch.Files.Sum(file => file.KnownInClear.Sum(value => value.Occurrences));
            document.Blocks.Add(Field("Remplacements", replacements.ToString(CultureInfo.CurrentCulture), palette));
            document.Blocks.Add(Field("Coffre", string.Create(CultureInfo.CurrentCulture, $"{batch.Delta.NewEntries.Count} attribution(s) nouvelle(s), entrée(s) au coffre avant tout fichier"), palette));
            document.Blocks.Add(batch.IsClean
                ? Field("Vérification", "propre — aucune valeur masquée par vos règles ne subsiste, ni dans les contenus ni dans les noms", palette, palette.Success)
                : Field("Vérification", "EN ÉCHEC — des valeurs masquées par vos règles subsistent : rien n'a été écrit", palette, palette.Danger));

            if (knownInClear > 0)
            {
                document.Blocks.Add(Field(
                    "Avertissement",
                    string.Create(CultureInfo.CurrentCulture, $"{knownInClear} occurrence(s) de valeurs connues du coffre restent en clair : vos règles actuelles ne les couvrent plus"),
                    palette,
                    palette.Warning));
            }
        }

        document.Blocks.Add(Spacer());
        AppendChanges(document, plan.Changes, selected, [], outcome, isReturn: false, palette);

        if (plan.Batch is { } prepared)
        {
            // Le chemin affiché est celui de la cible : on le retrouve par le
            // chemin de la source, que le plan associe à chaque fichier.
            Dictionary<string, string> targets = plan.Changes
                .Where(change => change.SourcePath.Length > 0 && change.Action is not (ExchangeAction.Delete or ExchangeAction.Legacy))
                .GroupBy(change => change.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().TargetPath, StringComparer.OrdinalIgnoreCase);

            document.Blocks.Add(Heading("MOTEUR, FICHIER PAR FICHIER", palette));
            foreach (PreparedFile file in prepared.Files)
            {
                string shown = targets.TryGetValue(file.Item.RelativePath, out string? target) ? target : file.Name.RelativePath;
                AppendFile(document, file, shown, legend: !plan.TargetIsLinked && project.Settings.EmitLegend, palette);
            }
        }

        AppendVault(document, project, palette);
        return document;
    }

    /// <summary>
    /// Décrit un rapatriement écrit de la cible vers la source : ce que chaque
    /// fichier est devenu, fusions, conflits et alertes compris. Il cite les
    /// chemins du dossier en clair, et le dit : il n'est pas fait pour l'IA.
    /// </summary>
    /// <param name="project">Projet courant</param>
    /// <param name="plan">Plan confirmé</param>
    /// <param name="selected">Fichiers facultatifs cochés</param>
    /// <param name="ignored">Chemins de la cible ignorés désormais</param>
    /// <param name="outcome">Compte rendu de l'écriture</param>
    /// <param name="host">Élément servant à résoudre les couleurs du thème</param>
    /// <returns>Le document du rapport</returns>
    public static FlowDocument Return(
        ObfuscationProject project,
        ReturnPlan plan,
        IReadOnlySet<int> selected,
        IReadOnlyCollection<string> ignored,
        ExchangeOutcome outcome,
        FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(ignored);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(host);

        Palette palette = new(host);
        FlowDocument document = CreateDocument(palette);

        document.Blocks.Add(Heading("RAPATRIEMENT", palette));
        document.Blocks.Add(Line(palette.Warning, "Ce rapport cite les chemins du dossier en clair : ne le transmettez pas à l'IA."));
        document.Blocks.Add(Spacer());
        document.Blocks.Add(Field("Version", ProductVersion.Current, palette));
        document.Blocks.Add(Field("Projet", project.DisplayName, palette));
        document.Blocks.Add(Field("Source", plan.SourceRoot, palette));
        document.Blocks.Add(Field("Nature", plan.SourceIsLinked ? "dossier en clair lié : git y garde l'historique" : "« in » du projet : les versions remplacées sont à la corbeille", palette));
        document.Blocks.Add(Field("Depuis", plan.TargetRoot, palette));
        AppendOutcome(document, plan.Changes, outcome, palette);

        if (outcome.Conflicts > 0)
        {
            document.Blocks.Add(Field(
                "Conflits",
                string.Create(CultureInfo.CurrentCulture, $"{outcome.Conflicts} — entre « {ThreeWayMerge.OursMarker} » et « {ThreeWayMerge.TheirsMarker} » : à résoudre avant de commiter"),
                palette,
                palette.Danger));
        }

        document.Blocks.Add(Spacer());
        document.Blocks.Add(SubHeading("Méthode", palette));
        document.Blocks.Add(Line(palette.Muted, "  Chaque fichier est fusionné à trois : la base est ce que la cible avait reçu, restauré ; d'un côté le fichier"));
        document.Blocks.Add(Line(palette.Muted, "  en clair actuel, de l'autre le fichier de l'IA, restauré. Une ligne que l'IA n'a pas touchée revient du"));
        document.Blocks.Add(Line(palette.Muted, "  fichier en clair, octet pour octet : seules ses modifications apparaissent au contrôle git."));
        document.Blocks.Add(Spacer());

        AppendChanges(document, plan.Changes, selected, ignored, outcome, isReturn: true, palette);
        return document;
    }

    /// <summary>
    /// Ajoute les comptes d'un échange écrit.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <param name="outcome">Compte rendu de l'écriture</param>
    /// <param name="palette">Couleurs du thème</param>
    private static void AppendOutcome(FlowDocument document, IReadOnlyList<ExchangeChange> changes, ExchangeOutcome outcome, Palette palette)
    {
        document.Blocks.Add(Field("Date", DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture), palette));
        document.Blocks.Add(Field(
            "Écrits",
            string.Create(CultureInfo.CurrentCulture, $"{outcome.Created} créé(s), {outcome.Modified} modifié(s), {outcome.Deleted} retiré(s)"),
            palette,
            palette.Accent));

        int identical = changes.Count(change => change.Action == ExchangeAction.Identical);
        if (identical > 0)
        {
            document.Blocks.Add(Field("Déjà à jour", identical.ToString(CultureInfo.CurrentCulture), palette));
        }

        if (outcome.Warnings > 0)
        {
            document.Blocks.Add(Field("Alertes", outcome.Warnings.ToString(CultureInfo.CurrentCulture), palette, palette.Warning));
        }

        foreach (string skipped in outcome.Skipped)
        {
            document.Blocks.Add(Field("Laissé de côté", skipped, palette, palette.Warning));
        }
    }

    /// <summary>
    /// Ajoute la liste des fichiers d'un échange : ce que l'échange en a fait,
    /// ses comptes de lignes et ses alertes.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="changes">Fichiers de l'échange</param>
    /// <param name="selected">Fichiers facultatifs cochés</param>
    /// <param name="ignored">Chemins de la cible ignorés désormais</param>
    /// <param name="outcome">Compte rendu de l'écriture</param>
    /// <param name="isReturn">Vrai pour un rapatriement : le chemin écrit est celui de la source</param>
    /// <param name="palette">Couleurs du thème</param>
    private static void AppendChanges(
        FlowDocument document,
        IReadOnlyList<ExchangeChange> changes,
        IReadOnlySet<int> selected,
        IReadOnlyCollection<string> ignored,
        ExchangeOutcome outcome,
        bool isReturn,
        Palette palette)
    {
        document.Blocks.Add(SubHeading("Fichiers", palette));
        if (changes.Count == 0)
        {
            document.Blocks.Add(Line(palette.Muted, "  aucun"));
        }

        foreach (ExchangeChange change in changes.Where(change => change.Action != ExchangeAction.Identical))
        {
            ExchangeRowModel row = new(change, isReturn);

            // Une obfuscation ne cite que les chemins de la cible : un chemin en
            // clair peut porter une valeur réelle.
            string path = isReturn ? row.Path : change.TargetPath;

            bool taken = change.Optional ? selected.Contains(change.Id) : change.Checked;
            bool skipped = outcome.Skipped.Any(item => item.StartsWith(isReturn ? change.SourcePath + " :" : change.TargetPath + " :", StringComparison.Ordinal));
            // Une suppression de l'IA laissée de côté est une décision : le
            // fichier en clair reste, et repartira vers la cible.
            (string verdict, Brush brush) = (change.Action, taken, skipped) switch
            {
                (ExchangeAction.Refused, _, _) => ("refusé", palette.Danger),
                (_, _, true) => ("laissé de côté", palette.Warning),
                (ExchangeAction.Delete or ExchangeAction.Deleted or ExchangeAction.Legacy, true, _) => ("retiré", palette.Success),
                (_, true, _) => ("écrit", palette.Success),
                _ when ignored.Contains(change.TargetPath, StringComparer.OrdinalIgnoreCase) => ("ignoré désormais", palette.Muted),
                (ExchangeAction.Deleted, false, _) => ("gardé dans la source, repartira vers la cible", palette.Muted),
                _ => ("laissé tel quel", palette.Muted),
            };

            Paragraph line = new() { Margin = new Thickness(0) };
            line.Inlines.Add(new Run($"  {row.ActionLabel,-18}") { Foreground = palette.Accent });
            line.Inlines.Add(new Run(path) { Foreground = palette.Body });
            if (row.Stats.Length > 0)
            {
                line.Inlines.Add(new Run("   " + row.Stats) { Foreground = palette.Muted });
            }

            line.Inlines.Add(new Run("   " + verdict) { Foreground = brush });
            document.Blocks.Add(line);

            if (isReturn && row.HasOtherPath)
            {
                document.Blocks.Add(Line(palette.Muted, "      " + row.OtherPath));
            }

            if (row.HasNote)
            {
                document.Blocks.Add(Line(palette.Muted, "      " + row.Note));
            }

            foreach (ExchangeWarningLine warning in row.Warnings)
            {
                document.Blocks.Add(Line(warning.IsSerious ? palette.Danger : palette.Warning, "      " + warning.Text));
            }
        }

        document.Blocks.Add(Spacer());
    }

    /// <summary>
    /// Décrit les règles actives et la politique du projet.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="project">Projet courant</param>
    /// <param name="palette">Couleurs du thème</param>
    private static void AppendEngine(FlowDocument document, ObfuscationProject project, Palette palette)
    {
        document.Blocks.Add(Heading("MOTEUR", palette));
        document.Blocks.Add(Field("Version", ProductVersion.Current, palette));
        document.Blocks.Add(Field("Projet", project.DisplayName, palette));
        document.Blocks.Add(Field("Dossier", project.RootPath, palette));
        document.Blocks.Add(Field("Jetons", $"{project.Format.Prefix}{project.Format.Nonce}_FAMILLE_0000", palette));
        document.Blocks.Add(Field("Profil", project.Settings.Profile, palette));
        document.Blocks.Add(Field(
            "Adresses privées",
            project.Settings.PrivateIp == PrivateIpPolicy.Keep
                ? "conservées telles quelles (réglage privateIp : Keep)"
                : "remplacées par des adresses privées de la même plage",
            palette));
        document.Blocks.Add(Field("Vocabulaire", $"{project.Vocabulary.Terms.Count} terme(s)", palette));
        document.Blocks.Add(Field("Liste noire", $"{project.Settings.AlwaysObfuscate.Count} terme(s) — l'emporte sur la liste blanche", palette));
        document.Blocks.Add(Field("Liste blanche", $"{project.Settings.AllowKeep.Count + project.Settings.IgnoredSuggestions.Count} valeur(s) gardée(s) en clair partout", palette));
        document.Blocks.Add(Field("Exceptions", $"{project.Settings.LineExceptions.Count} occurrence(s) gardée(s) en clair à un endroit précis", palette));
        document.Blocks.Add(Field(
            "Catégories off",
            project.Settings.DisabledCategories.Count == 0 ? "aucune" : string.Join(", ", project.Settings.DisabledCategories),
            palette));
        document.Blocks.Add(Spacer());

        document.Blocks.Add(SubHeading("Phases, dans l'ordre", palette));
        AppendPhase(document, palette, "1. Gel", "Les pseudonymes déjà présents sont mis hors d'atteinte, ce qui rend l'obfuscation idempotente.");
        AppendPhase(document, palette, "2. Détection", "Chaque règle voit le texte d'origine, jamais une version partiellement substituée.");
        AppendPhase(document, palette, "3. Arbitrage", "Les spans qui se chevauchent sont départagés ; le perdant disparaît entier, jamais tronqué.");
        AppendPhase(document, palette, "4. Réécriture", "Une seule passe, sur des offsets calculés une fois pour toutes.");
        AppendPhase(document, palette, "5. Vérification", "Les valeurs réelles du coffre sont recherchées dans la sortie ; aucune ne doit y être.");
        document.Blocks.Add(Spacer());

        document.Blocks.Add(SubHeading("Règles actives", palette));
        foreach (string ruleId in project.CreateDetectors().Select(detector => detector.RuleId))
        {
            document.Blocks.Add(Line(palette.Accent, "  " + ruleId));
            document.Blocks.Add(Line(
                palette.Muted,
                "      " + (DetectorPurposes.TryGetValue(ruleId, out string? text) ? text : "règle sans description.")));
        }

        document.Blocks.Add(Spacer());
        document.Blocks.Add(SubHeading("Forme des pseudonymes", palette));
        foreach ((SpanCategory category, string note) in StrategyNotes)
        {
            document.Blocks.Add(Pair($"  {CategoryLabels.Label(category)}", note, palette, 34));
        }

        document.Blocks.Add(Pair("  Tout le reste", "jeton neutre marqué, pour que le modèle sache qu'il s'agit d'un substitut", palette, 34));
        document.Blocks.Add(Spacer());
    }

    /// <summary>
    /// Décrit le traitement d'un fichier par le moteur.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="shownPath">Chemin affiché : celui de la cible, pseudonymisé</param>
    /// <param name="legend">Vrai si le fichier part avec sa légende</param>
    /// <param name="palette">Couleurs du thème</param>
    private static void AppendFile(FlowDocument document, PreparedFile file, string shownPath, bool legend, Palette palette)
    {
        IReadOnlyList<DetectedSpan> applied = file.Content.Applied;
        IReadOnlyList<SpanEviction> evictions = file.Content.Evictions;

        document.Blocks.Add(SubHeading(shownPath, palette));

        Paragraph header = new() { Margin = new Thickness(0) };
        header.Inlines.Add(new Run("   format ") { Foreground = palette.Muted });
        header.Inlines.Add(new Run(file.Kind.ToString()) { Foreground = palette.Body });
        header.Inlines.Add(new Run("   encodage ") { Foreground = palette.Muted });
        header.Inlines.Add(new Run(file.Document.EncodingName) { Foreground = palette.Body });
        header.Inlines.Add(new Run("   remplacements ") { Foreground = palette.Muted });
        header.Inlines.Add(new Run(applied.Count.ToString(CultureInfo.CurrentCulture))
        {
            Foreground = palette.Accent,
            FontWeight = FontWeights.Bold,
        });
        document.Blocks.Add(header);

        foreach ((string rule, int count) in applied
            .GroupBy(span => span.RuleId)
            .ToDictionary(group => group.Key, group => group.Count())
            .OrderByDescending(entry => entry.Value))
        {
            Paragraph line = new() { Margin = new Thickness(0) };
            line.Inlines.Add(new Run($"     {count,4} × ") { Foreground = palette.Accent });
            line.Inlines.Add(new Run(rule) { Foreground = palette.Muted });
            document.Blocks.Add(line);
        }

        if (evictions.Count > 0)
        {
            document.Blocks.Add(Line(
                palette.Muted,
                $"   arbitrages : {evictions.Count} span(s) écarté(s) au profit d'une règle plus prioritaire ou plus englobante"));

            foreach (var group in evictions
                .GroupBy(eviction => (eviction.Loser.RuleId, eviction.Winner.RuleId, eviction.Reason))
                .OrderByDescending(group => group.Count())
                .Take(8))
            {
                document.Blocks.Add(Line(
                    palette.Muted,
                    $"     {group.Count(),4} × {group.Key.Item1} écarté par {group.Key.Item2} ({Explain(group.Key.Reason)})"));
            }
        }

        foreach (ResidualLiteral residual in file.Leaks)
        {
            document.Blocks.Add(Line(
                palette.Danger,
                $"   FUITE : {CategoryLabels.Label(residual.Category)} « {residual.MaskedLiteral} » ×{residual.Occurrences}{LinesOf(residual)} — le pseudonyme {residual.Pseudonym} aurait dû la remplacer"));
        }

        foreach (ResidualLiteral residual in file.NameLeaks)
        {
            document.Blocks.Add(Line(
                palette.Danger,
                $"   FUITE DANS LE NOM : {CategoryLabels.Label(residual.Category)} « {residual.MaskedLiteral} » — le pseudonyme {residual.Pseudonym} aurait dû la remplacer"));
        }

        foreach (ResidualLiteral known in file.KnownInClear)
        {
            document.Blocks.Add(Line(
                palette.Warning,
                $"   EN CLAIR : {CategoryLabels.Label(known.Category)} « {known.MaskedLiteral} » ×{known.Occurrences}{LinesOf(known)} — masquée autrefois ({known.Pseudonym}), elle n'est plus couverte par vos règles"));
        }

        if (file.KnownInClear.Count > 0)
        {
            document.Blocks.Add(Line(
                palette.Muted,
                "     Si c'est voulu, rien à faire. Sinon, masquez-la depuis l'aperçu, où elle apparaît en orange, ou rajoutez le terme aux règles."));
        }

        if (file.Suspicious.Count > 0)
        {
            // Le compte du moteur inclut ce que l'utilisateur a déjà tranché :
            // ce qui reste à trancher est cité, élément par élément, dans la
            // liste des fichiers.
            document.Blocks.Add(Line(
                palette.Muted,
                $"   signalés par le moteur : {file.Suspicious.Count} — ceux qui restent à trancher sont cités plus haut, dans « Fichiers », avec leur ligne"));
        }

        if (legend)
        {
            document.Blocks.Add(Line(palette.Muted, $"   légende : {Path.GetFileName(OutputNaming.LegendPath(shownPath))}"));
        }

        document.Blocks.Add(Spacer());
    }

    /// <summary>
    /// Décrit l'état du coffre après l'exécution.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="project">Projet courant</param>
    /// <param name="palette">Couleurs du thème</param>
    private static void AppendVault(FlowDocument document, ObfuscationProject project, Palette palette)
    {
        document.Blocks.Add(Heading("COFFRE", palette));
        document.Blocks.Add(Line(
            palette.Body,
            string.Create(
                CultureInfo.CurrentCulture,
                $"{project.Store.Entries.Count} attribution(s), en {VaultPresenter.Label(project.StoredProtection)} : {VaultPresenter.Reach(project.StoredProtection)}.")));

        foreach (var group in project.Store.Entries
            .GroupBy(entry => entry.Category)
            .OrderByDescending(group => group.Count()))
        {
            Paragraph line = new() { Margin = new Thickness(0) };
            line.Inlines.Add(new Run($"  {group.Count(),4} × ") { Foreground = palette.Accent });
            line.Inlines.Add(new Run(CategoryLabels.Label(group.Key)) { Foreground = palette.Body });
            document.Blocks.Add(line);
        }

        document.Blocks.Add(Spacer());
        document.Blocks.Add(Line(
            palette.Muted,
            "Une attribution est définitive tant que le coffre existe : la même valeur recevra"));
        document.Blocks.Add(Line(palette.Muted, "le même pseudonyme dans tous les fichiers, aujourd'hui et demain."));
    }

    /// <summary>
    /// Ajoute la description d'une phase du moteur.
    /// </summary>
    /// <param name="document">Document en cours de composition</param>
    /// <param name="palette">Couleurs du thème</param>
    /// <param name="name">Nom de la phase</param>
    /// <param name="purpose">Ce qu'elle garantit</param>
    private static void AppendPhase(FlowDocument document, Palette palette, string name, string purpose)
    {
        Paragraph line = new() { Margin = new Thickness(0) };
        line.Inlines.Add(new Run($"  {name,-16}") { Foreground = palette.Accent, FontWeight = FontWeights.Bold });
        line.Inlines.Add(new Run(purpose) { Foreground = palette.Muted });
        document.Blocks.Add(line);
    }

    /// <summary>
    /// Compose un titre de section.
    /// </summary>
    /// <param name="text">Libellé du titre</param>
    /// <param name="palette">Couleurs du thème</param>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph Heading(string text, Palette palette)
    {
        return new Paragraph(new Run($"══ {text} ══")
        {
            Foreground = palette.Accent,
            FontWeight = FontWeights.Bold,
        })
        {
            Margin = new Thickness(0, 6, 0, 6),
        };
    }

    /// <summary>
    /// Compose un sous-titre.
    /// </summary>
    /// <param name="text">Libellé du sous-titre</param>
    /// <param name="palette">Couleurs du thème</param>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph SubHeading(string text, Palette palette)
    {
        return new Paragraph(new Run($"── {text} ──")
        {
            Foreground = palette.Section,
            FontWeight = FontWeights.SemiBold,
        })
        {
            Margin = new Thickness(0, 4, 0, 2),
        };
    }

    /// <summary>
    /// Compose une ligne « libellé : valeur ».
    /// </summary>
    /// <param name="label">Libellé</param>
    /// <param name="value">Valeur</param>
    /// <param name="palette">Couleurs du thème</param>
    /// <param name="valueBrush">Couleur de la valeur, celle du texte par défaut</param>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph Field(string label, string value, Palette palette, Brush? valueBrush = null)
    {
        Paragraph paragraph = new() { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run($"{label,-18}") { Foreground = palette.Muted });
        paragraph.Inlines.Add(new Run(value) { Foreground = valueBrush ?? palette.Body });
        return paragraph;
    }

    /// <summary>
    /// Compose une ligne à deux colonnes alignées.
    /// </summary>
    /// <param name="left">Colonne de gauche</param>
    /// <param name="right">Colonne de droite</param>
    /// <param name="palette">Couleurs du thème</param>
    /// <param name="width">Largeur réservée à la colonne de gauche</param>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph Pair(string left, string right, Palette palette, int width)
    {
        Paragraph paragraph = new() { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run(left.PadRight(width)) { Foreground = palette.Body });
        paragraph.Inlines.Add(new Run(right) { Foreground = palette.Muted });
        return paragraph;
    }

    /// <summary>
    /// Compose une ligne simple.
    /// </summary>
    /// <param name="brush">Couleur du texte</param>
    /// <param name="text">Texte de la ligne</param>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph Line(Brush brush, string text)
    {
        return new Paragraph(new Run(text) { Foreground = brush }) { Margin = new Thickness(0) };
    }

    /// <summary>
    /// Compose une ligne vide de séparation.
    /// </summary>
    /// <returns>Le paragraphe correspondant</returns>
    private static Paragraph Spacer()
    {
        return new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
    }

    /// <summary>
    /// Crée le document et son gabarit typographique.
    /// </summary>
    /// <param name="palette">Couleurs du thème</param>
    /// <returns>Le document vide</returns>
    private static FlowDocument CreateDocument(Palette palette)
    {
        return new FlowDocument
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12.5,
            PagePadding = new Thickness(12, 10, 12, 16),
            Background = Brushes.Transparent,
            Foreground = palette.Body,
        };
    }

    /// <summary>
    /// Cite les lignes d'une valeur retrouvée, sans la valeur elle-même.
    /// </summary>
    /// <param name="residual">Valeur retrouvée</param>
    /// <returns>La mention des lignes, vide si elles sont inconnues</returns>
    private static string LinesOf(ResidualLiteral residual)
    {
        if (residual.Lines.Count == 0)
        {
            return string.Empty;
        }

        const int Shown = 8;
        string lines = string.Join(", ", residual.Lines.Take(Shown));
        if (residual.Lines.Count > Shown)
        {
            return $" (lignes {lines}…)";
        }

        return residual.Lines.Count > 1 ? $" (lignes {lines})" : $" (ligne {lines})";
    }

    /// <summary>
    /// Traduit une raison d'arbitrage en phrase lisible.
    /// </summary>
    /// <param name="reason">Raison technique</param>
    /// <returns>La formulation destinée au lecteur</returns>
    private static string Explain(EvictionReason reason)
    {
        return reason switch
        {
            EvictionReason.Contained => "span inclus dans un span plus large",
            EvictionReason.DuplicateRange => "même portion repérée deux fois",
            _ => "chevauchement partiel",
        };
    }

    /// <summary>
    /// Couleurs du rapport, résolues une fois depuis le thème.
    /// </summary>
    private sealed class Palette
    {
        /// <summary>
        /// Résout les couleurs depuis l'élément affiché.
        /// </summary>
        /// <param name="host">Élément servant de point de départ à la recherche</param>
        public Palette(FrameworkElement host)
        {
            Body = Resolve(host, "TextPrimary");
            Muted = Resolve(host, "TextMuted");
            Section = Resolve(host, "TextSecondary");
            Accent = Resolve(host, "Accent");
            Warning = Resolve(host, "Warning");
            Danger = Resolve(host, "Danger");
            Success = Resolve(host, "Success");
        }

        /// <summary>Texte courant.</summary>
        public Brush Body { get; }

        /// <summary>Texte secondaire et explications.</summary>
        public Brush Muted { get; }

        /// <summary>Sous-titres.</summary>
        public Brush Section { get; }

        /// <summary>Titres, nombres et identifiants de règle.</summary>
        public Brush Accent { get; }

        /// <summary>Signalements.</summary>
        public Brush Warning { get; }

        /// <summary>Fuites et échecs.</summary>
        public Brush Danger { get; }

        /// <summary>Vérification réussie.</summary>
        public Brush Success { get; }

        /// <summary>
        /// Résout un pinceau du thème.
        /// </summary>
        /// <param name="host">Élément servant de point de départ</param>
        /// <param name="key">Clé de la ressource</param>
        /// <returns>Le pinceau correspondant</returns>
        private static Brush Resolve(FrameworkElement host, string key)
        {
            return host.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
    }
}
