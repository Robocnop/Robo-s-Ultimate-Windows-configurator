# Audit du code (v2.6.2)

Date : 05/11/2026. Périmètre : `src/RoboConfigurator/Core/*`, `MainWindow.xaml(.cs)`, `App.xaml.cs`, `StepItem.cs`, `build.ps1`, `.github/workflows/build.yml`.
Audit fait **en lisant le code uniquement** : rien n'a été lancé sur un vrai PC. Les points marqués *(à confirmer sur PC)* sont des déductions à vérifier sur une machine réelle.

Priorité : **P1** = à corriger en premier, **P2** = utile, **P3** = confort.
Chaque point a un identifiant (B1, R2, S3...) pour pouvoir dire « OK pour B1, B3, S1 ; pas R5 ».

## Suivi (phase 2, branche `audit-improvements`)

Corrigé dans le code et vérifié par la compilation, les tests unitaires, le test de fumée et des passages en
`-Simulation`. **Rien n'a été testé sur un vrai PC en dehors de la simulation** : voir la liste « À valider à la
main » dans `AVANCEMENT.md`.

| Point | Commit | État |
|---|---|---|
| M1, M2 | 5ff1174 | Fait : projet xUnit `tests\RoboConfigurator.Tests`, test de fumée dans `tests\smoke.ps1`, `build.ps1 -Test` |
| S1, S4 | 65d3c47 | Fait |
| B1, B2 | dfbfab7 | Fait |
| B3, R4, R5, S2 | 1f57d8e | Fait. Q1 : signatures Ninite (Secure By Design Inc.) et Office (Microsoft Corporation) vérifiées ; pas de SHA-256 figé (emplacement prévu, vide) |
| R1 | 8831e37 | Fait |
| R2 | 85f701b | Fait |
| B4, R7 | 74a25d9 | Fait |
| B5 | b6718b2 | Fait |
| R6 | 30b7c16 | Fait |
| R3 | 7d9bec2 | Fait |
| S3 | 17674d3 | Fait (DPAPI, portée machine) |
| S5 | 643fa28 | Fait |
| U2 | 1da9b45 | Fait |
| U3 | 0605baf | Fait |
| U4 | 0ed8cab | Fait |
| U1 | 62a3792 | Fait (Q5) |
| L1, L2 | e8d69db | Fait |
| L3 | a6351d5 | Fait |
| S7 | 98506b9 | Fait |
| S6 | aaccb38 | Fait |
| M3 | eda748b | En partie : explorateur et `<delete>` corrigés. Découpage d'`Engine.cs` volontairement non fait ; coupure de `ReadIni` au « ; » laissée telle quelle (format CrapFixer, aucun cas connu) |
| M4 | 06ed9e7 | Fait : limite de 4 Mo, `packages.lock.json`. Actions GitHub toujours épinglées par étiquette |
| Section 8 | 5d686f6, a4d828f | Fait : `-SimulateFail`, vérification des liens, simulation détaillée d'Edge et de la barre des tâches |
| R8 | 796d579 | Fait : avertissement (Q6) |
| U6 CrapFixer | 73ec264 | Fait (Q3) : paquets provisionnés retirés, sans faire échouer l'étape |
| U6 Barre des tâches | — | **Non changé** (Q2) : `LockedStartLayout = 1` gardé tant que l'effet n'est pas vérifié sur un vrai PC |
| U5 | — | Rien à corriger |
| — | 7fff9e5 | Trouvé en simulation : le bandeau d'erreur cite aussi les étapes non faites ou à finir après redémarrage |

---

## 1. Bugs probables

### B1 — Une étape terminée « avec avertissement » est marquée comme faite (P1)
- **Fichier** : `Core/Engine.cs:97-99`
- **Problème** : `_state.MarkDone(step)` est appelé avant de regarder si l'étape a rendu un avertissement. L'étape Activation rend un avertissement quand Windows **n'est pas activé** (« Script d'activation indisponible », « Windows ne semble pas activé ») : elle est quand même enregistrée dans `etapes-terminees.txt`.
- **Risque** : au lancement suivant, l'activation est affichée « Déjà faite » et n'est jamais réessayée.
- **Correction** : distinguer « fait, mais à vérifier » (Barre des tâches appliquée au redémarrage → marquée faite) de « pas fait » (activation non obtenue → non marquée, réessayée). L'étape rend un résultat `{ Done | DoneWithWarning | NotDone }` au lieu d'une simple chaîne.

