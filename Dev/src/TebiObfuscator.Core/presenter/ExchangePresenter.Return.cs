using System.Globalization;
using TebiObfuscator.Core.Model.Documents;
using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Exchange;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Model.Reporting;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Rapatriement du travail de l'IA, de la cible vers la source.
/// </summary>
public sealed partial class ExchangePresenter
{
    /// <summary>Extensions des fichiers qui s'exécutent, à reprendre seulement après les avoir lus.</summary>
    private static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".py", ".js", ".vbs", ".exe"];

    /// <summary>
    /// Prépare le rapatriement : chaque fichier que l'IA a modifié, créé ou
    /// supprimé depuis le dernier échange est restauré, fusionné avec le
    /// fichier en clair actuel, et contrôlé. Rien n'est écrit. Peut s'exécuter
    /// sur n'importe quel fil.
    /// </summary>
    /// <param name="progress">Destinataire de l'avancement, facultatif</param>
    /// <returns>Le plan, à confirmer</returns>
    public ReturnPlan PrepareReturn(IProgress<BatchProgress>? progress)
    {
        if (Interlocked.Exchange(ref _prepared, 1) != 0)
        {
            throw new InvalidOperationException("Un présentateur d'échange ne prépare qu'un seul geste : créez-en un autre.");
        }

        ObfuscationProject snapshot = _snapshot;
        string sourceRoot = snapshot.SourcePath;
        string targetRoot = snapshot.TargetPath;

        CollectionResult targetScan = CollectTarget(snapshot);
        IsolationReport isolation = IsolationGuard.Check(
            snapshot.RootPath,
            snapshot.IsSourceLinked ? sourceRoot : null,
            snapshot.IsTargetLinked ? targetRoot : null,
            targetScan.Links);

        ExchangeRegister register = ExchangeRegister.Load(snapshot.ExchangeRegisterPath, snapshot.ExchangeProtector);
        ExchangeGroup? group = register.FindGroup(targetRoot, sourceRoot);

        if (group is null || !Directory.Exists(sourceRoot) || !Directory.Exists(targetRoot))
        {
            return new ReturnPlan(sourceRoot, snapshot.IsSourceLinked, targetRoot, group is not null, isolation, [], new Dictionary<int, PlannedReturn>());
        }

        DeobfuscationEngine engine = snapshot.CreateDeobfuscationEngine();
        ContentStore contents = new(snapshot.ExchangeContentPath);
        PlanBuilder<PlannedReturn> plan = new();
        List<string> stale = [];

        Dictionary<string, string> targetFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in targetScan.Files)
        {
            targetFiles[RelativePaths.Of(targetRoot, file)] = file;
        }

        int total = group.Entries.Count + targetFiles.Count;
        int done = 0;

        foreach (ExchangeEntry entry in group.Entries.OrderBy(item => item.Target, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report(new BatchProgress(done, total, entry.Target, "comparaison", (double)done++ / Math.Max(1, total)));

            string sourceRelative = LineExemption.DisplayFile(entry.Source);
            if (!targetFiles.TryGetValue(entry.Target, out string? targetFull))
            {
                PlanDeletion(plan, stale, entry, sourceRoot, targetRoot);
                continue;
            }

            byte[] theirs = File.ReadAllBytes(targetFull);
            if (string.Equals(ContentStore.HashOf(theirs), entry.TargetHash, StringComparison.OrdinalIgnoreCase)
                || (contents.TryGet(entry.TargetHash, out byte[] received) && SameIgnoringLineEnds(theirs, received)))
            {
                continue;
            }

            PlanModification(plan, engine, contents, entry, theirs, RelativePaths.Resolve(sourceRoot, sourceRelative), sourceRelative);
        }

        HashSet<string> known = new(group.Entries.Select(entry => entry.Target), StringComparer.OrdinalIgnoreCase);
        foreach ((string relative, string full) in targetFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report(new BatchProgress(done, total, relative, "comparaison", (double)done++ / Math.Max(1, total)));

            if (!known.Contains(relative) && !group.IsGenerated(relative) && !IsProducedLegacy(snapshot, relative))
            {
                PlanCreation(plan, engine, relative, full, sourceRoot, snapshot);
            }
        }

        return new ReturnPlan(sourceRoot, snapshot.IsSourceLinked, targetRoot, hasExchanged: true, isolation, plan.Changes, plan.Writes)
        {
            StaleEntries = stale,
        };
    }

    /// <summary>
    /// Écrit un rapatriement préparé : chaque fichier retenu est écrit dans la
    /// source d'un seul geste, le registre est mis à jour, et les occurrences
    /// gardées ici sont recalées sur leurs nouvelles lignes. Un fichier en
    /// clair modifié entre le plan et la confirmation est laissé de côté. Une
    /// suppression de l'IA laissée décochée est oubliée du registre : le
    /// fichier en clair repartira vers la cible.
    /// </summary>
    /// <param name="plan">Plan confirmé</param>
    /// <param name="selected">Identifiants des fichiers facultatifs que l'utilisateur a cochés</param>
    /// <param name="recycler">Retrait des fichiers, à la corbeille</param>
    /// <returns>Le compte rendu</returns>
    /// <exception cref="InvalidOperationException">Si le plan ne peut plus être écrit</exception>
    public ExchangeOutcome CommitReturn(ReturnPlan plan, IReadOnlySet<int> selected, IFileRecycler recycler)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(recycler);

        if (!plan.CanCommit)
        {
            throw new InvalidOperationException("Ce rapatriement ne peut pas être écrit.");
        }

        IsolationReport isolation = CheckIsolation(_project);
        if (!isolation.IsSafe)
        {
            throw new InvalidOperationException(isolation.Blocking[0]);
        }

        ExchangeRegister register = ExchangeRegister.Load(_project.ExchangeRegisterPath, _project.ExchangeProtector);
        ExchangeGroup group = register.FindGroup(plan.TargetRoot, plan.SourceRoot)
            ?? throw new InvalidOperationException("Le registre des échanges ne connaît plus cette cible.");

        ContentStore contents = new(_project.ExchangeContentPath);
        Tally tally = new();
        bool exemptionsMoved = false;
        int warnings = 0;

        foreach (ExchangeChange change in plan.Changes)
        {
            if (!plan.Writes.TryGetValue(change.Id, out PlannedReturn? write) || !(change.Optional ? selected.Contains(change.Id) : change.Checked))
            {
                // Une suppression de l'IA vue et laissée de côté est une
                // décision prise : le fichier en clair reste, et repartira vers
                // la cible à la prochaine obfuscation, comme un fichier nouveau.
                // Le recréer ne perd rien. Une modification refusée, elle, reste
                // en attente : l'écraser perdrait le travail de l'IA.
                if (change.Action == ExchangeAction.Deleted && group.ByTarget(change.TargetPath) is ExchangeEntry declined)
                {
                    group.Entries.Remove(declined);
                }

                continue;
            }

            string sourceFull = RelativePaths.Resolve(plan.SourceRoot, LineExemption.DisplayFile(write.SourceKey));
            if (!StillAsPlanned(sourceFull, write.ObservedSourceHash, plan.SourceRoot))
            {
                tally.Skipped.Add($"{change.SourcePath} : modifié pendant la confirmation, laissé tel quel.");
                continue;
            }

            if (write.Action == ExchangeAction.Deleted)
            {
                recycler.Recycle(sourceFull);
                if (group.BySource(write.SourceKey) is ExchangeEntry removed)
                {
                    group.Entries.Remove(removed);
                }

                tally.Deleted++;
                continue;
            }

            // Dans « in », qui n'est pas un dépôt git, la version remplacée part
            // à la corbeille : c'est là qu'on la retrouve.
            if (!plan.SourceIsLinked && File.Exists(sourceFull))
            {
                recycler.Recycle(sourceFull);
            }

            AtomicFile.WriteAllBytes(sourceFull, write.Content!);

            ExchangeEntry? entry = group.BySource(write.SourceKey);
            if (entry is null)
            {
                entry = new ExchangeEntry { Source = write.SourceKey };
                group.Entries.Add(entry);
            }

            entry.Target = write.TargetRelative;
            entry.SourceHash = ContentStore.HashOf(write.Content!);
            entry.TargetHash = contents.Put(write.TargetBytes);
            entry.SyncedAt = DateTimeOffset.Now;

            if (write.LineMap is not null)
            {
                exemptionsMoved |= MoveExemptions(write.SourceKey, write.LineMap);
            }

            tally.Conflicts += change.Conflicts;
            warnings += change.Warnings.Count;
            if (write.Action == ExchangeAction.Created)
            {
                tally.Created++;
            }
            else
            {
                tally.Modified++;
            }
        }

        foreach (string target in plan.StaleEntries)
        {
            if (group.ByTarget(target) is ExchangeEntry gone)
            {
                group.Entries.Remove(gone);
            }
        }

        register.Record(new ExchangeEvent
        {
            At = DateTimeOffset.Now,
            Gesture = ExchangeGesture.Return,
            Folder = FolderLabel(plan.SourceRoot),
            Created = tally.Created,
            Modified = tally.Modified,
            Deleted = tally.Deleted,
            Conflicts = tally.Conflicts,
            Warnings = warnings,
        });

        register.Save(_project.ExchangeRegisterPath, _project.ExchangeProtector);
        contents.Collect(register.ReferencedContents());

        if (exemptionsMoved)
        {
            _project.RefreshPolicy();
            _project.SaveRules();
        }

        return new ExchangeOutcome(tally.Created, tally.Modified, tally.Deleted, tally.Conflicts, warnings, tally.Skipped);
    }

    /// <summary>
    /// Range dans le plan un fichier que l'IA a modifié : sa version est
    /// restaurée puis fusionnée avec le fichier en clair actuel.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="engine">Moteur de restauration</param>
    /// <param name="contents">Contenus reçus par la cible</param>
    /// <param name="entry">Fichier du dernier échange</param>
    /// <param name="theirsBytes">Contenu actuel de la cible</param>
    /// <param name="sourceFull">Chemin absolu du fichier en clair</param>
    /// <param name="sourceRelative">Chemin du fichier en clair, relatif à la source</param>
    private void PlanModification(
        PlanBuilder<PlannedReturn> plan,
        DeobfuscationEngine engine,
        ContentStore contents,
        ExchangeEntry entry,
        byte[] theirsBytes,
        string sourceFull,
        string sourceRelative)
    {
        SourceDocument theirsDocument = SourceDocument.Load(theirsBytes);
        DeobfuscationResult theirs = engine.Deobfuscate(theirsDocument.Text);
        TextLayout theirsLayout = TextLayout.Of(theirs.Text);

        // Le fichier en clair a disparu depuis l'échange : la version de l'IA
        // peut le recréer, mais seulement sur demande.
        if (!File.Exists(sourceFull))
        {
            List<int> identity = [.. Enumerable.Range(0, theirsLayout.Lines.Count)];
            plan.Add(ExchangeAction.Created, entry.Target, sourceRelative, @checked: false, optional: true,
                ReturnWarnings(theirs, identity, FormatSniffer.Detect(sourceRelative, theirs.Text), theirs.Text, null),
                "supprimé côté clair depuis l'échange : cochez pour le recréer",
                new PlannedReturn(ExchangeAction.Created, entry.Source, entry.Target, theirsDocument.ToBytes(theirs.Text), null, theirsBytes));
            return;
        }

        byte[] currentBytes = File.ReadAllBytes(sourceFull);
        SourceDocument current = SourceDocument.Load(currentBytes);
        TextLayout currentLayout = TextLayout.Of(current.Text);
        string currentHash = ContentStore.HashOf(currentBytes);
        bool sourceUnchanged = string.Equals(currentHash, entry.SourceHash, StringComparison.OrdinalIgnoreCase);

        MergeResult merge;
        MergeInputs? inputs = null;
        FileKind kind = FormatSniffer.Detect(sourceRelative, current.Text);
        bool @checked = true;
        bool optional = false;
        string? note = null;

        if (contents.TryGet(entry.TargetHash, out byte[] received))
        {
            // La base est restaurée comme la version de l'IA : leurs artefacts
            // de restauration se répondent et s'annulent.
            TextLayout baseLayout = TextLayout.Of(engine.Deobfuscate(SourceDocument.Load(received).Text).Text);
            bool aligned = sourceUnchanged && baseLayout.Lines.Count == currentLayout.Lines.Count;

            if (aligned)
            {
                merge = ThreeWayMerge.Apply(baseLayout, currentLayout, theirsLayout);
            }
            else
            {
                // Les éléments de la fusion sont gardés : ses conflits pourront
                // être tranchés, et la fusion refaite.
                inputs = new MergeInputs(baseLayout, currentLayout, theirsLayout, current, theirs, kind);
                merge = ThreeWayMerge.Merge(baseLayout, currentLayout, theirsLayout);
            }

            note = sourceUnchanged ? null : "le fichier en clair a aussi changé depuis l'échange : fusion à trois";
        }
        else
        {
            // Sans la base, on ne sait pas ce que l'IA a changé : sa version
            // remplace le fichier en clair, seulement sur demande.
            merge = ThreeWayMerge.Merge(currentLayout, currentLayout, theirsLayout);
            @checked = false;
            optional = true;
            note = "contenu reçu par l'IA introuvable : sa version remplacerait le fichier en clair — cochez pour l'accepter";
        }

        if (merge.IsUnchanged)
        {
            return;
        }

        (byte[] content, List<ExchangeWarning> warnings) = Compose(current, theirs, kind, merge);

        ExchangeChange change = plan.Add(ExchangeAction.Modified, entry.Target, sourceRelative, @checked, optional, warnings, note,
            new PlannedReturn(ExchangeAction.Modified, entry.Source, entry.Target, content, currentHash, theirsBytes)
            {
                LineMap = merge.CurrentToResult,
                Merge = merge.Blocks.Count > 0 ? inputs : null,
                Blocks = merge.Blocks,
            });

        plan.Replace(change with { Added = merge.Added, Removed = merge.Removed, Conflicts = merge.Conflicts });
    }

    /// <summary>
    /// Tranche les conflits d'un fichier du rapatriement : la fusion est
    /// refaite avec les choix donnés, puis le contenu à écrire, ses alertes et
    /// ses comptes sont recalculés. Un conflit laissé non tranché garde ses
    /// marqueurs. Rien n'est écrit : le plan est mis à jour.
    /// </summary>
    /// <param name="plan">Plan du rapatriement</param>
    /// <param name="changeId">Identifiant du fichier</param>
    /// <param name="choices">Ce que l'on garde de chaque conflit, dans l'ordre du fichier</param>
    /// <returns>Le fichier, mis à jour</returns>
    /// <exception cref="InvalidOperationException">Si le fichier n'a pas de conflit à trancher</exception>
    public ExchangeChange ResolveConflicts(ReturnPlan plan, int changeId, IReadOnlyList<ConflictChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(choices);

        if (!plan.Writes.TryGetValue(changeId, out PlannedReturn? write) || write.Merge is not MergeInputs inputs)
        {
            throw new InvalidOperationException("Ce fichier n'a pas de conflit à trancher.");
        }

        MergeResult merge = ThreeWayMerge.Merge(inputs.Base, inputs.Current, inputs.Theirs, choices);
        (byte[] content, List<ExchangeWarning> warnings) = Compose(inputs.Document, inputs.Restored, inputs.Kind, merge);

        ExchangeChange updated = plan.Changes.Single(change => change.Id == changeId) with
        {
            Warnings = warnings,
            Added = merge.Added,
            Removed = merge.Removed,
            Conflicts = merge.Conflicts,
        };

        plan.Replace(updated, write with
        {
            Content = content,
            LineMap = merge.CurrentToResult,
            Blocks = merge.Blocks,
            Choices = [.. choices],
        });

        return updated;
    }

    /// <summary>
    /// Compose le contenu à écrire d'une fusion, et ses alertes : celles de la
    /// restauration, les conflits non tranchés, un changement d'encodage.
    /// </summary>
    /// <param name="current">Fichier en clair actuel, avec son encodage</param>
    /// <param name="theirs">Restauration de la version de l'IA</param>
    /// <param name="kind">Format du fichier</param>
    /// <param name="merge">Fusion</param>
    /// <returns>Les octets à écrire et les alertes</returns>
    private (byte[] Content, List<ExchangeWarning> Warnings) Compose(SourceDocument current, DeobfuscationResult theirs, FileKind kind, MergeResult merge)
    {
        byte[] content = current.ToBytesOrUtf8(merge.Text, out bool switched);
        List<ExchangeWarning> warnings = ReturnWarnings(theirs, merge.TheirsToResult, kind, merge.Text, current.Text);

        foreach (int line in merge.ConflictLines)
        {
            warnings.Add(new ExchangeWarning(ExchangeWarningKind.Conflict, line, "conflit à résoudre : le même passage a changé des deux côtés — « Résoudre… » pour choisir"));
        }

        if (switched)
        {
            warnings.Add(new ExchangeWarning(ExchangeWarningKind.Encoding, 0, "l'IA a ajouté des caractères que l'encodage du fichier ne sait pas écrire : il passe en UTF-8"));
        }

        return (content, warnings);
    }

    /// <summary>
    /// Range dans le plan un fichier que l'IA a supprimé : la suppression est
    /// proposée, décochée, si le fichier en clair n'a pas changé depuis
    /// l'échange. Un fichier disparu des deux côtés est simplement oublié.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="stale">Fichiers à oublier, complétés ici</param>
    /// <param name="entry">Fichier du dernier échange</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <param name="targetRoot">Dossier cible</param>
    private static void PlanDeletion(PlanBuilder<PlannedReturn> plan, List<string> stale, ExchangeEntry entry, string sourceRoot, string targetRoot)
    {
        // Un fichier présent mais écarté de l'examen — un lien, un fichier
        // ignoré — n'a pas été supprimé.
        if (File.Exists(RelativePaths.Resolve(targetRoot, entry.Target)))
        {
            return;
        }

        string sourceRelative = LineExemption.DisplayFile(entry.Source);
        string sourceFull = RelativePaths.Resolve(sourceRoot, sourceRelative);
        if (!File.Exists(sourceFull))
        {
            stale.Add(entry.Target);
            return;
        }

        string currentHash = ContentStore.HashOfFile(sourceFull);
        bool untouched = string.Equals(currentHash, entry.SourceHash, StringComparison.OrdinalIgnoreCase);

        plan.Add(ExchangeAction.Deleted, entry.Target, sourceRelative, @checked: false, optional: untouched, [],
            untouched
                ? "supprimé par l'IA : cochez pour le retirer aussi de la source"
                : "supprimé par l'IA, mais modifié côté clair depuis l'échange : laissé en place",
            untouched ? new PlannedReturn(ExchangeAction.Deleted, entry.Source, entry.Target, null, currentHash, []) : null);
    }

    /// <summary>
    /// Range dans le plan un fichier que l'IA a créé : son contenu et son nom
    /// sont restaurés. Un script, un réglage d'outil ou un fichier qui
    /// existerait déjà dans la source n'est repris que sur demande.
    /// </summary>
    /// <param name="plan">Plan en construction</param>
    /// <param name="engine">Moteur de restauration</param>
    /// <param name="relative">Chemin du fichier dans la cible</param>
    /// <param name="full">Chemin absolu du fichier dans la cible</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <param name="snapshot">Instantané du projet</param>
    private void PlanCreation(
        PlanBuilder<PlannedReturn> plan,
        DeobfuscationEngine engine,
        string relative,
        string full,
        string sourceRoot,
        ObfuscationProject snapshot)
    {
        byte[] bytes = File.ReadAllBytes(full);
        SourceDocument document = SourceDocument.Load(bytes);
        DeobfuscationResult restored = engine.Deobfuscate(document.Text);

        string clear = RestoreName(engine, relative, sourceRoot, snapshot.IsTargetLinked);
        string? problem = RelativePaths.Problem(clear);
        if (problem is not null)
        {
            plan.Add(ExchangeAction.Refused, relative, clear, @checked: false, optional: false, [], $"nom restauré invalide : {problem}", null);
            return;
        }

        string sourceFull = RelativePaths.Resolve(sourceRoot, clear);
        string? link = RelativePaths.LinkOnPath(sourceRoot, sourceFull);
        if (link is not null)
        {
            plan.Add(ExchangeAction.Refused, relative, clear, @checked: false, optional: false, [],
                "un lien se trouve sur le chemin dans la source : un échange ne passe jamais par un lien", null);
            return;
        }

        int lines = TextLayout.Of(restored.Text).Lines.Count;
        List<ExchangeWarning> warnings = ReturnWarnings(restored, [.. Enumerable.Range(0, lines)], FormatSniffer.Detect(clear, restored.Text), restored.Text, null);

        bool exists = File.Exists(sourceFull);
        bool script = IsScriptOrToolSetting(clear);
        if (script)
        {
            warnings.Add(new ExchangeWarning(ExchangeWarningKind.Script, 0, "script ou réglage d'outil créé par l'IA : lisez-le avant de le reprendre"));
        }

        List<string> exclusions = [.. snapshot.Settings.Exchange.Excluded.Select(TextFileCollector.NormalizeExclusion).Where(item => item.Length > 0)];
        if (TextFileCollector.IsExcluded(exclusions, clear))
        {
            warnings.Add(new ExchangeWarning(ExchangeWarningKind.Note, 0, "créé dans un emplacement exclu des échanges"));
        }

        string? note = exists ? "un fichier de ce nom existe déjà dans la source, hors des échanges : cochez pour le remplacer" : null;
        ExchangeChange change = plan.Add(ExchangeAction.Created, relative, clear, @checked: !exists && !script, optional: true, warnings, note,
            new PlannedReturn(ExchangeAction.Created, LineExemption.PathKeyMark + clear, relative, document.ToBytes(restored.Text), exists ? ContentStore.HashOfFile(sourceFull) : null, bytes));

        plan.Replace(change with { Added = lines });
    }

    /// <summary>
    /// Restaure le nom d'un fichier créé par l'IA, élément par élément. Dans
    /// « out », le suffixe « _obfusc » est retiré ; un dossier qui existe déjà
    /// dans la source garde sa casse.
    /// </summary>
    /// <param name="engine">Moteur de restauration</param>
    /// <param name="relative">Chemin dans la cible</param>
    /// <param name="sourceRoot">Dossier source</param>
    /// <param name="linked">Vrai si la cible est le dossier de l'IA</param>
    /// <returns>Le chemin dans la source, avec des barres obliques</returns>
    private static string RestoreName(DeobfuscationEngine engine, string relative, string sourceRoot, bool linked)
    {
        string[] segments = RelativePaths.Segments(relative);
        List<string> restored = new(segments.Length);
        string folder = sourceRoot;

        for (int index = 0; index < segments.Length; index++)
        {
            bool isFile = index == segments.Length - 1;
            string segment = isFile && !linked ? OutputNaming.ClearName(segments[index]) : segments[index];
            string extension = isFile ? Path.GetExtension(segment) : string.Empty;
            string stem = segment[..^extension.Length];
            string name = (stem.Length == 0 ? stem : engine.Deobfuscate(stem).Text) + extension;

            if (!isFile && Directory.Exists(folder))
            {
                // La restauration rend la première graphie connue d'une valeur :
                // un dossier existant garde la sienne.
                name = Directory.EnumerateDirectories(folder)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            }

            restored.Add(name);
            folder = Path.Combine(folder, name);
        }

        return string.Join('/', restored);
    }

    /// <summary>
    /// Relève les alertes d'un fichier restauré, aux lignes du fichier écrit :
    /// jetons inconnus ou d'un autre coffre, repères à compléter, adresses
    /// déduites, coordonnées inventées, syntaxe cassée.
    /// </summary>
    /// <param name="theirs">Restauration de la version de l'IA</param>
    /// <param name="theirsToResult">Rang, dans le fichier écrit, de chaque ligne restaurée</param>
    /// <param name="kind">Format du fichier</param>
    /// <param name="result">Texte écrit</param>
    /// <param name="before">Texte en clair avant le rapatriement, ou <c>null</c> pour un fichier nouveau</param>
    /// <returns>Les alertes, une par nature et par ligne</returns>
    private List<ExchangeWarning> ReturnWarnings(
        DeobfuscationResult theirs,
        IReadOnlyList<int> theirsToResult,
        FileKind kind,
        string result,
        string? before)
    {
        List<ExchangeWarning> warnings = [];
        TextLines lines = new(theirs.Text);
        string ownPrefix = _snapshot.Format.Prefix + _snapshot.Format.Nonce;

        // Ligne, dans le fichier écrit, d'une position du texte de l'IA ; zéro
        // si cette ligne n'y a pas été reprise.
        int ResultLine(int inputPosition)
        {
            int line = lines.LineOf(theirs.OutputPositionOf(inputPosition)) - 1;
            return line >= 0 && line < theirsToResult.Count && theirsToResult[line] >= 0 ? theirsToResult[line] + 1 : 0;
        }

        foreach (UnknownToken token in theirs.UnknownTokens)
        {
            int line = ResultLine(token.Start);
            if (line == 0)
            {
                continue;
            }

            bool own = token.Canonical.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase);
            warnings.Add(own
                ? new ExchangeWarning(ExchangeWarningKind.UnknownToken, line, $"jeton inconnu du coffre, laissé tel quel ({token.Canonical}) : à remplacer par la vraie valeur")
                : new ExchangeWarning(ExchangeWarningKind.ForeignToken, line, $"jeton d'un autre coffre, laissé tel quel ({token.Canonical})"));
        }

        foreach (TokenSubstitution substitution in theirs.Substitutions.Where(item => item.Inferred))
        {
            int line = ResultLine(substitution.Start);
            if (line > 0)
            {
                warnings.Add(new ExchangeWarning(ExchangeWarningKind.Inferred, line, "adresse déduite de son sous-réseau : à vérifier"));
            }
        }

        foreach ((int start, int _) in theirs.UnmatchedCoordinates)
        {
            int line = ResultLine(start);
            if (line > 0)
            {
                warnings.Add(new ExchangeWarning(ExchangeWarningKind.Coordinate, line, "coordonnée inventée par l'IA, restée fictive : à remplacer par la vraie valeur"));
            }
        }

        // Un repère « À_COMPLÉTER » se cherche dans le texte écrit, accents et
        // casse mis à part : l'IA l'écrit parfois sans accents.
        string placeholder = TextFolding.Fold(AgentGuideWriter.Placeholder);
        TextLayout written = TextLayout.Of(result);
        for (int line = 0; line < written.Lines.Count; line++)
        {
            if (TextFolding.Fold(written.Lines[line]).Contains(placeholder, StringComparison.Ordinal))
            {
                warnings.Add(new ExchangeWarning(ExchangeWarningKind.Placeholder, line + 1, $"valeur réelle à compléter (repère {AgentGuideWriter.Placeholder})"));
            }
        }

        // Une syntaxe cassée n'est imputée à l'IA que si le fichier se lisait
        // avant ; des marqueurs de conflit la cassent forcément, et sont
        // signalés à part.
        if (!result.Contains(ThreeWayMerge.OursMarker, StringComparison.Ordinal))
        {
            StructureCheck check = StructureValidator.Check(kind, result);
            if (check.Applicable && !check.Valid && (before is null || StructureValidator.Check(kind, before).Valid))
            {
                warnings.Add(new ExchangeWarning(ExchangeWarningKind.Syntax, check.Line, string.Create(CultureInfo.CurrentCulture, $"{kind} illisible : {check.Message}")));
            }
        }

        return [.. warnings.DistinctBy(warning => (warning.Kind, warning.Line)).OrderBy(warning => warning.Line)];
    }

    /// <summary>
    /// Indique si un fichier créé par l'IA est un script ou un réglage d'outil,
    /// à lire avant de le reprendre dans la source.
    /// </summary>
    /// <param name="relative">Chemin du fichier</param>
    /// <returns><c>true</c> pour un script ou un réglage d'outil</returns>
    private static bool IsScriptOrToolSetting(string relative)
    {
        return ScriptExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)
            || RelativePaths.Segments(relative).Any(segment => segment.StartsWith('.'));
    }

    /// <summary>
    /// Recale sur leurs nouvelles lignes les occurrences gardées ici d'un
    /// fichier réécrit ; une occurrence dont la ligne a disparu est oubliée.
    /// </summary>
    /// <param name="sourceKey">Clé du fichier réécrit</param>
    /// <param name="lineMap">Rang, dans le fichier écrit, de chaque ligne de l'ancien fichier</param>
    /// <returns><c>true</c> si une exception a changé</returns>
    private bool MoveExemptions(string sourceKey, IReadOnlyList<int> lineMap)
    {
        IList<string> settings = _project.Settings.LineExceptions;
        bool changed = false;

        for (int index = settings.Count - 1; index >= 0; index--)
        {
            if (!LineExemption.TryParse(settings[index], out LineExemption exemption)
                || !string.Equals(exemption.File, sourceKey, StringComparison.Ordinal)
                || exemption.Line < 1
                || exemption.Line > lineMap.Count)
            {
                continue;
            }

            int moved = lineMap[exemption.Line - 1];
            if (moved < 0)
            {
                settings.RemoveAt(index);
                changed = true;
            }
            else if (moved + 1 != exemption.Line)
            {
                settings[index] = (exemption with { Line = moved + 1 }).ToSetting();
                changed = true;
            }
        }

        return changed;
    }
}
