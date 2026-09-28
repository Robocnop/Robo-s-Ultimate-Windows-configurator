# Robo-s-Ultimate-Windows-configurator

Configuration automatique d'un PC juste après l'installation de Windows 10 ou 11.

## Utilisation

1. Copier `Lancer.bat` et `Configurator.ps1` sur le PC (clé USB, etc.).
2. Double-cliquer sur `Lancer.bat` et accepter la demande administrateur.

Rien d'autre à installer : le script tourne avec le PowerShell 5.1 fourni par Windows.

## Ce que fait le script

| Étape | Outil | Automatique ? |
|---|---|---|
| Ninite | Installe 7-Zip, Chrome, Firefox, Foxit Reader, Notepad++ | Oui, cliquer sur **Close** à la fin |
| CrapFixer | Applique les tweaks recommandés + thème sombre, supprime les applis inutiles (Candy Crush, Skype, Bing News...) | Entièrement |
| Edge | Désinstalle Edge avec son désinstalleur officiel (même méthode que WinUtil), WebView2 conservé | Entièrement |
| Office | Lance `OfficeSetup.exe` et attend la fin de l'installation | Entièrement |
| Activation | Vérifie que Windows est activé, sinon ouvre la page d'activation | Oui |

Pour CrapFixer, le script lit directement ses fichiers `Wintweak2.ini` et `Winappx.ini` et applique les
entrées cochées par défaut dans l'appli. Mettre à jour l'archive CrapFixer sur le Nextcloud suffit donc
pour mettre à jour les tweaks.

Tout est téléchargé dans `C:\RoboConfigurator`, avec un journal dans `journal.txt`. Les étapes réussies
sont mémorisées : relancer le script reprend là où il s'était arrêté.

## Options

```bat
Lancer.bat -Skip Office,Activation   :: ignorer des étapes (Ninite, CrapFixer, Edge, Office, Activation)
Lancer.bat -AllTweaks                :: appliquer TOUS les tweaks CrapFixer, pas seulement les recommandés
Lancer.bat -WindowsKey XXXXX-XXXXX-XXXXX-XXXXX-XXXXX   :: installer une clé Windows si non activé
Lancer.bat -Reset                    :: tout refaire depuis le début
```

## Remarques

- Les réglages utilisateur (thème sombre, barre des tâches...) s'appliquent au compte qui lance le script :
  le lancer depuis la session qui sera utilisée.
- Les liens de téléchargement sont en haut de `Configurator.ps1` (`$Downloads`).
