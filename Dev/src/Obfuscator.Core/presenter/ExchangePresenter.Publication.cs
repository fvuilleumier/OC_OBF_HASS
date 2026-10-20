using System.Globalization;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Exchange;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Obfuscation de la source vers la cible.
/// </summary>
public sealed partial class ExchangePresenter
{
    /// <summary>
    /// Noms et dossiers qu'on n'envoie d'ordinaire pas à une IA : des secrets
    /// en bloc, des états internes, des bases, des journaux. Ils partent
    /// pseudonymisés comme le reste, mais la liste le signale.
    /// </summary>
    private static readonly string[] SensitiveNames = ["secrets.yaml", "secrets.yml", ".env", ".storage/", "known_devices.yaml"];

    /// <summary>Extensions des fichiers d'ordinaire tenus à l'écart d'une IA.</summary>
    private static readonly string[] SensitiveExtensions = [".log", ".db", ".sqlite", ".sqlite3", ".bak"];

    /// <summary>
    /// Nombre de valeurs signalées citées une à une par fichier : au-delà, la
    /// liste à confirmer renvoie à l'aperçu plutôt que de déborder.
    /// </summary>
    private const int SuspectsListed = 10;

    /// <summary>
    /// Prépare l'obfuscation de la source vers la cible : le lot est obfusqué
    /// et contrôlé en mémoire, sur l'instantané, puis comparé à la cible et au
    /// registre. Rien n'est écrit. Peut s'exécuter sur n'importe quel fil.
    /// </summary>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le plan, à confirmer</returns>
    public PublicationPlan PreparePublication(IProgress<BatchProgress>? progress)
    {
        // Les attributions sont faites dans l'instantané : un second plan sur
        // le même instantané n'aurait plus rien à faire adopter au coffre.
        if (Interlocked.Exchange(ref _prepared, 1) != 0)
        {
            throw new InvalidOperationException("Un présentateur d'échange ne prépare qu'un seul geste : créez-en un autre.");
        }

        ObfuscationProject snapshot = _snapshot;
        string sourceRoot = snapshot.SourcePath;
        string targetRoot = snapshot.TargetPath;
        bool linked = snapshot.IsTargetLinked;

        CollectionResult targetScan = CollectTarget(snapshot);
        IsolationReport isolation = IsolationGuard.Check(
            snapshot.RootPath,
            snapshot.IsSourceLinked ? sourceRoot : null,
            linked ? targetRoot : null,
            targetScan.Links);

        if (!Directory.Exists(sourceRoot) || (linked && !Directory.Exists(targetRoot)))
        {
            return new PublicationPlan(sourceRoot, targetRoot, linked, isolation, [], new Dictionary<int, PlannedWrite>(), null);
        }

        List<BatchItem> items = [.. CollectSource(snapshot).Files.Select(path => BatchItem.Of(path, sourceRoot))];
        PreparedBatch batch = new ObfuscatePresenter(snapshot).Prepare(items, progress);

        ExchangeRegister register = ExchangeRegister.Load(snapshot.ExchangeRegisterPath, snapshot.ExchangeProtector);
        ExchangeGroup? group = register.FindGroup(targetRoot, sourceRoot);
        ContentStore contents = new(snapshot.ExchangeContentPath);

        PlanBuilder<PlannedWrite> plan = new();
        Dictionary<string, string> targetsBySource = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> targetCounts = new(StringComparer.OrdinalIgnoreCase);

        foreach (PreparedFile file in batch.Files)
        {
            string target = TargetOf(file, linked);
            targetsBySource[file.Item.Key] = target;
            targetCounts[target] = targetCounts.GetValueOrDefault(target) + 1;
        }

        foreach (PreparedFile file in batch.Files)
        {
            PlanFile(plan, file, targetsBySource[file.Item.Key], targetCounts, group, contents, targetRoot);
        }

        PlanRemovals(plan, group, targetsBySource, contents, targetRoot, linked);

        if (linked)
        {
            PlanLegacy(plan, group, targetScan, targetsBySource.Values, targetRoot);
            PlanGuide(plan, batch, snapshot, targetsBySource, targetRoot);
        }

        return new PublicationPlan(sourceRoot, targetRoot, linked, isolation, plan.Changes, plan.Writes, batch);
    }

