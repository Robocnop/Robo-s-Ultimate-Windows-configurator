# Fichiers intégrés à l'exe (optionnel)

Par défaut, l'application télécharge ses fichiers depuis le Nextcloud au moment de la configuration.

Pour un exe qui fonctionne **sans Internet**, déposer ici un ou plusieurs de ces fichiers avant de lancer `build.ps1` :

| Fichier | Étape |
|---|---|
| `Ninite.exe` | Ninite |
| `CrapFixer.zip` | CrapFixer |
| `OfficeSetup.exe` | Office |
| `MAS_AIO.cmd` | Activation (script de secours) |

Les fichiers présents sont intégrés dans `RoboConfigurator.exe` et utilisés à la place du téléchargement.
Ils ne sont pas versionnés (voir `.gitignore`).