### B2 — Le rapport du Bureau affiche « OK » pour une étape à vérifier (P1)
- **Fichier** : `Core/Engine.cs:699-707`
- **Problème** : le rapport ne connaît que ignorée / EN ÉCHEC / à terminer après le redémarrage / OK / non faite. Une étape en avertissement (activation non obtenue, barre des tâches pas encore appliquée, Pro conservé au lieu de Famille) sort en **« OK »**, sans message. Le nom du PC, le nettoyage et les étapes faites lors d'un lancement précédent n'y figurent pas.
- **Risque** : le rapport dit que tout va bien alors que Windows n'est pas activé.
- **Correction** : écrire pour chaque étape l'état réel de l'appli (`StepItem.Status`) et son détail, par exemple `Activation : À VÉRIFIER - Windows ne semble pas activé`. Ajouter les lignes Nom du PC et Nettoyage, et « déjà faite (lancement précédent) ».

### B3 — Un fichier intégré extrait à moitié est réutilisé tel quel (P1)
- **Fichier** : `Core/Payloads.cs:56-69`
- **Problème** : un fichier intégré à l'exe est écrit directement à sa place finale (`File.Create(path)`), sans `.part`. Si l'appli est coupée pendant l'extraction, le fichier tronqué reste. Au lancement suivant, `Length > 0` suffit pour le déclarer « déjà présent ».
- **Risque** : un installateur corrompu est lancé à chaque reprise, avec une erreur incompréhensible.
- **Correction** : extraire dans `.part` puis renommer, comme pour les téléchargements, et vérifier le fichier présent (S2) au lieu de sa seule taille.

### B4 — Le code de retour de Ninite et d'OfficeSetup est ignoré (P2)
- **Fichiers** : `Core/Engine.cs:169-175` (Ninite), `Core/Engine.cs:549-557` (Office)
- **Problème** : Ninite est marquée faite quel que soit son code de retour (fenêtre fermée par la croix, erreur réseau de Ninite...). Pour Office, si `OfficeSetup.exe` échoue tout de suite, l'appli attend quand même **60 minutes** avant de déclarer l'échec.
- **Risque** : Ninite « Terminée » sans rien d'installé ; une heure perdue sur Office.
- **Correction** : Ninite avec un code non nul → « À vérifier », non marquée faite. Office : `OfficeSetup.exe` terminé avec un code non nul et Word absent → échec immédiat avec le code. *(Codes de sortie réels de Ninite et d'OfficeSetup à confirmer sur PC.)*

### B5 — Une erreur hors étape bloque l'écran de fin (P2)
- **Fichiers** : `MainWindow.xaml.cs:329-333`, `App.xaml.cs:20-26`
- **Problème** : `Engine.Run` n'attrape les erreurs qu'à l'intérieur de chaque étape. Une exception ailleurs (lecture de `etapes-terminees.txt`, `File.Delete(_configFile)`, journal inaccessible, voir R6) remonte jusqu'à `OnStartClick`. La boîte d'erreur s'affiche, mais `ShowSummary` n'est jamais appelé : l'écran reste sur le déroulement sans bouton Fermer, Redémarrer ni Ouvrir le dossier. `AppDomain.UnhandledException` n'est pas suivi non plus.
- **Risque** : l'utilisateur ne sait pas où en est la configuration.
- **Correction** : un `try/catch` global dans `Engine.Run` qui journalise l'erreur, marque l'étape en cours en échec et rend quand même un `RunResult` ; l'écran de fin s'affiche dans tous les cas.

---

## 2. Robustesse

### R1 — Aucun délai maximum sur les programmes lancés (P1)
- **Fichier** : `Core/SystemInfo.cs:395-445` (`Run`, `RunVisible`)
- **Problème** : `WaitForExit()` attend sans limite. Ça concerne le désinstalleur d'Edge (via la copie de `cmd.exe`), `reg.exe export`, `slmgr /ipk` et `/ato` (qui contacte Microsoft), `changepk.exe`, PowerShell (`Get-AppxPackage`, `Remove-AppxPackage`), le script MAS, et `OfficeSetup.exe` avant la boucle de 60 minutes.
- **Risque** : une étape bloquée bloque toute la configuration, sans message, et le redémarrage n'arrive jamais. La seule issue est de fermer l'appli (R2).
- **Correction** : un paramètre `timeout` dans `Run` / `RunVisible`. Une fois le délai dépassé : journal, arrêt du processus, exception claire. Valeurs proposées : `reg`, `slmgr /ipk`, PowerShell 2 min ; `slmgr /ato` 5 min ; désinstalleur d'Edge 10 min ; `changepk` et MAS 15 min ; `OfficeSetup` compris dans les 60 minutes. Ninite garde une attente illimitée (l'utilisateur doit cliquer sur Close).