    /// <summary>
    /// Écrit une obfuscation préparée : les attributions entrent au coffre,
    /// puis chaque fichier retenu est écrit d'un seul geste, puis le registre.
    /// Un fichier de la cible modifié entre le plan et la confirmation est
    /// laissé de côté, et le compte rendu le dit.
    /// </summary>
    /// <param name="plan">Plan confirmé</param>
    /// <param name="selected">Identifiants des fichiers facultatifs que l'utilisateur a cochés</param>
    /// <param name="recycler">Retrait des fichiers, à la corbeille</param>
    /// <returns>Le compte rendu</returns>
    /// <exception cref="InvalidOperationException">Si le plan ne peut plus être écrit</exception>
    public ExchangeOutcome CommitPublication(PublicationPlan plan, IReadOnlySet<int> selected, IFileRecycler recycler)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(recycler);

        if (!plan.CanCommit || plan.Batch is null)
        {
            throw new InvalidOperationException("Cette obfuscation ne peut pas être écrite : une fuite ou un défaut d'isolement l'interdit.");
        }

        // L'isolement est revérifié au dernier moment : un lien a pu être posé
        // dans le dossier de l'IA pendant que la liste était affichée.
        IsolationReport isolation = CheckIsolation(_project);
        if (!isolation.IsSafe)
        {
            throw new InvalidOperationException(isolation.Blocking[0]);
        }

        // Les attributions de l'instantané entrent au coffre avant tout
        // fichier : une sortie dont les pseudonymes manqueraient au coffre ne
        // pourrait jamais être restituée.
        (_, int conflicts) = new VaultPresenter(_project).Adopt(plan.Batch.Delta);
        if (conflicts > 0)
        {
            throw new InvalidOperationException("Le coffre a changé depuis la préparation de l'obfuscation : relancez-la.");
        }

        ExchangeRegister register = ExchangeRegister.Load(_project.ExchangeRegisterPath, _project.ExchangeProtector);
        ExchangeGroup group = register.GroupFor(plan.TargetRoot, plan.SourceRoot);
        ContentStore contents = new(_project.ExchangeContentPath);
        Tally tally = new();

        foreach (ExchangeChange change in plan.Changes)
        {
            if (!plan.Writes.TryGetValue(change.Id, out PlannedWrite? write) || !(change.Optional ? selected.Contains(change.Id) : change.Checked))
            {
                continue;
            }

            string full = RelativePaths.Resolve(plan.TargetRoot, write.TargetRelative);
            if (!StillAsPlanned(full, write.ObservedHash, plan.TargetRoot))
            {
                tally.Skipped.Add($"{write.TargetRelative} : modifié pendant la confirmation, laissé tel quel.");
                continue;
            }

            Apply(write, full, group, contents, recycler, tally);
        }

        register.Record(new ExchangeEvent
        {
            At = DateTimeOffset.Now,
            Gesture = ExchangeGesture.Obfuscation,
            Folder = FolderLabel(plan.TargetRoot),
            Created = tally.Created,
            Modified = tally.Modified,
            Deleted = tally.Deleted,
            Warnings = plan.Changes.Sum(change => change.Warnings.Count),
        });

        register.Save(_project.ExchangeRegisterPath, _project.ExchangeProtector);
        contents.Collect(register.ReferencedContents());

