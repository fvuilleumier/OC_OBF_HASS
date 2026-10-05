<!-- tebiobf:début · section régénérée par TebiObfuscator à chaque obfuscation : ne pas la modifier -->
# Fichiers pseudonymisés

Les fichiers de ce dépôt ont été pseudonymisés par TebiObfuscator avant de vous être
confiés : les valeurs identifiantes — noms, adresses, secrets, coordonnées — ont été
remplacées par des pseudonymes ; tout le reste est authentique. Vos modifications seront
restaurées en clair dans le dépôt d'origine, puis relues avec git avant d'être appliquées.

## Consignes de travail

- Travaillez uniquement dans ce dépôt. Les valeurs réelles n'y figurent pas et ne
  doivent pas être recherchées ailleurs.
- Un même pseudonyme désigne une même entité, dans tous les fichiers. Recopiez-le à
  l'identique — casse, séparateurs, chiffres — quand vous le réutilisez.
- N'inventez pas de pseudonyme pour une entité existante. Une valeur réelle que vous ne
  connaissez pas — un mot de passe, une adresse, une coordonnée — s'écrit `À_COMPLÉTER` :
  elle sera signalée, pour être complétée.
- Une adresse nouvelle dans un sous-réseau existant — même début qu'une adresse des
  fichiers — sera restituée dans le vrai réseau : vous pouvez en attribuer.
- Les coordonnées géographiques sont fictives : n'en déduisez ni lieu ni distance.
- Ne renommez pas les fichiers, ne déplacez pas de dossier et ne reformatez pas un
  fichier entier : chaque ligne changée apparaîtra au contrôle git du dépôt d'origine.
  Gardez l'indentation et les fins de ligne.
- Vous pouvez créer des fichiers : leur nom sera restitué de la même façon. Une
  suppression sera proposée, jamais appliquée d'office.
- Ne modifiez pas cette section : elle est régénérée à chaque obfuscation.

## Comment lire ces fichiers

- Deux occurrences d'un même pseudonyme désignent **la même entité réelle**,
  y compris d'un fichier à l'autre du même lot.
- Deux pseudonymes différents désignent **deux entités différentes**.
- Les pseudonymes typés restent syntaxiquement valides : une adresse reste
  une adresse, une adresse de courrier reste une adresse de courrier.
- Un pseudonyme ne contient aucune information sur la valeur d'origine :
  il est inutile d'essayer de la deviner.

## Familles de pseudonymes présentes

| Forme | Signifie | Occurrences |
|---|---|---|
| `TBX7D_ORG_0001` | une raison sociale, un nom de projet ou un terme interne | 252 |
| `TBX7D_SECRET_0001` | un secret : mot de passe, clé d'API, jeton d'accès | 167 |
| `900000001` | un identifiant d'enregistrement, la valeur d'une clé nommée ID ; un numéro reste un numéro | 55 |
| `host0001.example.invalid` | un nom de machine pleinement qualifié | 14 |
| `198.18.0.188` | une adresse IPv4, ou son début quand c'est le réseau qui est masqué ; une adresse privée reste une adresse privée de la même plage, une adresse publique devient une adresse de la plage d'essai 198.18.0.0/15, et l'appartenance au même sous-réseau comme la partie hôte sont préservées | 8 |
| `41001` | un port d'écoute non standard ; les ports de service normalisés sont conservés | 4 |
| `TBX7D_CRED_0001` | un identifiant de connexion nominatif | 3 |

## Ce qui est authentique

- La structure du fichier : clés, commentaires, indentation, ordre des sections.
- Les masques de sous-réseau et masques génériques.
- Les adresses à signification protocolaire (bouclage, diffusion, multidiffusion)
  et les résolveurs publics.
- Les noms de constructeurs, de produits, de protocoles et les numéros de version.
- Les comptes techniques et les rôles (`admin`, `root`, `SYSTEM`…), qui ne
  désignent personne en particulier.
- Les ports de service normalisés (22, 443…), les chemins d'URL et les
  paramètres de configuration.
- **La structure des adresses IPv4.** Les réseaux sont fictifs, mais une
  adresse privée a été remplacée par une adresse privée de la même plage
  (10.x, 172.16–31.x, 192.168.x) et une adresse publique par une adresse de
  198.18.0.0/15 ; deux machines d'un même sous-réseau le restent, et la partie
  hôte (.1, .254…) est d'origine.

## Fichiers publiés

| Fichier | Pseudonymes |
|---|---|
| `Automations.yaml` | Secrets et mots de passe ×158, Identifiants (clés ID) ×55, Noms internes (société, projets) ×4 |
| `Configurations.yaml` | Adresses IPv4 ×3, Secrets et mots de passe ×2 |
| `Dashboards/L'Oree des dous - pieces.yaml` | Noms internes (société, projets) ×23 |
| `Dashboards/L'Orée des dous.yaml` | Noms internes (société, projets) ×93, Machines (nom qualifié) ×6, Adresses IPv4 ×3, Secrets et mots de passe ×2, Comptes de connexion ×2 |
| `Dashboards/Views/Pieces/Appareils.yaml` | aucun |
| `Dashboards/Views/Pieces/AppareilsNotSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Chauffage.yaml` | Noms internes (société, projets) ×9 |
| `Dashboards/Views/Pieces/Conditions.yaml` | aucun |
| `Dashboards/Views/Pieces/Energie.yaml` | aucun |
| `Dashboards/Views/Pieces/EnergieDoesntSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Interactions.yaml` | aucun |
| `Dashboards/Views/Pieces/InteractionsNotSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Navigation.yaml` | aucun |
| `Dashboards/Views/PlanInteractif.yaml` | Noms internes (société, projets) ×58, Machines (nom qualifié) ×4, Secrets et mots de passe ×2 |
| `Dashboards/Views/PlanInteractif_plan.yaml` | Noms internes (société, projets) ×29, Machines (nom qualifié) ×3, Secrets et mots de passe ×2 |
| `Dashboards/Views/PlanInteractif_tile.yaml` | Noms internes (société, projets) ×29 |
| `Influxdb.yaml` | Ports non standard ×1 |
| `Themes/DarkTheme.yaml` | aucun |
| `Themes/oneDarkPro.yaml` | aucun |
| `UtilityMeters.yaml` | aucun |
| `Z2m/configuration.yaml` | Noms internes (société, projets) ×7, Ports non standard ×3, Adresses IPv4 ×2, Comptes de connexion ×1, Secrets et mots de passe ×1 |
| `Z2m/state.json` | Machines (nom qualifié) ×1 |

<!-- tebiobf:fin -->

# AGENTS.md

L'objectif du projet est le suivant : Maintenir et améliorer le serveur home assistant d'une villa en vue d'optimiser les consommation de la villa et d'ergonomie d'utilisation de l'application Home assistant (hass)
Il n'y a ni code, ni outillage de build, de test ou de lint — ne cherchez pas et n'exécutez pas `npm`, `make`, `pytest` ou toute commande similaire.

## Structure
- `README.md` — description du dépôt en une ligne.

## Working here
- Conservez tout texte descriptif en français, en cohérence avec les fichiers existants.
- Toujours demander avant de modifier
- Toujours demander avant de commiter / adder / pusher dans git
- On est en mode "Ask" et si je te demande de modifier, tu modifies mais ne modifie pas toi même les fichiers
- Tutoies-moi, on parle français
