# Robo-s-Ultimate-Windows-configurator

Configuration automatique d'un PC juste après l'installation de Windows 10 ou 11.

## Utilisation

1. Copier `Lancer.bat` et `Configurator.ps1` sur le PC (clé USB, etc.).
2. Double-cliquer sur `Lancer.bat` et accepter la demande administrateur.

Rien d'autre à installer : le script tourne avec le PowerShell 5.1 fourni par Windows.

Au démarrage, un assistant permet de tout régler avant de lancer :

```
  1. Nom du PC             : DESKTOP-AB12CD -> PC-SALON
  2. Étapes                : [x] Ninite  [x] CrapFixer  [x] Edge  [x] Office  [x] Activation
  3. Tweaks CrapFixer      : recommandés
  4. Clé Windows           : aucune
  5. Redémarrage à la fin  : automatique
Numéro à modifier, ou Entrée pour valider
```

Une fois la configuration validée, le script tourne seul jusqu'à la fin (seul Ninite demande de cliquer sur
**Close**). Si le script est interrompu, il propose au lancement suivant de reprendre avec la même configuration.

## Ce que fait le script

| Étape | Outil | Automatique ? |
|---|---|---|
| Ninite | Installe 7-Zip, Chrome, Firefox, Foxit Reader, Notepad++ | Oui, cliquer sur **Close** à la fin |
| CrapFixer | Applique les tweaks recommandés + thème sombre, supprime les applis inutiles (Candy Crush, Skype, Bing News...) | Entièrement |
| Edge | Désinstalle Edge avec son désinstalleur officiel (même méthode que WinUtil), WebView2 conservé | Entièrement |
| Office | Lance `OfficeSetup.exe` et attend la fin de l'installation | Entièrement |
| Activation | Vérifie que Windows est activé, sinon ouvre la page d'activation | Oui |

À la fin, le script :
- **renomme le PC** si un nouveau nom a été choisi dans l'assistant (appliqué au redémarrage) ;
- génère un **rapport du poste** `Rapport-<nom>-<date>.txt`, copié sur le Bureau et dans `C:\RoboConfigurator` :
  nom, modèle, n° de série, BIOS, Secure Boot, TPM, version et activation de Windows, processeur, mémoire,
  disques et espace libre, carte graphique, cartes réseau (MAC / IP) et résultat de chaque étape ;
- **redémarre** au bout de 60 secondes si c'est coché (annulable avec `shutdown /a`).

Pour CrapFixer, le script lit directement ses fichiers `Wintweak2.ini` et `Winappx.ini` et applique les
entrées cochées par défaut dans l'appli. Mettre à jour l'archive CrapFixer sur le Nextcloud suffit donc
pour mettre à jour les tweaks.

Tout est téléchargé dans `C:\RoboConfigurator`, avec un journal dans `journal.txt`. Les étapes réussies
sont mémorisées : relancer le script reprend là où il s'était arrêté.

## Options

Les options pré-remplissent l'assistant :

```bat
Lancer.bat -Skip Office,Activation   :: décocher des étapes (Ninite, CrapFixer, Edge, Office, Activation)
Lancer.bat -AllTweaks                :: cocher TOUS les tweaks CrapFixer, pas seulement les recommandés
Lancer.bat -WindowsKey XXXXX-XXXXX-XXXXX-XXXXX-XXXXX   :: clé Windows à installer si non activé
Lancer.bat -Reset                    :: oublier la configuration et les étapes faites, tout refaire
```

## Remarques

- Les réglages utilisateur (thème sombre, barre des tâches...) s'appliquent au compte qui lance le script :
  le lancer depuis la session qui sera utilisée.
- Les liens de téléchargement sont en haut de `Configurator.ps1` (`$Downloads`).
- La configuration est enregistrée dans `C:\RoboConfigurator\configuration.json` (clé Windows comprise) et
  supprimée automatiquement quand toutes les étapes ont réussi.
