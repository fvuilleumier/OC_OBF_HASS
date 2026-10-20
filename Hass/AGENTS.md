<!-- obf:début · section régénérée par Obfuscator à chaque obfuscation : ne pas la modifier -->
# Fichiers pseudonymisés

Les fichiers de ce dépôt ont été pseudonymisés par Obfuscator avant de vous être
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
| `666304389bac4f6e90d0c160ad98c6b6` | un identifiant global | 6879 |
| `900000001` | un identifiant d'enregistrement, la valeur d'une clé nommée ID ou dont le nom contient le mot ID (unique_id, config_entry_id) ; un numéro reste un numéro | 5461 |
| `TBX9F_ORG_0001` | une raison sociale, un nom de projet ou un terme interne | 937 |
| `02:00:00:00:00:01` | une adresse MAC | 251 |
| `192.168.23.10` | une adresse IPv4, ou son début quand c'est le réseau qui est masqué ; une adresse privée reste une adresse privée de la même plage, une adresse publique devient une adresse de la plage d'essai 198.18.0.0/15, et l'appartenance au même sous-réseau comme la partie hôte sont préservées | 123 |
| `TBX9F_SECRET_0001` | un secret : mot de passe, clé d'API, jeton d'accès | 57 |
| `host0001.example.invalid` | un nom de machine pleinement qualifié | 17 |
| `TBX9F_CRED_0001` | un identifiant de connexion nominatif | 10 |
| `TBX9F_VAL_0001` | une valeur sensible non classée | 7 |
| `41001` | un port d'écoute non standard ; les ports de service normalisés sont conservés | 6 |
| `270` | une coordonnée géographique — latitude, longitude ou altitude ; le nombre est fictif, de même forme, et ne garde aucune proximité avec le lieu réel | 4 |

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
| `Automations.yaml` | Identifiants GUID ×121, Identifiants (clés ID) ×60, Secrets et mots de passe ×16, Noms internes (société, projets) ×4 |
| `Configurations.yaml` | Adresses IPv4 ×3, Ports non standard ×1, Identifiants (clés ID) ×1 |
| `Dashboards/L'Oree des dous - pieces.yaml` | Noms internes (société, projets) ×23 |
| `Dashboards/L'Orée des dous.yaml` | Noms internes (société, projets) ×48, Identifiants GUID ×6, Identifiants (clés ID) ×3, Adresses IPv4 ×3, Comptes de connexion ×2 |
| `Dashboards/Views/L'Oree des dous - pieces/01 - maison-des-dous.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/02 - couloir-premier.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/03 - chambre-TBX9F_ORG_0002.yaml` | Noms internes (société, projets) ×11 |
| `Dashboards/Views/L'Oree des dous - pieces/04 - chambre-TBX9F_ORG_0003-TBX9F_ORG_0004.yaml` | Noms internes (société, projets) ×6 |
| `Dashboards/Views/L'Oree des dous - pieces/05 - chambre-TBX9F_ORG_0005.yaml` | Noms internes (société, projets) ×6 |
| `Dashboards/Views/L'Oree des dous - pieces/06 - salle-de-jeu.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/07 - salon.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/08 - salle-a-manger.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/09 - cuisine.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/10 - toilettes-1er.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/11 - salle-de-bain-1er.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/12 - couloir-rez.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/13 - serveur-bis.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/14 - buanderie.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/15 - cave.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/16 - bureau.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/17 - bureau-cr.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/18 - salle-de-bain-rez.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/19 - galetas.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/20 - atelier.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/21 - garage.yaml` | aucun |
| `Dashboards/Views/L'Oree des dous - pieces/22 - jardin.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/01 - Home.yaml` | Identifiants GUID ×2, Noms internes (société, projets) ×2 |
| `Dashboards/Views/L'Orée des dous/02 - maison.yaml` | Noms internes (société, projets) ×16, Identifiants GUID ×4 |
| `Dashboards/Views/L'Orée des dous/03 - m.yaml` | Noms internes (société, projets) ×10, Identifiants (clés ID) ×2 |
| `Dashboards/Views/L'Orée des dous/04 - energy.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/05 - graphes-and-consommation.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/06 - sante-systeme.yaml` | Noms internes (société, projets) ×11, Identifiants (clés ID) ×1 |
| `Dashboards/Views/L'Orée des dous/07 - appareils-oublies.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/08 - automatisations.yaml` | Noms internes (société, projets) ×2 |
| `Dashboards/Views/L'Orée des dous/09 - boutons.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/10 - chauffage.yaml` | Noms internes (société, projets) ×1 |
| `Dashboards/Views/L'Orée des dous/11 - lavette.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/12 - ducon.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/13 - natel-de-TBX9F_ORG_0001.yaml` | Noms internes (société, projets) ×5, Comptes de connexion ×2 |
| `Dashboards/Views/L'Orée des dous/14 - tv-des-dous.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/15 - yoto.yaml` | Noms internes (société, projets) ×1 |
| `Dashboards/Views/L'Orée des dous/16 - ups.yaml` | aucun |
| `Dashboards/Views/L'Orée des dous/17 - tests.yaml` | Adresses IPv4 ×3 |
| `Dashboards/Views/Pieces/Appareils.yaml` | aucun |
| `Dashboards/Views/Pieces/AppareilsNotSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Chauffage.yaml` | Noms internes (société, projets) ×9 |
| `Dashboards/Views/Pieces/Conditions.yaml` | aucun |
| `Dashboards/Views/Pieces/Energie.yaml` | aucun |
| `Dashboards/Views/Pieces/EnergieDoesntSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Interactions.yaml` | aucun |
| `Dashboards/Views/Pieces/InteractionsNotSetYet.yaml` | aucun |
| `Dashboards/Views/Pieces/Navigation.yaml` | aucun |
| `Dashboards/Views/PlanInteractif.yaml` | Noms internes (société, projets) ×16, Identifiants GUID ×4 |
| `Dashboards/Views/PlanInteractif_plan.yaml` | Noms internes (société, projets) ×8, Identifiants GUID ×4 |
| `Dashboards/Views/PlanInteractif_tile.yaml` | Noms internes (société, projets) ×8 |
| `Elements/Areas.jsonc` | Noms internes (société, projets) ×6 |
| `Elements/Config.jsonc` | Coordonnées géographiques ×2, Machines (nom qualifié) ×1, Ports non standard ×1 |
| `Elements/Devices.jsonc` | Identifiants GUID ×1153, Identifiants (clés ID) ×221, Adresses IPv4 ×108, Adresses MAC ×59, Secrets et mots de passe ×39, Noms internes (société, projets) ×12, Autres valeurs sensibles ×6, Machines (nom qualifié) ×1 |
| `Elements/Entities.jsonc` | Identifiants GUID ×5581, Identifiants (clés ID) ×3874, Noms internes (société, projets) ×571, Adresses MAC ×187, Secrets et mots de passe ×1 |
| `Elements/Floors.jsonc` | aucun |
| `Elements/labels.jsonc` | aucun |
| `Elements/locale.jsonc` | aucun |
| `Elements/panels.jsonc` | Autres valeurs sensibles ×1 |
| `Elements/states.jsonc` | Identifiants (clés ID) ×1295, Noms internes (société, projets) ×155, Machines (nom qualifié) ×13, Comptes de connexion ×6, Adresses MAC ×5, Adresses IPv4 ×4, Identifiants GUID ×3, Coordonnées géographiques ×2 |
| `Elements/themes.jsonc` | aucun |
| `Elements/user.jsonc` | Identifiants (clés ID) ×2, Identifiants GUID ×1 |
| `Elements/userData.jsonc` | aucun |
| `Influxdb.yaml` | Ports non standard ×1 |
| `README.md` | aucun |
| `Tests/generatedServerLovelace.yaml` | aucun |
| `Tests/serverLovelace.yaml` | aucun |
| `Tests/Serverur.yaml` | aucun |
| `Tests/tests_lovelace.yaml` | aucun |
| `Themes/DarkTheme.yaml` | aucun |
| `Themes/oneDarkPro.yaml` | aucun |
| `Themes/OreeDesDousDark.yaml` | aucun |
| `UtilityMeters.yaml` | aucun |
| `Z2m/configuration.yaml` | Noms internes (société, projets) ×6, Ports non standard ×3, Identifiants (clés ID) ×2, Adresses IPv4 ×2, Secrets et mots de passe ×1 |
| `Z2m/state.json` | Machines (nom qualifié) ×2 |

<!-- obf:fin -->