### R2 — Fermer l'appli pendant une étape laisse des fichiers derrière (P1)
- **Fichiers** : `MainWindow.xaml.cs:453-469`, `Core/Engine.cs:258-310`
- **Problème** : « Quitter » appelle `Application.Current.Shutdown()`. Le thread de l'étape est tué net et ses blocs `finally` ne s'exécutent pas. Pendant l'étape Edge, `C:\Windows\SystemTemp\dllhost.exe` (copie de `cmd.exe`) et le faux `SystemApps\...\MicrosoftEdge.exe` vide restent sur le disque. Une coupure de courant a le même effet.
- **Risque** : un faux `dllhost.exe` dans un dossier de Windows, qu'un antivirus peut signaler. Un `MicrosoftEdge.exe` vide qui peut gêner une réinstallation d'Edge *(à confirmer sur PC)*. Les programmes enfants (OfficeSetup, MAS) continuent sans être suivis.
- **Correction** : (1) au début de l'étape Edge et au lancement de l'appli, supprimer ces deux fichiers s'ils viennent de nous (`dllhost.exe` dans SystemTemp identique à `cmd.exe`, `MicrosoftEdge.exe` de 0 octet) ; (2) une annulation propre entre deux étapes (U1).

### R3 — Le fichier de reprise n'est pas écrit de façon atomique (P2)
- **Fichier** : `Core/RunConfig.cs:317-324`
- **Problème** : `File.WriteAllText` écrit directement par-dessus `configuration.json`. Une coupure pendant l'écriture laisse un fichier vide ou tronqué. Au lancement suivant, le bandeau dit « Configuration précédente illisible » et l'appli repart des options par défaut. Les choix sont perdus sans que ce soit dit clairement, et le fichier abîmé reste.
- **Risque** : faible (le fichier est petit), mais la reprise se fait avec les réglages par défaut (sélection Ninite par défaut, clé vide...).
- **Correction** : écrire dans `configuration.json.tmp`, puis `File.Replace` (ou `File.Move` si le fichier n'existe pas). Un fichier illisible est renommé en `configuration.json.illisible`, journalisé et signalé dans le bandeau. `etapes-terminees.txt` (ajout de lignes) est déjà assez sûr ; il suffit d'ignorer les lignes vides ou partielles.

### R4 — Téléchargements : attente fixe, fin de flux non vérifiée (P2)
- **Fichier** : `Core/Payloads.cs:72-116`
- **Problème** :
  - L'attente entre les essais est fixe (5 s), pas progressive, et elle a lieu aussi **après** le dernier essai.
  - Le nombre d'octets reçus n'est pas comparé à `Content-Length`.
  - Rien ne vérifie que le fichier est bien un exe ou un zip : une page HTML d'erreur du Nextcloud servie avec le code 200 (lien expiré, page de connexion) serait acceptée.
  - L'URL et le code HTTP ne sont pas dans le journal.
- **Risque** : une erreur incompréhensible plus loin (« archive invalide », « n'est pas une application Win32 »).
- **Correction** :
  - Attendre 5 s puis 15 s, et ne pas attendre après le 3ᵉ essai.
  - Comparer la taille reçue à `Content-Length`.
  - Vérifier le début du fichier : `MZ` pour un exe, `PK` pour un zip, du texte pour le `.cmd`.
  - Journaliser l'URL, le code HTTP, la taille attendue et la taille reçue.

### R5 — Messages d'erreur sans connexion Internet (P2)
- **Fichier** : `Core/Payloads.cs:84-92`
- **Problème** : sans réseau, chaque fichier fait 3 essais, puis l'erreur affichée est le message brut de .NET (« Le nom distant n'a pas pu être résolu »).
- **Risque** : peu clair pour quelqu'un qui ne connaît pas l'outil.
- **Correction** : reconnaître les cas courants (DNS, délai dépassé, 404, 403) et afficher « Pas de connexion Internet », « Lien de téléchargement introuvable (404) », etc. Le message technique reste dans le journal.

### R6 — Le journal peut faire échouer une étape (P3)
- **Fichier** : `Core/Log.cs:81-85`
- **Problème** : seule `IOException` est ignorée à l'écriture du journal. Une `UnauthorizedAccessException` (fichier en lecture seule, antivirus) remonte dans l'étape en cours, qui passe en échec, ou hors étape (B5).
- **Correction** : ignorer toute exception d'écriture du journal et le signaler une seule fois dans l'interface.

### R7 — Étape Office : la fin de l'installation est devinée (P2)
- **Fichier** : `Core/Engine.cs:536-560`
- **Problème** : l'installation est considérée finie quand `WINWORD.EXE` existe et qu'aucun processus `OfficeSetup` ne tourne. Le travail réel est fait par Click-to-Run, et `WINWORD.EXE` peut apparaître avant la fin *(à confirmer sur PC)*.
- **Risque** : l'activation d'Office (Ohook) démarre pendant que l'installation continue.
- **Correction** : attendre aussi la fin des processus `OfficeC2RClient`, et vérifier `HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration` (`VersionToReport`). Toutes les 5 minutes, journaliser ce qui est attendu (processus présents, fichiers trouvés).

### R8 — Compte administrateur différent de la session ouverte (P3, à décider)
- **Fichiers** : `Core/Engine.cs:395-482` (Barre des tâches), `Core/CrapFixer.cs` (tweaks `HKCU`), `Core/Engine.cs:712` (rapport sur le Bureau)
- **Problème** : si l'appli est lancée depuis une session standard avec les identifiants d'un **autre** compte administrateur (UAC), `HKCU`, `%APPDATA%` et le Bureau sont ceux de ce compte, pas ceux de l'utilisateur.
- **Risque** : tweaks, épingles et rapport sont appliqués au mauvais profil, sans erreur.
- **Correction** : au minimum, détecter le cas (utilisateur du processus différent du propriétaire de `explorer.exe`), le journaliser et afficher un avertissement au démarrage.

---

## 3. Sécurité

### S1 — `C:\RoboConfigurator` est modifiable par tous les utilisateurs du PC (P1)
- **Fichier** : `MainWindow.xaml.cs:44-47` (création du dossier)
- **Problème** : le dossier est créé à la racine de `C:\` et hérite de ses droits. La racine donne **Modification** aux « Utilisateurs authentifiés » sur ses sous-dossiers (vérifié avec `icacls C:\` sur ce PC : `Utilisateurs authentifiés:(OI)(CI)(IO)(M)`). Un compte non administrateur peut donc :
  - lire `configuration.json`, qui contient la **clé Windows en clair** ;
  - remplacer `OfficeSetup.exe`, `Ninite*.exe`, `MAS_AIO.cmd` ou `CrapFixer.zip` restés après un échec. Au lancement suivant, l'appli les trouve « déjà présents » et les **exécute en administrateur** sans vérification ;
  - créer une jonction de dossier dans `C:\RoboConfigurator\CrapFixer\` (S4).
- **Risque** : élévation de privilèges locale et fuite de la clé. Faible sur un PC neuf à un seul compte, réel sur un PC partagé ou quand le dossier est gardé après un échec.
- **Correction** : à la création du dossier, réduire ses droits à Administrateurs + SYSTEM, sans héritage (`DirectorySecurity`, `SetAccessRuleProtection(true, false)`). Si le dossier existe déjà avec d'autres droits, les corriger et le noter dans le journal.

### S2 — Aucune vérification d'intégrité des fichiers téléchargés (P1)
- **Fichier** : `Core/Payloads.cs`
- **Problème** : aucun hash, aucune signature. Pourtant `CrapFixer.zip` pilote des écritures et des suppressions dans `HKLM`, `MAS_AIO.cmd` est un script lancé en administrateur, et les exe sont lancés en administrateur.
- **Risque** : un fichier remplacé (sur le Nextcloud, sur le disque via S1, ou en transit) est exécuté sans contrôle.
- **Correction proposée** :
  - `Ninite*.exe` et `OfficeSetup.exe` : vérifier la **signature Authenticode** (`WinVerifyTrust`, API de Windows, sans dépendance) et le signataire attendu (Microsoft Corporation pour Office ; celui de Ninite est à relever sur un vrai fichier). Les installateurs Ninite personnalisés sont régénérés par ninite.com : seule la signature est possible pour eux.
  - `CrapFixer.zip` et `MAS_AIO.cmd` : un **SHA-256 attendu** écrit dans le code. Contrepartie : il faut publier un nouvel exe à chaque mise à jour de ces fichiers sur le Nextcloud (voir Q1).
  - Un fichier « déjà présent » est revérifié avant usage ; s'il est invalide, il est supprimé puis retéléchargé.

### S3 — La clé Windows reste en clair dans le fichier de reprise (P2)
- **Fichiers** : `Core/RunConfig.cs:286`, `Core/Engine.cs:122`
- **Ce qui va déjà bien** : la clé n'apparaît **pas** en clair dans le journal. `Log.Write` la masque (`XXXXX-XXXXX-XXXXX-XXXXX-12345`), et ce masquage couvre aussi le journal affiché dans l'interface et la sortie de `slmgr`. Elle n'apparaît pas non plus dans le rapport : seule la description de la clé OEM y figure. `changepk` est journalisé avec `<clé>`.
- **Problème** : `configuration.json` garde la clé en clair tant qu'une étape a échoué ou reste à terminer après le redémarrage, parfois pendant des jours. Une clé passée par `-WindowsKey` est aussi visible dans la ligne de commande du processus (et dans celle de `slmgr /ipk`) tant qu'il tourne.
- **Correction** : chiffrer la clé dans le fichier avec **DPAPI** (`ProtectedData.Protect`, portée machine, déjà dans .NET 4.7.2) et la retirer du fichier dès que l'étape Activation est faite. Rien de simple pour la ligne de commande de `slmgr` : le documenter. Ajouter un test qui vérifie que `Save` n'écrit jamais la clé en clair.

### S4 — Le nettoyage suit les jonctions de dossiers (P1, lié à S1)
- **Fichier** : `Core/Engine.cs:793-809` (`DeleteFolder`)
- **Problème** : sur .NET Framework, `Directory.GetFiles(dir, "*", AllDirectories)` **entre dans les jonctions et les liens symboliques**. Une jonction posée dans un sous-dossier vers `C:\Windows\...` (possible pour un utilisateur standard, S1) ferait supprimer ces fichiers en administrateur. Les fichiers verrouillés seraient même programmés pour suppression au redémarrage (`MoveFileEx`).
- **Risque** : suppression de fichiers système. Le chemin nettoyé est fixe (`C:\RoboConfigurator`, jamais lu dans la configuration) : c'est la seule porte d'entrée.
- **Correction** : parcourir le dossier soi-même en **ignorant les points d'analyse** (`FileAttributes.ReparsePoint` : supprimer le lien, pas sa cible), vérifier que chaque chemin supprimé commence par `C:\RoboConfigurator\`, et ne pas nettoyer si `_workDir` est lui-même un lien. Avec S1 en plus, le risque disparaît.

### S5 — Valeurs relues du fichier de reprise non revalidées (P3)
- **Fichier** : `Core/RunConfig.cs:302-315`
- **Problème** : `NewName` et `WindowsKey` ne sont validés que dans l'interface (`OnStartClick`). Relus depuis `configuration.json`, ils partent tels quels vers WMI (`Rename`) et `slmgr /ipk`. Aucune injection dans un shell n'est possible (`cscript` est lancé directement, sans `cmd`), mais un fichier modifié (S1) passerait.
- **Correction** : revalider dans `RunConfig.Load` (`IsValidPcName`, `IsValidKey`) et vider les valeurs invalides en le journalisant. Valider aussi `-WindowsKey` dès `Options.Parse`, avec une erreur affichée comme pour les autres options.

### S6 — Lien de mise à jour ouvert sans contrôle (P3)
- **Fichiers** : `MainWindow.xaml.cs:201-204`, `Core/Updates.cs:486-487`
- **Problème** : `Process.Start(url)` ouvre ce que renvoie l'API GitHub, sans contrôle. Pas exploitable en pratique, mais `Process.Start` lancerait aussi un chemin local.
- **Correction** : n'ouvrir que des adresses `https://github.com/...`.

### S7 — Programmes système appelés par leur nom seul (P3)
- **Fichiers** : `Core/SystemInfo.cs:451`, `Core/Engine.cs:517, 569`, `MainWindow.xaml.cs:405, 426, 443`
- **Constat** : les arguments sont bien construits. Les chemins sont entre guillemets partout (`slmgr.vbs`, `setup.exe` dans `cmd /c ""..." ..."`, MAS, `reg export`), PowerShell reçoit `-EncodedCommand` et le nom de paquet est échappé (`'` → `''`).
- **Problème** : `cscript.exe`, `reg.exe`, `shutdown.exe`, `powershell.exe` et `cmd.exe` sont trouvés via le `PATH`.
- **Correction** : utiliser leur chemin complet (`Environment.SystemDirectory`).

---

## 4. Maintenabilité

### M1 — Aucun test unitaire (P1)
- **Problème** : la seule vérification automatique est le test de fumée de la CI (options, traductions, `IsNewer`). Rien ne teste le parsing des `.ini`, `RunConfig.Load/Save`, le masquage des clés ni `MatchesPackage`.
- **Correction** : voir la section 7.

### M2 — Logique difficile à tester en l'état (P2)
- **Fichiers** : `Core/CrapFixer.cs:147, 172-174`, `Core/SystemInfo.cs`
- **Problème** : `ReadIni` lit forcément un fichier, `SelectTweaks` lit `SystemInfo.IsWin11` (statique), et `Engine` appelle directement `SystemInfo`, le registre et `Process`.
- **Correction** (sans changer le comportement) : ajouter `ReadIni(IEnumerable<string> lines)` à côté de `ReadIni(path)`, `SelectTweaks(all, allTweaks, isWin11)`, et `InternalsVisibleTo` pour le projet de tests. Pas de refonte de `Engine` pour l'instant.

### M3 — Petits défauts de structure (P3)
- `Engine.cs` (840 lignes) regroupe les 6 étapes, le rapport et le nettoyage. Une classe par étape serait plus claire, mais ce n'est **pas proposé maintenant** : trop de risque de régression sans test sur PC réel.
- Le redémarrage de l'explorateur est écrit deux fois (`Engine.cs:213-222` et `524-531`).
- `LoadSystemInfoAsync` utilise les chaînes `"oui"` et `"non"` comme valeurs internes (`MainWindow.xaml.cs:216-223`) : un `bool?` est plus sûr.
- `ReadIni` coupe une valeur à la première espace suivie de `;` (`CrapFixer.cs:166`) : une valeur `STRING` contenant « ; » serait tronquée. Aucun cas connu *(à vérifier sur l'archive actuelle)*.
- `SetRegistry` avec `<delete>` et un chemin réduit à la ruche (`HKCU` seul) appellerait `DeleteSubKeyTree("")` : refuser explicitement ce cas.

### M4 — Build et CI (P2)
- **Fichiers** : `build.ps1`, `.github/workflows/build.yml`
- La **taille de l'exe** n'est pas contrôlée : une dépendance ajoutée par erreur passerait inaperçue. Proposition : la CI échoue au-delà de 4 Mo.
- Pas de `packages.lock.json` : WPF-UI et Costura sont figés, mais pas leurs dépendances. Proposition : `RestorePackagesWithLockFile`, et `--locked-mode` en CI.
- La CI ne lance ni tests unitaires (section 7) ni passage en simulation (section 8).
- Les actions GitHub sont épinglées par étiquette (`@v4`), pas par commit. Acceptable, à noter.

---

## 5. UX (interface)

### U1 — Pas de vrai bouton Annuler (P2, à décider)
- **Fichier** : `MainWindow.xaml.cs:453-469`
- **Problème** : on ne peut arrêter qu'en fermant la fenêtre, ce qui tue l'étape en cours (R2).
- **Correction proposée** : un bouton **« Arrêter après cette étape »**. L'étape en cours se termine, les suivantes passent à « Ignorée (arrêt demandé) », le rapport et le journal sont écrits, sans nettoyage ni redémarrage. La croix de la fenêtre propose le même choix. Pas d'arrêt **au milieu** d'une étape : trop risqué pour Edge et la barre des tâches.

### U2 — Rien n'empêche de lancer l'exe deux fois (P2)
- **Fichier** : `App.xaml.cs`
- **Problème** : le bouton Démarrer est bien désactivé pendant la confirmation, mais rien n'empêche d'ouvrir **deux fois l'exe**. Deux moteurs écriraient alors dans le même dossier, le même journal et la même liste d'étapes faites.
- **Correction** : un `Mutex` nommé `Global\RoboConfigurator` au démarrage. S'il est déjà pris : message « L'application est déjà ouverte », puis mise au premier plan de la fenêtre existante.

### U3 — Fenêtre trop haute pour les petits écrans (P2)
- **Fichier** : `MainWindow.xaml:8-11`
- **Problème** : la fenêtre fait 820 px de haut (minimum 560). En 1366×768, ou en 1024×768 avec le pilote graphique de base (fréquent juste après l'installation de Windows), la barre du bas avec **Démarrer** sort de l'écran.
- **Correction** : au chargement, limiter la taille à `SystemParameters.WorkArea` et maximiser si l'écran fait moins de 820 px de haut. Les pages défilent déjà, seule la taille de la fenêtre est en cause.

### U4 — Démarrer reste grisé si WMI ne répond pas (P3)
- **Fichier** : `MainWindow.xaml.cs:209-231`
- **Problème** : `StartButton` n'est activé qu'à la fin de `LoadSystemInfoAsync`. Si une requête WMI bloque (service WMI abîmé), le bouton ne s'active jamais, sans explication.
- **Correction** : au bout de 20 s, activer quand même le bouton avec « Informations du PC indisponibles » (la clé OEM ne sera pas utilisée).

### U5 — Thème clair / sombre (rien à corriger)
`SystemThemeWatcher` suit le thème de Windows et le XAML ne contient aucune couleur codée en dur (vérifié). Reste à vérifier à l'œil en mode contraste élevé *(sur PC)*.

### U6 — Comportements à trancher (voir questions)
- **Barre des tâches** : `LockedStartLayout = 1` est écrit côté machine et côté utilisateur (`Engine.cs:431-440`). Sur Windows 11, cela **verrouille** probablement la barre : l'utilisateur ne peut plus épingler ni désépingler, et la disposition revient à chaque ouverture de session *(à confirmer sur PC)*.
- **CrapFixer** : `Remove-AppxPackage` est lancé sans `-AllUsers`, et le paquet provisionné n'est pas retiré. Les applis supprimées **reviennent donc pour tout nouveau compte** créé sur le PC.

---

## 6. Traductions

Bilan : presque toutes les chaînes passent par `Lang.T` ou `{local:T}`. Les seules chaînes en dur dans le XAML sont identiques dans les deux langues (« Applications », « Options », « Windows Pro », le titre, le gabarit de la clé) : rien à corriger.

### L1 — Typographie française dans le journal anglais (P3)
- **Fichiers** : `Engine.cs:133` (`Windows : ...`), `Engine.cs:490-494` (`Firefox : ...`), `Report.cs:208, 214, 241` (`- X : ...`), `SystemInfo.cs:452` (`PowerShell « ... »`)
- **Problème** : espace avant « : » et guillemets « » même en anglais.
- **Correction** : `Lang.T(": ", " : ")` et `Lang.T("“…”", "« … »")`.

### L2 — Noms internes des étapes affichés tels quels (P3)
- **Fichiers** : `Engine.cs:92, 100, 114-115, 706`, `MainWindow.xaml.cs:367, 373`
- **Problème** : le journal, le résumé et le rapport affichent les identifiants internes `Taskbar`, `Activation`, `Edge`, même en français (« Étapes en échec : Taskbar »).
- **Correction** : une fonction `StepNames.Display(step)` qui rend le titre traduit, le même que dans l'interface.

### L3 — Le nom du journal dépend de la langue (P3)
- **Fichier** : `MainWindow.xaml.cs:30`
- **Problème** : le journal s'appelle `log.txt` ou `journal.txt`. Une reprise lancée dans l'autre langue (`-Lang`) écrit dans un second fichier.
- **Correction** : si un journal existe déjà, la reprise continue dans ce fichier.

---

## 7. Tests unitaires proposés

Un projet `tests/RoboConfigurator.Tests` (net472) séparé : **rien n'est ajouté à l'exe**. Les dépendances (xUnit ou MSTest, Q4) ne servent qu'aux tests, lancés par `dotnet test` dans la CI.

| Sujet | Cas testés |
|---|---|
| `CrapFixer.ReadIni` | sections, `clé=valeur`, commentaires `;` en début et en fin de ligne, lignes vides, clé avant toute section ignorée, casse des clés, `=` dans la valeur, BOM UTF-8 |
| `CrapFixer.SelectTweaks` | `DefaultSelected`, `-AllTweaks`, « Enable Dark Theme » toujours inclus, filtre `OsVersion` 10/11 |
| `CrapFixer.ParseNumber` | décimal, `0x...`, `0xFFFFFFFF` en DWORD, valeur invalide |
| `CrapFixer.MatchesPackage` | jokers `*` et `?`, casse, caractères spéciaux d'expression régulière dans le nom |
| `RunConfig` | `Save` puis `Load` identiques ; fichier sans `NiniteApps` (avant v2.6.0) ; étapes inconnues retirées ; édition inconnue ; fichier vide ou tronqué → erreur claire ; clé jamais en clair après S3 |
| `Options.Parse` | chaque option existante (`-Skip`, `-Apps`, `-AllTweaks`, `-WindowsKey`, `-Edition`, `-Lang`, `-Simulation`, `-Reset`, `-ForceCleanUp`), préfixes `-` / `/` / `--`, casse, valeur manquante en dernier argument, valeurs inconnues → erreur sans rien casser |
| `Updates.IsNewer` | `2.6.10` > `2.6.9`, finale > pré-version, pré-version < finale suivante, `2.6` contre `2.6.0`, valeur invalide → `false` |
| `Log` (masquage) | clé en majuscules et en minuscules, plusieurs clés sur une ligne, texte sans clé inchangé |
| `RunConfig.IsValidPcName` / `IsValidKey` | limites (15 caractères, chiffres seuls, tirets) |
| `NiniteApps` | `Normalize` (doublons, ordre, inconnues), `IsDefault`, `FileName` stable, `Url` |
| Statut des étapes | après B1 : un avertissement « pas fait » ne marque pas l'étape faite |

## 8. Ce que couvre le mode `-Simulation`

Vérifié dans le code : en simulation, rien n'est modifié en dehors de `%TEMP%\RoboConfigurator-simulation`. Il n'y a ni téléchargement, ni renommage, ni copie sur le Bureau, ni nettoyage, ni `shutdown`. Le système est seulement lu : WMI et registre (diagnostic, rapport, édition), plus la vérification de mise à jour sur GitHub.

Ce qu'il **ne couvre pas** :
1. **Les échecs** : impossible de voir une étape qui échoue, une étape « à terminer après le redémarrage », un nettoyage forcé ou un téléchargement raté. Proposition : une nouvelle option `-SimulateFail Edge,Office` (un ajout, rien n'est cassé) qui fait échouer les étapes indiquées, et `-SimulateFail Activation:restart` pour le changement d'édition.
2. **Les liens de téléchargement** : la simulation ne contacte pas le Nextcloud, elle n'aurait donc pas vu le changement d'adresse de la v2.6.2. Proposition : en simulation, une requête `HEAD` (lecture seule) sur chaque lien, avec le code HTTP et la taille dans le journal.
3. **Edge et Barre des tâches** n'ont qu'une simulation générique (« Edge : simulation... »). Proposition : montrer ce qui serait fait (désinstalleur trouvé, version, fichiers créés), en lecture seule.
4. **La CI** ne peut pas lancer la simulation : la fenêtre WPF est obligatoire. Proposition : après M2, tester `Engine` en simulation sans fenêtre (le moteur ne dépend que des `StepItem`).

---

## 9. Questions avant la phase 2

- **Q1 (S2)** — Mettre le SHA-256 de `CrapFixer.zip` et de `MAS_AIO.cmd` dans le code ? C'est plus sûr, mais chaque mise à jour de ces fichiers sur le Nextcloud demandera un nouvel exe. Autre choix : signature Authenticode pour les exe seulement, et simple contrôle du format pour le zip et le `.cmd`.
- **Q2 (U6)** — Barre des tâches : garder `LockedStartLayout = 1` (barre verrouillée), ou appliquer la disposition une fois et laisser l'utilisateur la modifier ensuite ?
- **Q3 (U6)** — CrapFixer : retirer aussi les applis pour tous les comptes (`-AllUsers` et paquet provisionné) ?
- **Q4 (M1)** — Tests : xUnit (recommandé, le plus courant avec `dotnet test`) ou MSTest ? Dans les deux cas, la dépendance ne concerne que le projet de test, pas l'exe.
- **Q5 (U1)** — Le bouton « Arrêter après cette étape » vous convient-il, ou faut-il garder le comportement actuel ?
- **Q6 (R8)** — Compte administrateur différent de la session : un simple avertissement suffit-il ?

## 10. Ordre proposé pour la phase 2

Un commit par point, dans cet ordre (d'abord les tests, puis la sécurité et la reprise) :
1. M2 + M1 : tests de la logique actuelle, pour sécuriser la suite
2. S1, S4 : droits du dossier, nettoyage
3. B1, B2 : statuts et rapport
4. B3, R4, R5, S2 : téléchargements
5. R1, R2, B4, B5, R7 : délais, fichiers laissés, erreurs
6. R3, S3, S5 : fichier de reprise
7. U2, U3, U4, U1 : interface
8. L1 à L3, M3, M4, S6, S7, simulation (section 8)

À chaque commit : `build.ps1`, test de fumée de la CI, tests unitaires, passage en `-Simulation`. Ni changement de `<Version>`, ni tag.
