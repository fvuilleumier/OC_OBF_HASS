# MHassnage — guide pour les agents

MHassnage maintient une installation **Home Assistant (« Hass »)** avec l'aide d'une IA, **sans jamais
exposer les vraies données** à cette IA. L'application automatise le cycle complet :

```
Hass ──export──▶ dépôt PRIVÉ (valeurs réelles) ──Obfuscator──▶ dépôt PUBLIC (pseudonymisé)
                                                                     │ l'IA y travaille
Hass ◀─appliquer── dépôt PRIVÉ ◀──Obfuscator (rapatriement)──────────┘
```

- **Dépôt privé** `OREE_DOUS_HASS` : export réel de Hass, sert aussi de backup. Jamais lu par une IA.
- **Dépôt public** `OC_OBF_HASS` (dossier `Hass/`) : même contenu pseudonymisé ; l'IA ne voit que lui.
- **Obfuscator** (application externe de l'utilisateur, projet `OBF_HASS`) : pseudonymise / dépseudonymise.
  MHassnage le pilote par son CLI `obfcli` et ouvre sa WPF `Obfuscator.exe` quand une décision humaine est requise.

> Règle absolue : **aucun secret dans le dépôt** (jeton Hass, identifiants). Toute la configuration réelle
> vit dans `%APPDATA%\MHassnage\appsettings.json`, hors git.

---

## Solution

.NET 10, `MHassnage.slnx`, `Directory.Build.props` (Nullable, ImplicitUsings, LangVersion latest).
Architecture **Service / Model / View / ViewController (+ Controller)**. Code et commentaires en français.

| Projet | Cible | Rôle |
|---|---|---|
| `MHassnage.Core` | net10.0, sans UI | Modèles et services ; porte la **version** (`<Version>` du csproj, affichée « v.x.x.x.x ») |
| `MHassnage.HassExport.Cli` | net10.0, exe `mhassnage-export` | Console mince : export Hass vers le dépôt privé |
| `MHassnage.Wpf` | net10.0-windows, exe `MHassnage` | Application principale (CommunityToolkit.Mvvm) |

Paquets : `Microsoft.Extensions.Configuration.Json/Binder`, `YamlDotNet`, `DiffPlex` (Core),
`CommunityToolkit.Mvvm` (Wpf). Git et l'Obfuscator sont appelés en **processus externes**.

---

## Configuration

- Fichier réel : `%APPDATA%\MHassnage\appsettings.json` (ou variable d'environnement `MHASSNAGE_SETTINGS`).
  Créé au premier lancement à partir de `MHassnage.Core/appsettings.example.json` (versionné, sans secret).
- `SettingsService` : chargement, validation (réglages manquants ou `À_COMPLÉTER`), écriture
  (exclusions, correspondances de fichiers).
- **Jeton Hass** (`Hass:Token`) : saisi en clair, chiffré au lancement suivant par `SecretProtector`
  (AES-256-GCM, clé dérivée HKDF d'une clé propre à l'application, préfixe `enc:v1:`). Lisible sur tout poste
  où MHassnage est installé, illisible pour qui n'a que le fichier. Jeton illisible ⇒ traité comme absent.
- `HassTokenHelp` : marche à suivre en 6 étapes pour recréer un jeton (affichée si absent / illisible / refusé).

Sections principales :

| Section | Contenu |
|---|---|
| `Hass` | `Url`, `Token`, `DefaultSambaHassPath` (partage `\\IP\config`), `Keys` (éléments exportés), `Elements` (fichiers HA ↔ dépôt), `Themes`, `Z2m`, `Dashboards`, `StateKeys`/`StateFiles`, `ApplyBackupFolder`, `ApplyBackupKeep` |
| `Repositories` | `Private` / `Public` : `Path`, `ExportFolder`, `ElementsFolder`, `Excluded` (règles : `Dev/` dossier, `*.md` motif, chemin exact) |
| `Obfuscator` | `CliPath` (obfcli.exe), `WpfPath` (Obfuscator.exe), `ProjectFolder` (dossier `.obf`), `BackupFolder` |
| `Git` | `ExecutablePath` |
| `Health` | seuils du panel État : batteries, âge des sauvegardes, jetons dormants, nombre d'admins |

---

## Services (MHassnage.Core/Service)

| Service | Rôle |
|---|---|
| `HassClient` | WebSocket `/api/websocket`, authentification par jeton longue durée, commandes séquentielles. `HassAuthException` si jeton refusé, `HassCommandException` si erreur de commande. |
| `HassExportService` | Export complet : copie Samba (fichiers YAML, thèmes, Z2M), éléments WebSocket → `Elements/<clé>.jsonc` (listes indexées et triées par id pour des diffs stables), dashboards `lovelace/config` → YAML + un fichier par vue. Option « sans les states ». |
| `YamlService` | JSON ↔ YAML ; types préservés (chaînes ambiguës entre guillemets), aller-retour sans perte. |
| `GitService` | git CLI (profil et identifiants de l'utilisateur respectés, aucune invite) : état, lecture HEAD, commit sélectif **par fichier ou par bloc** (contenu reconstruit, `hash-object` + `update-index`), push, pull `--ff-only`. |
| `DiffService` | Diff côte à côte (DiffPlex), mots modifiés, blocs numérotés, repli du contexte, reconstruction « HEAD + blocs retenus ». |
| `ObfuscatorService` | Lance `obfcli publish/return/status --json [--dry-run]` (publish avec `--allow-suggestions`), lit le rapport JSON ; ouvre `Obfuscator.exe <projet> --publish/--return`. |
| `HassApplyService` | Écriture dans Hass : analyse dépôt ↔ Hass, sauvegarde préalable, écriture (Samba / `lovelace/config/save`), vérification de config, redémarrage, restauration. |
| `HassHealthService` | Relevé santé / sécurité en lecture seule (mises à jour, réparations, notifications, sauvegardes, batteries, indisponibles, comptes admin, MFA, jetons, URL externe). |
| `FolderBackupService` | Copie miroir (destination vidée puis remplacée), avec garde-fous (racine, dossiers imbriqués). |
| `SettingsService`, `SecretProtector` | Voir Configuration. |

### Contrat avec l'Obfuscator (obfcli)

Un objet JSON unique sur stdout, valeurs masquées, chemins pseudonymisés. Codes :
`0` OK · `1` erreur · `2` bloqué (fuite, refus, isolation, fichier modifié par l'IA) ·
`3` éléments à trancher · `4` conflits ou choix (rapatriement).
Le code de retour du processus prime sur le champ `exitCode` du JSON.
Les suggestions de vocabulaire ne bloquent pas (`--allow-suggestions`) ; seuls les « à trancher » bloquent.

---

## Application WPF — opérations (une par panel)

1. **État** (accueil, rechargé à chaque retour) : tableau de bord façon Power BI — cartes Hass, Obfuscator,
   dépôts privé et public ; cartes « À vérifier » (mises à jour, réparations/notifications, sécurité,
   sauvegardes Hass, batteries faibles, entités indisponibles) ; entités par domaine ; réglages manquants ;
   aide du jeton.
   Obfuscator : « privé → IA » = fichiers changés depuis la dernière publication ;
   « IA → privé » = fichiers modifiés par l'IA non rapatriés.
2. **Sauvegarder Hass** : export (case « Sauvegarder les states », décochée par défaut) → diff du dépôt privé → commit.
3. **Préparer pour l'IA** : export (option) → simulation `publish` → si tout passe, publication → diff/commit du
   dépôt public ; sinon liste des points et « Ouvrir dans l'Obfuscator » puis « Revérifier ».
4. **Récupérer le travail de l'IA** : pull du dépôt public (option) → simulation `return` → rapatriement ou
   Obfuscator. Deux onglets de diff (OREE_DOUS_HASS, OC_OBF_HASS), barre de commit commune avec une case par
   dépôt. Après chaque Commit/Push : copie miroir de `ProjectFolder` vers `Obfuscator:BackupFolder` (hors git).
5. **Appliquer dans Hass** :
   - *Analyser* : liste ce qui diffère entre dépôt privé et Hass (dashboards comparés sur le contenu, pas la
     mise en forme) ; élément modifié dans Hass depuis le dernier commit ⇒ décoché + avertissement.
   - Nouveaux `*.yaml` à la racine de l'export : nom côté Hass éditable (casse), correspondance enregistrée
     dans `Hass:Elements` après application.
   - *Appliquer* (confirmation) : sauvegarde de l'état actuel → écriture (fichiers avant `configuration.yaml`)
     → si config touchée : `check_config` (invalide ⇒ pas de redémarrage) puis redémarrage et attente ;
     thèmes seuls : `frontend.reload_themes` ; Z2M : message pour redémarrer l'add-on → export de contrôle.
   - *Restaurer la version précédente* : remet une sauvegarde (fichiers créés supprimés, dashboards remis),
     elle-même sauvegardée avant. Sauvegardes dans `%APPDATA%\MHassnage\HassBackups` (30 conservées).
   - Ne fait jamais : supprimer un fichier Hass absent du dépôt, écrire les states ni `Elements/*.jsonc`,
     écrire les fichiers `views/*` (seul le fichier principal du dashboard compte).

### Composants réutilisables

- `RepositoryView` / `RepositoryViewController` : liste des fichiers modifiés (cases, lettres M/A/D/R/?/!,
  « (partiel) », clic droit *Exclure ce fichier / son dossier*), diff, rafraîchissement.
- `DiffView` / `DiffViewController` : côte à côte, numéros de ligne, mots surlignés, en-têtes de bloc à cocher.
- `CommitBar` / `CommitBarViewController` : message, **Pull, Commit, Push, Commit & Push** sur les dépôts
  cochés ; « rien à commiter » / « rien à pousser » = information, pas erreur ; `AfterAction` optionnelle.
- `LogView` / `LogPanel` / `LogWindow` : console sélectionnable (Copier tout, Effacer, ouvrir en grand —
  revient dans son panel à la fermeture), défilement automatique, couleurs par préfixe
  (`OK`, `ABSENT`, `IGNORÉ`, `ERREUR`, étapes `▶`).
- `BusyOverlay` (voile + anneau pendant les traitements), `ConfirmWindow` (confirmation sombre),
  `DarkTitleBar` (barre de titre sombre via DWM).
- `MainViewController` + `NavigationController` : un panel par opération, créé une fois, vue choisie par
  `DataTemplate` ; version en haut à gauche, dans le titre et en bas à droite.

### Thème

Sombre partout (`Resources/Styles.xaml`) : fond `#1E2228`, panneaux `#272C34`, accent bleu Hass `#41BDF5`.
Styles implicites pour cases à cocher, barres de défilement, onglets ; en-têtes de panel à hauteur fixe
(`PanelHeader`, `PanelTitle`, `PanelDescription`) pour éviter les sauts entre panels.
Logo : roue dentée bleue avec un « M » (`mhassnage.svg`), PNG/ICO générés par `Resources/Generate-Logo.ps1`.

---

## Console `mhassnage-export`

`mhassnage-export [--output|-o <dossier>] [--no-states]` — sortie colorée.
Codes : `0` OK, `1` erreur, `2` jeton absent ou refusé (aide affichée), `3` réglages incomplets.

---

## Annexe — l'Obfuscator (application externe, sources non incluses)

Application de l'utilisateur, utilisée aussi hors MHassnage. Ses sources ne font **pas** partie de ce dépôt :
toute évolution se demande à part (prompt de spécification), MHassnage ne dépend que de son **CLI** et
de ses **arguments de lancement**. Version de référence : 1.0.4.3.

### Projets
| Projet | Cible | Sortie |
|---|---|---|
| Obfuscator.Core | net10.0 | bibliothèque (YamlDotNet, ProtectedData) |
| Obfuscator.Cli | net10.0-windows | **`obfcli.exe`** (Spectre.Console.Cli, options strictes : une option inconnue est refusée) |
| Obfuscator.Wpf | net10.0-windows | **`Obfuscator.exe`** |

### Projet Obfuscator (`OBF_HASS`)
Un dossier est un projet s'il contient **`.obf/`** ; le CLI remonte l'arborescence jusqu'à le trouver
(d'où : `Obfuscator:ProjectFolder` doit pointer exactement sur le dossier qui contient `.obf`).

```
<projet>/
  in/  out/                    source et cible par défaut (si aucun dossier n'est lié)
  .obf/project.jsonc           réglages (camelCase, commentaires tolérés)
  .obf/vocabulary.txt          termes masqués : « terme » ou « terme => Catégorie »
  .obf/mapping.enc             coffre chiffré pseudonyme ↔ valeur réelle
  .obf/exchange.enc            registre chiffré des échanges
  .obf/exchange/               contenus reçus par la cible (base des fusions à trois voies)
  .obf/findings.jsonc          dernier relevé de détection
```

Clés utiles de `project.jsonc` :
- `exchange.clearFolder` (source en clair = dépôt privé), `exchange.aiFolder` (cible = `OC_OBF_HASS\Hass`),
  `exchange.excluded` (chemins source écartés, `dossier/`), `exchange.ignored` (chemins cible ignorés au retour),
  `exchange.writeClaudeMd`.
- Décisions de l'utilisateur, persistées : `allowKeep` (valeurs gardées en clair / suggestions ignorées),
  `alwaysObfuscate` (toujours masquées, prioritaire), `lineExceptions` (`"/fichier|ligne#rang|valeur"`),
  `vocabulary.txt` (termes promus).
- `keptEntityDomains` (entity_id laissés en clair : sensor, binary_sensor, update, number, switch, button),
  `privateIp` (`Pseudonymize` | `Keep`), `tokenPrefix` (`TBX`), `vaultProtection` (`Low` = clé de
  l'application, transportable ; `High` = DPAPI, lié au compte Windows).

### Pseudonymisation
- Détecteurs : secrets, clés sensibles, réseau (IP, MAC, FQDN, URL…), identités, GUID, PEM, vocabulaire, regex.
  Relevé des résidus (base64, entropie, PEM) : **signale, ne masque jamais seul**.
- Jeton : `{PREFIXE}{NONCE}_{FAMILLE}_{NNNN}`, ex. `TBXA7_HOST_0001` (familles : IP, MAC, HOST, FQDN, URL, GUID,
  ID, MAIL, PERSON, ORG, SECRET, TOKEN, GEO, …). Une IP privée reste privée dans la même plage.
- entity_id Hass des domaines conservés : en clair, seuls les termes masqués qu'ils contiennent sont remplacés.
- La cible reçoit `AGENTS.md` (section régénérée : règles pour l'IA, familles, pseudonymes par fichier ;
  recopier les jetons à l'identique, `À_COMPLÉTER` pour une valeur inconnue) et `CLAUDE.md` (`@AGENTS.md`).

### Échanges
- **Publication** (clair → IA), actions : `Create`, `Update`, `Identical`, `Delete`, `Blocked` (fichier cible
  modifié par l'IA non rapatrié), `Legacy`, `Generated` (AGENTS.md/CLAUDE.md), `Refused`.
  Éléments à trancher : `Suspect` (résidu non décidé) et `VocabularyCandidate` (terme proposé).
- **Rapatriement** (IA → clair), actions : `Modified` (fusion à trois voies), `Created`, `Deleted` (proposée,
  décochée), `Refused`. Conflit = marqueurs `<<<<<<< en clair` / `=======` / `>>>>>>> IA` ; « choix » =
  fichier facultatif décoché par défaut.
- **Isolement** : projet, clair et IA distincts, non imbriqués, sans lien symbolique ni métadonnée git de la
  cible renvoyant au clair ; sinon bloquant.
- Alertes graves : `Leak`, `Conflict`, `Syntax`, `UnknownToken`, `ForeignToken`, `Placeholder`.

### CLI `obfcli`
| Commande | Options | Codes |
|---|---|---|
| `init [dossier]` | `-p network\|appconfig\|notes`, `--vault low\|high` | 0 |
| `scan [chemin]` | `--show-values` | 0 |
| `review [chemin]` | interactif | 0 |
| `obfuscate [chemin]` | `-o` | 0 |
| `verify [chemin]` | | 0, 2 résidus |
| `deobfuscate [fichier]` | `-o`, `-c` (presse-papiers) | 0, 1 |
| `status [chemin]` | `--json` | 0, 1 |
| `publish [chemin]` | `--json`, `--dry-run`, `--allow-suggestions` | 0, 1, 2, 3 |
| `return [chemin]` | `--json`, `--dry-run` | 0, 1, 2, 4 |

- Codes : 0 OK · 1 erreur · 2 bloqué · 3 à trancher · 4 conflits/choix ; le plus grave l'emporte (1 > 2 > 4 > 3).
- `publish`/`return` n'écrivent que si le code vaut 0 et sans `--dry-run` ; jamais de git.
  `--dry-run` travaille sur un instantané qui ne peut rien enregistrer.
- `--allow-suggestions` : les `VocabularyCandidate` restent listés mais ne bloquent pas.

Rapport `--json` (un seul objet UTF-8 sur stdout, aucune valeur réelle, chemins de la cible) :
```
{ command, dryRun, exitCode, applied, project, sourceFolder, targetFolder, canCommit,
  isolation: { safe, message },
  summary: { publish/status : create, update, delete, identical, blocked, refused, legacy, generated
             return : modified, created, deleted, refused },
  changes:  [ { action, path, added, removed, conflicts, warnings: [ { kind, serious, line, message } ] } ],
  pending:  [ { kind: Suspect|VocabularyCandidate, value (masquée), files, occurrences, rule } ],   // publish
  conflicts:[ { path, count, reason: Conflict|Choice } ],                                         // return
  outcome:  { created, modified, deleted, skipped } | null,
  errors:   [ ... ],
  status:   { hasExchanged, targetPending, sourcePending, targetModified, targetCreated, targetDeleted,
              sourceChanged, sourceAdded, lastObfuscation, lastReturn } | null }               // status
```
`sourcePending` = fichiers du clair changés depuis la dernière publication ; `targetPending` = fichiers
modifiés / créés / supprimés par l'IA non rapatriés.

### WPF `Obfuscator.exe`
`Obfuscator.exe [dossierProjet] [--publish | --return]` : ouvre le projet (jamais de création) puis la liste
d'obfuscation (`--publish`) ou de rapatriement avec le premier conflit (`--return`) ; rien n'est écrit sans
confirmation. **Instance unique** par utilisateur (mutex + canal nommé) : un second lancement avec arguments
transmet la demande à la fenêtre ouverte et la met au premier plan ; refusée si une opération est en cours.

---

## Points d'attention pour un agent

- Ne jamais committer de valeur réelle de Hass ni de jeton ; ne rien écrire dans le dépôt public sauf via l'Obfuscator.
- Garder la logique dans `MHassnage.Core` (sans dépendance WPF) ; les ViewControllers orchestrent, les vues lient.
- Les sources de l'Obfuscator ne font pas partie de ce projet : évolutions demandées à part ; tout ce qu'il faut
  en savoir est dans l'annexe ci-dessus.
- Fichiers de code en UTF-8 avec BOM, fins de ligne CRLF (attention aux remplacements de texte en script).
- Pendant que `MHassnage.exe` ou Visual Studio tourne, compiler vers un autre dossier :
  `dotnet build MHassnage.slnx -p:BaseOutputPath=<temp>\`.