        return new ExchangeOutcome(tally.Created, tally.Modified, tally.Deleted, 0, plan.Changes.Sum(change => change.Warnings.Count), tally.Skipped);
    }

    /// <summary>
    /// Donne le chemin d'un fichier dans la cible : son chemin pseudonymisé
    /// dans le dossier de l'IA, suffixé « _obfusc » dans « out ».
    /// </summary>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="linked">Vrai si la cible est le dossier de l'IA</param>
    /// <returns>Le chemin relatif dans la cible</returns>
    private static string TargetOf(PreparedFile file, bool linked)
    {
        return linked ? file.Name.RelativePath : ObfuscatePresenter.OutputRelativePath(file.Name.RelativePath);
    }

    /// <summary>
    /// Range un fichier obfusqué dans le plan : créé, mis à jour, identique,
    /// bloqué ou refusé, avec ses alertes.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="target">Chemin du fichier dans la cible</param>
    /// <param name="targetCounts">Nombre de fichiers visant chaque chemin de la cible</param>
    /// <param name="group">Fichiers du dernier échange, ou <c>null</c></param>
    /// <param name="contents">Contenus reçus par la cible</param>
    /// <param name="targetRoot">Dossier cible</param>
    private void PlanFile(
        PlanBuilder<PlannedWrite> plan,
        PreparedFile file,
        string target,
        Dictionary<string, int> targetCounts,
        ExchangeGroup? group,
        ContentStore contents,
        string targetRoot)
    {
        List<ExchangeWarning> warnings = PublicationWarnings(file);
        string source = file.Item.RelativePath;
        string? refusal = RefusalOf(file, target, targetCounts);

        string full = RelativePaths.Resolve(targetRoot, target);
        string? link = refusal is null ? RelativePaths.LinkOnPath(targetRoot, full) : null;
        if (link is not null)
        {
            refusal = $"un lien se trouve sur le chemin (« {RelativePaths.Of(targetRoot, link)} ») : un échange ne passe jamais par un lien";
        }

        if (refusal is not null)
        {
            plan.Add(ExchangeAction.Refused, target, source, @checked: false, optional: false, warnings, refusal, write: null);
            return;
        }

        byte[] content = file.OutputBytes();
        string sourceHash = HashOf(file.Item.Path);
        ExchangeEntry? entry = group?.BySource(file.Item.Key);
        string? legend = _snapshot.IsTargetLinked || !_snapshot.Settings.EmitLegend
            ? null
            : LegendWriter.Create(_snapshot, file.Content.Applied, Path.GetFileName(full));

        if (!File.Exists(full))
        {
            // L'IA a supprimé ce fichier depuis le dernier échange : c'est un
            // travail pas encore rapatrié, qu'on ne défait que sur demande. Dans
            // « out », que l'utilisateur nettoie lui-même, il est recréé.
            if (_snapshot.IsTargetLinked && entry is not null && string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(ExchangeAction.Blocked, target, source, @checked: false, optional: true, warnings,
                    "supprimé dans la cible depuis le dernier échange : rapatriez d'abord, ou cochez pour le recréer",
                    new PlannedWrite(ExchangeAction.Create, target, content, null, file.Item.Key, sourceHash) { Legend = legend });
                return;
            }

            string? renamed = entry is not null && !string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase)
                ? $"nom changé par les règles (ancien : {entry.Target})"
                : null;

            plan.Add(ExchangeAction.Create, target, source, @checked: true, optional: false, warnings, renamed,
                new PlannedWrite(ExchangeAction.Create, target, content, null, file.Item.Key, sourceHash) { Legend = legend });
            return;
        }

        byte[] existing = File.ReadAllBytes(full);
        string existingHash = ContentStore.HashOf(existing);

        if (string.Equals(existingHash, ContentStore.HashOf(content), StringComparison.OrdinalIgnoreCase) || SameIgnoringLineEnds(existing, content))
        {
            // La cible a déjà ce contenu : seul le registre est mis à jour, avec
            // ce que la cible a réellement.
            plan.Add(ExchangeAction.Identical, target, source, @checked: true, optional: false, warnings, note: null,
                new PlannedWrite(ExchangeAction.Identical, target, existing, existingHash, file.Item.Key, sourceHash) { Legend = legend });
            return;
        }

        bool sameEntry = entry is not null && string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase);
        if (sameEntry && Untouched(entry!, existing, existingHash, contents))
        {
            plan.Add(ExchangeAction.Update, target, source, @checked: true, optional: false, warnings, note: null,
                new PlannedWrite(ExchangeAction.Update, target, content, existingHash, file.Item.Key, sourceHash) { Legend = legend });
            return;
        }

        // Un travail de l'IA pas encore rapatrié, ou un fichier que l'outil ne
        // connaît pas : on n'écrase que sur demande.
        string why = sameEntry
            ? "modifié dans la cible depuis le dernier échange : rapatriez d'abord, ou cochez pour écraser"
            : "un fichier inconnu de Obfuscator occupe cet emplacement : cochez pour le remplacer";

        plan.Add(ExchangeAction.Blocked, target, source, @checked: false, optional: true, warnings, why,
            new PlannedWrite(ExchangeAction.Update, target, content, existingHash, file.Item.Key, sourceHash) { Legend = legend });
    }

    /// <summary>
    /// Range dans le plan les fichiers de la cible qui n'ont plus de raison
    /// d'y être : leur source a disparu ou a été exclue, ou leur nom
    /// pseudonymisé a changé. Un fichier que l'IA a modifié depuis le dernier
    /// échange n'est retiré que sur demande.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="group">Fichiers du dernier échange, ou <c>null</c></param>
    /// <param name="targetsBySource">Chemin dans la cible de chaque fichier publié, par clé de la source</param>
    /// <param name="contents">Contenus reçus par la cible</param>
    /// <param name="targetRoot">Dossier cible</param>
    /// <param name="linked">Vrai si la cible est le dossier de l'IA</param>
    private static void PlanRemovals(
        PlanBuilder<PlannedWrite> plan,
        ExchangeGroup? group,
        Dictionary<string, string> targetsBySource,
        ContentStore contents,
        string targetRoot,
        bool linked)
    {
        if (group is null)
        {
            return;
        }

        HashSet<string> claimed = new(targetsBySource.Values, StringComparer.OrdinalIgnoreCase);
        foreach (ExchangeEntry entry in group.Entries)
        {
            bool published = targetsBySource.TryGetValue(entry.Source, out string? current);
            if ((published && string.Equals(current, entry.Target, StringComparison.OrdinalIgnoreCase)) || claimed.Contains(entry.Target))
            {
                continue;
            }

            string full = RelativePaths.Resolve(targetRoot, entry.Target);
            if (!File.Exists(full) || RelativePaths.LinkOnPath(targetRoot, full) is not null)
            {
                continue;
            }

            byte[] existing = File.ReadAllBytes(full);
            string existingHash = ContentStore.HashOf(existing);
            bool untouched = Untouched(entry, existing, existingHash, contents);
            string source = LineExemption.DisplayFile(entry.Source);

            string note = (published, untouched) switch
            {
                (true, true) => $"ancien nom, remplacé par {current}",
                (true, false) => $"ancien nom de {current}, modifié dans la cible depuis le dernier échange : pas encore rapatrié",
                (false, true) => "retiré de la source ou exclu",
                (false, false) => "retiré de la source, mais modifié dans la cible depuis le dernier échange : pas encore rapatrié",
            };

            plan.Add(ExchangeAction.Delete, entry.Target, source, @checked: untouched, optional: !published || !untouched, [], note,
                new PlannedWrite(ExchangeAction.Delete, entry.Target, null, existingHash, entry.Source, entry.SourceHash)
                {
                    IsRename = published,
                    Legend = linked ? null : string.Empty,
                });
        }
    }

    /// <summary>
    /// Propose de retirer du dossier de l'IA les fichiers produits autrefois
    /// et copiés à la main : <c>_obfusc</c> et légendes, que l'outil ne connaît
    /// pas. La case est décochée : l'IA a pu y travailler.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="group">Fichiers du dernier échange, ou <c>null</c></param>
    /// <param name="targetScan">Fichiers de la cible</param>
    /// <param name="claimed">Chemins de la cible occupés par l'obfuscation</param>
    /// <param name="targetRoot">Dossier cible</param>
    private static void PlanLegacy(
        PlanBuilder<PlannedWrite> plan,
        ExchangeGroup? group,
        CollectionResult targetScan,
        IEnumerable<string> claimed,
        string targetRoot)
    {
        HashSet<string> taken = new(claimed, StringComparer.OrdinalIgnoreCase);
        foreach (string file in targetScan.Files)
        {
            string relative = RelativePaths.Of(targetRoot, file);
            if (!OutputNaming.IsProduced(Path.GetFileName(relative)) || taken.Contains(relative) || group?.ByTarget(relative) is not null)
            {
                continue;
            }

            plan.Add(ExchangeAction.Legacy, relative, string.Empty, @checked: false, optional: true, [],
                "ancien fichier produit et copié à la main : à retirer s'il n'a plus rien à rapatrier",
                new PlannedWrite(ExchangeAction.Delete, relative, null, ContentStore.HashOf(File.ReadAllBytes(file)), string.Empty, string.Empty));
        }
    }

    /// <summary>
    /// Range dans le plan le guide de l'IA, et le renvoi de Claude Code s'il
    /// manque.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="batch">Lot obfusqué</param>
    /// <param name="snapshot">Instantané du projet</param>
    /// <param name="targetsBySource">Chemin dans la cible de chaque fichier publié</param>
    /// <param name="targetRoot">Dossier cible</param>
    private static void PlanGuide(
        PlanBuilder<PlannedWrite> plan,
        PreparedBatch batch,
        ObfuscationProject snapshot,
        Dictionary<string, string> targetsBySource,
        string targetRoot)
    {
        List<PublishedFile> published = [.. batch.Files.Select(file => new PublishedFile(
            targetsBySource[file.Item.Key],
            LegendWriter.CountByCategory(file.Content.Applied)))];

        string agents = RelativePaths.Resolve(targetRoot, AgentGuideWriter.AgentsFileName);
        string? existing = File.Exists(agents) ? File.ReadAllText(agents) : null;
        string merged = AgentGuideWriter.Merge(existing, AgentGuideWriter.Section(snapshot, published));

        if (!string.Equals(existing, merged, StringComparison.Ordinal))
        {
            byte[] content = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(merged);
            plan.Add(ExchangeAction.Generated, AgentGuideWriter.AgentsFileName, string.Empty, @checked: true, optional: false, [],
                existing is null ? "guide de l'IA, créé" : "guide de l'IA, section Obfuscator régénérée",
                new PlannedWrite(ExchangeAction.Generated, AgentGuideWriter.AgentsFileName, content, existing is null ? null : ContentStore.HashOf(File.ReadAllBytes(agents)), string.Empty, string.Empty));
        }

        string claude = RelativePaths.Resolve(targetRoot, AgentGuideWriter.ClaudeFileName);
        if (snapshot.Settings.Exchange.WriteClaudeMd && !File.Exists(claude))
        {
            plan.Add(ExchangeAction.Generated, AgentGuideWriter.ClaudeFileName, string.Empty, @checked: true, optional: false, [],
                "renvoi de Claude Code vers AGENTS.md, créé",
                new PlannedWrite(ExchangeAction.Generated, AgentGuideWriter.ClaudeFileName, System.Text.Encoding.UTF8.GetBytes(AgentGuideWriter.ClaudeContent), null, string.Empty, string.Empty));
        }
    }

    /// <summary>
    /// Donne la raison pour laquelle un fichier ne peut pas partir, s'il y en
    /// a une.
    /// </summary>
    /// <param name="file">Fichier obfusqué</param>
    /// <param name="target">Chemin du fichier dans la cible</param>
    /// <param name="targetCounts">Nombre de fichiers visant chaque chemin de la cible</param>
    /// <returns>La raison, ou <c>null</c></returns>
    private static string? RefusalOf(PreparedFile file, string target, Dictionary<string, int> targetCounts)
    {
        if (!file.IsClean)
        {
            return "une valeur masquée ailleurs y reste en clair : aucun fichier ne part tant qu'elle n'est pas masquée";
        }

        if (file.Name.Problem is not null)
        {
            return $"nom produit invalide : {file.Name.Problem}";
        }

        if (targetCounts.GetValueOrDefault(target) > 1)
        {
            return "un autre fichier prendrait le même nom dans la cible : deux noms qui ne diffèrent que par la casse ou les accents";
        }

        return HasConflictMarkers(file.Document.Text)
            ? "le fichier en clair contient encore des marqueurs de conflit : résolvez-les d'abord"
            : null;
    }

    /// <summary>
    /// Relève les alertes d'un fichier obfusqué : fuites, valeurs signalées
    /// non tranchées, valeurs connues en clair, fichier sensible.
    /// </summary>
    /// <param name="file">Fichier obfusqué</param>
    /// <returns>Les alertes</returns>
    private List<ExchangeWarning> PublicationWarnings(PreparedFile file)
    {
        // Chaque alerte nomme l'élément en cause par son extrait masqué — la
        // seule forme qui ait le droit d'atteindre un rapport —, sa ligne, et
        // ce qu'il faut faire.
        List<ExchangeWarning> warnings = [];

        foreach (ResidualLiteral leak in file.Leaks)
        {
            int line = leak.Lines.Count > 0 ? leak.Lines[0] : 0;
            warnings.Add(new ExchangeWarning(
                ExchangeWarningKind.Leak,
                line,
                string.Create(CultureInfo.CurrentCulture, $"fuite : {CategoryLabels.Label(leak.Category)} « {leak.MaskedLiteral} » reste en clair{OtherLines(leak.Lines)}, alors qu'elle est masquée ailleurs ({leak.Pseudonym}). Rien ne part tant qu'elle n'est pas masquée : ouvrez l'aperçu de ce fichier, ou ajoutez la valeur aux règles.")));
        }

        foreach (ResidualLiteral leak in file.NameLeaks)
        {
            warnings.Add(new ExchangeWarning(
                ExchangeWarningKind.Leak,
                0,
                $"fuite dans le nom du fichier : {CategoryLabels.Label(leak.Category)} « {leak.MaskedLiteral} » resterait en clair dans le chemin de la cible, alors qu'elle est masquée ailleurs ({leak.Pseudonym})."));
        }

        ObfuscationPolicy policy = _snapshot.Policy;
        List<ResidualFinding> pending = [.. file.Suspicious.Where(finding =>
            !policy.IsKeptEverywhere(finding.Value)
            && !policy.IsExceptedAt(file.Item.Key, finding.Line, file.Document.Text, finding.Start, finding.Value))];

        foreach (ResidualFinding finding in pending.Take(SuspectsListed))
        {
            warnings.Add(new ExchangeWarning(
                ExchangeWarningKind.Suspect,
                finding.Line,
                $"« {finding.MaskedSample} » signalé ({finding.Kind}), non tranché : partira en clair. Dans l'aperçu : « Masquer », « Garder ici » ou « Partout »."));
        }

        if (pending.Count > SuspectsListed)
        {
            warnings.Add(new ExchangeWarning(
                ExchangeWarningKind.Suspect,
                pending[SuspectsListed].Line,
                string.Create(CultureInfo.CurrentCulture, $"… et {pending.Count - SuspectsListed} autre(s) valeur(s) signalée(s) non tranchée(s) : voir l'aperçu de ce fichier.")));
        }

        foreach (ResidualLiteral known in file.KnownInClear)
        {
            int line = known.Lines.Count > 0 ? known.Lines[0] : 0;
            warnings.Add(new ExchangeWarning(
                ExchangeWarningKind.KnownInClear,
                line,
                string.Create(CultureInfo.CurrentCulture, $"{CategoryLabels.Label(known.Category)} « {known.MaskedLiteral} » : masquée autrefois ({known.Pseudonym}), vos règles la laissent maintenant en clair{OtherLines(known.Lines)}. Si ce n'est pas voulu, masquez-la dans l'aperçu.")));
        }

        if (IsSensitive(file.Item.RelativePath))
        {
            warnings.Add(new ExchangeWarning(ExchangeWarningKind.Sensitive, 0, "fichier qu'on n'envoie d'ordinaire pas à une IA : vérifiez qu'il doit partir"));
        }

        return warnings;
    }

    /// <summary>
    /// Cite les autres lignes d'une valeur retrouvée, la première étant celle
    /// de l'alerte.
    /// </summary>
    /// <param name="lines">Lignes des occurrences</param>
    /// <returns>La mention, vide s'il n'y en a pas d'autre</returns>
    private static string OtherLines(IReadOnlyList<int> lines)
    {
        if (lines.Count <= 1)
        {
            return string.Empty;
        }

        string shown = string.Join(", ", lines.Skip(1).Take(8));
        return lines.Count > 9 ? $" (aussi lignes {shown}…)" : $" (aussi ligne(s) {shown})";
    }

    /// <summary>
    /// Indique si un fichier fait partie de ceux qu'on n'envoie d'ordinaire
    /// pas à une IA.
    /// </summary>
    /// <param name="relative">Chemin relatif à la source</param>
    /// <returns><c>true</c> pour un fichier sensible connu</returns>
    private static bool IsSensitive(string relative)
    {
        string name = Path.GetFileName(relative);
        return SensitiveNames.Any(sensitive => sensitive.EndsWith('/')
                ? relative.StartsWith(sensitive, StringComparison.OrdinalIgnoreCase) || relative.Contains("/" + sensitive, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, sensitive, StringComparison.OrdinalIgnoreCase))
            || SensitiveExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Indique si un texte contient encore des marqueurs de conflit : une
    /// ligne d'ouverture et une ligne de fermeture.
    /// </summary>
    /// <param name="text">Texte examiné</param>
    /// <returns><c>true</c> si un conflit reste à résoudre</returns>
    private static bool HasConflictMarkers(string text)
    {
        TextLayout layout = TextLayout.Of(text);
        return layout.Lines.Any(line => line.StartsWith("<<<<<<< ", StringComparison.Ordinal))
            && layout.Lines.Any(line => line.StartsWith(">>>>>>> ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Indique si le fichier de la cible est encore tel que le dernier échange
    /// l'a laissé, fins de ligne mises à part.
    /// </summary>
    /// <param name="entry">Fichier du dernier échange</param>
    /// <param name="existing">Contenu actuel de la cible</param>
    /// <param name="existingHash">Empreinte de ce contenu</param>
    /// <param name="contents">Contenus reçus par la cible</param>
    /// <returns><c>true</c> si personne ne l'a modifié</returns>
    private static bool Untouched(ExchangeEntry entry, byte[] existing, string existingHash, ContentStore contents)
    {
        return string.Equals(existingHash, entry.TargetHash, StringComparison.OrdinalIgnoreCase)
            || (contents.TryGet(entry.TargetHash, out byte[] received) && SameIgnoringLineEnds(existing, received));
    }

    /// <summary>
    /// Vérifie, juste avant d'écrire, que le fichier visé est toujours celui
    /// que le plan a vu, et qu'aucun lien n'a été posé sur son chemin.
    /// </summary>
    /// <param name="full">Chemin absolu du fichier</param>
    /// <param name="observedHash">Empreinte vue au moment du plan, <c>null</c> s'il n'existait pas</param>
    /// <param name="root">Dossier de l'échange</param>
    /// <returns><c>true</c> si l'écriture peut se faire</returns>
    private static bool StillAsPlanned(string full, string? observedHash, string root)
    {
        if (RelativePaths.LinkOnPath(root, full) is not null)
        {
            return false;
        }

        bool exists = File.Exists(full);
        return observedHash is null
            ? !exists
            : exists && string.Equals(ContentStore.HashOfFile(full), observedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Écrit ou retire un fichier de la cible, et met le registre à jour.
    /// </summary>
    /// <param name="write">Écriture prévue</param>
    /// <param name="full">Chemin absolu du fichier</param>
    /// <param name="group">Fichiers de l'échange</param>
    /// <param name="contents">Contenus reçus par la cible</param>
    /// <param name="recycler">Retrait des fichiers</param>
    /// <param name="tally">Comptes du compte rendu</param>
    private static void Apply(PlannedWrite write, string full, ExchangeGroup group, ContentStore contents, IFileRecycler recycler, Tally tally)
    {
        switch (write.Action)
        {
            case ExchangeAction.Delete:
                recycler.Recycle(full);
                RecycleLegend(write, full, recycler);
                if (!write.IsRename && write.SourceKey.Length > 0 && group.BySource(write.SourceKey) is ExchangeEntry removed)
                {
                    group.Entries.Remove(removed);
                }

                tally.Deleted++;
                return;

            case ExchangeAction.Generated:
                AtomicFile.WriteAllBytes(full, write.Content!);
                group.RecordGenerated(write.TargetRelative, ContentStore.HashOf(write.Content!));
                return;

            case ExchangeAction.Identical:
                Record(group, write, contents.Put(write.Content!));
                WriteLegend(write, full);
                return;

            default:
                AtomicFile.WriteAllBytes(full, write.Content!);
                WriteLegend(write, full);
                Record(group, write, contents.Put(write.Content!));
                if (write.ObservedHash is null)
                {
                    tally.Created++;
                }
                else
                {
                    tally.Modified++;
                }

                return;
        }
    }

    /// <summary>
    /// Écrit la légende d'un fichier de « out », s'il en a une.
    /// </summary>
    /// <param name="write">Écriture prévue</param>
    /// <param name="full">Chemin absolu du fichier</param>
    private static void WriteLegend(PlannedWrite write, string full)
    {
        if (!string.IsNullOrEmpty(write.Legend))
        {
            AtomicFile.WriteAllText(OutputNaming.LegendPath(full), write.Legend);
        }
    }

    /// <summary>
    /// Retire la légende d'un fichier de « out » retiré.
    /// </summary>
    /// <param name="write">Retrait prévu</param>
    /// <param name="full">Chemin absolu du fichier retiré</param>
    /// <param name="recycler">Retrait des fichiers</param>
    private static void RecycleLegend(PlannedWrite write, string full, IFileRecycler recycler)
    {
        string legend = OutputNaming.LegendPath(full);
        if (write.Legend is not null && File.Exists(legend))
        {
            recycler.Recycle(legend);
        }
    }

    /// <summary>
    /// Inscrit au registre ce que la cible a reçu.
    /// </summary>
    /// <param name="group">Fichiers de l'échange</param>
    /// <param name="write">Écriture faite</param>
    /// <param name="targetHash">Empreinte du contenu de la cible, rangé au magasin</param>
    private static void Record(ExchangeGroup group, PlannedWrite write, string targetHash)
    {
        ExchangeEntry? entry = group.BySource(write.SourceKey);
        if (entry is null)
        {
            entry = new ExchangeEntry { Source = write.SourceKey };
            group.Entries.Add(entry);
        }

        entry.Target = write.TargetRelative;
        entry.SourceHash = write.SourceHash;
        entry.TargetHash = targetHash;
        entry.SyncedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// Plan en construction : fichiers numérotés et écritures prévues.
    /// </summary>
    /// <typeparam name="TWrite">Nature des écritures prévues</typeparam>
    private sealed class PlanBuilder<TWrite>
        where TWrite : class
    {
        private readonly List<ExchangeChange> _changes = [];
        private readonly Dictionary<int, TWrite> _writes = [];

        /// <summary>Fichiers du plan, dans l'ordre d'ajout.</summary>
        public IReadOnlyList<ExchangeChange> Changes => _changes;

        /// <summary>Écritures prévues, par identifiant.</summary>
        public IReadOnlyDictionary<int, TWrite> Writes => _writes;

        /// <summary>
        /// Ajoute un fichier au plan.
        /// </summary>
        /// <param name="action">Ce que l'échange en fait</param>
        /// <param name="target">Chemin dans la cible</param>
        /// <param name="source">Chemin dans la source</param>
        /// <param name="checked">Vrai si l'échange le fait d'office</param>
        /// <param name="optional">Vrai si l'utilisateur peut cocher ou décocher</param>
        /// <param name="warnings">Alertes</param>
        /// <param name="note">Explication courte</param>
        /// <param name="write">Écriture prévue, ou <c>null</c></param>
        /// <returns>Le fichier ajouté</returns>
        public ExchangeChange Add(
            ExchangeAction action,
            string target,
            string source,
            bool @checked,
            bool optional,
            IReadOnlyList<ExchangeWarning> warnings,
            string? note,
            TWrite? write)
        {
            ExchangeChange change = new(_changes.Count + 1, action, target, source, @checked, optional, warnings) { Note = note };
            _changes.Add(change);
            if (write is not null)
            {
                _writes[change.Id] = write;
            }

            return change;
        }

        /// <summary>
        /// Remplace un fichier déjà ajouté, pour y porter les comptes d'une fusion.
        /// </summary>
        /// <param name="change">Fichier mis à jour</param>
        public void Replace(ExchangeChange change)
        {
            _changes[change.Id - 1] = change;
        }
    }

    /// <summary>Comptes d'un échange écrit.</summary>
    private sealed class Tally
    {
        /// <summary>Fichiers créés.</summary>
        public int Created { get; set; }

        /// <summary>Fichiers modifiés.</summary>
        public int Modified { get; set; }

        /// <summary>Fichiers retirés.</summary>
        public int Deleted { get; set; }

        /// <summary>Conflits écrits.</summary>
        public int Conflicts { get; set; }

        /// <summary>Fichiers laissés de côté, avec la raison.</summary>
        public List<string> Skipped { get; } = [];
    }
}
