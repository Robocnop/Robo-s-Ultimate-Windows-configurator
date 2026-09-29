# État d'avancement

Dernière mise à jour : 29/09/2026. Dernière version publiée : [v1.2.0](https://github.com/Robocnop/Robo-s-Ultimate-Windows-configurator/releases/tag/v1.2.0) (pré-version).

## État des étapes

| Étape | État | Testé en VM | Notes |
|---|---|---|---|
| Téléchargements | ✅ Fonctionne | Oui (v1.0.0) | Liens Nextcloud valides, 3 essais par fichier |
| Ninite | ✅ Fonctionne | Oui (v1.0.0) | Cliquer sur **Close** à la fin |
| CrapFixer | ✅ Fonctionne | Oui (v1.0.0) | 47 tweaks recommandés (46 sur Win10) + thème sombre, 13 applis supprimées |
| Edge | ⚠️ À valider | Non | Réécrit en v1.1.0 (voir plus bas) |
| Office | ⚠️ À valider | Interrompu | La VM a planté pendant l'installation (voir plus bas) |
| Activation | ⚠️ À valider | Non atteint | Vérifie l'activation de Windows, `-WindowsKey` optionnel |
| Assistant de configuration | ⚠️ À valider | Non | Tout est demandé au début, puis le script tourne seul. Menu testé avec des réponses simulées |
| Renommage du PC | ⚠️ À valider | Non | Choisi dans l'assistant, appliqué au redémarrage |
| Redémarrage automatique | ⚠️ À valider | Non | 60 secondes après la fin, annulable avec `shutdown /a` |
| Rapport du poste | ⚠️ À valider | Non | Testé seul sur un PC réel sans droits admin : OK. Secure Boot et TPM restent à vérifier en admin |

## Historique

- **v1.0.0** : première version. Premier test en VM : téléchargements, Ninite et CrapFixer OK.
- **v1.1.0** : optimizerDuck est remplacé par une désinstallation automatique d'Edge.
  - optimizerDuck 2.27.6 se lançait mais restait en arrière-plan sans afficher son interface.
  - D'après son code source, il ne désinstalle pas vraiment Edge : il le classe seulement parmi les applis « à risque » de sa liste de bloatwares.
  - Le script utilise désormais le désinstalleur officiel d'Edge (méthode WinUtil de Chris Titus : faux `MicrosoftEdge.exe` dans `SystemApps`, puis `setup.exe --uninstall --system-level --force-uninstall`). WebView2 est conservé.
- **v1.1.1** : ajout de ce fichier d'état d'avancement et de la vidéo de présentation. Le script est identique à la v1.1.0.
- **v1.2.0** : à la fin, le script propose de renommer le PC avant le redémarrage et génère un rapport du poste (matériel, Windows, réseau, résultat des étapes).
- **En cours (non publié)** : assistant de configuration au démarrage (nom du PC, étapes, tweaks, clé Windows, redémarrage). Une fois validé, le script tourne seul jusqu'au redémarrage.

## Problèmes connus

### Écran bleu WHEA_UNCORRECTABLE_ERROR dans la VM pendant l'installation d'Office

Ce n'est probablement pas lié au script : c'est Windows, dans la VM, qui s'est figé, et les journaux de VMware ne montrent aucun plantage de VMware lui-même.

- Côté hôte, le microcode du processeur i7-14700F est `0x12F` (version corrigée) et le BIOS est Gigabyte F17 de juin 2026. Aucune erreur matérielle réelle n'a été relevée.
- Cause probable : VMware tourne en mode **ULM**, c'est-à-dire par-dessus Hyper-V, parce que la sécurité basée sur la virtualisation (VBS / Intégrité de la mémoire) est active sur l'hôte. Ce mode est connu pour être instable avec les processeurs hybrides.
- À essayer : désactiver l'accélération 3D, décocher les options de virtualisation du processeur (VT-x/EPT, IOMMU, compteurs de performance) et passer à 2 processeurs. En dernier recours, désactiver l'Intégrité de la mémoire sur l'hôte.

## Environnement de test

- La VM de test a été supprimée. Elle était entièrement chiffrée à cause du TPM virtuel : lors de la recréation, choisir de ne chiffrer que les fichiers du TPM.
- Une ISO d'outils, `Robo-Outils-v2.iso`, contient FlyOOBE 3.03.100 (avec les Setup Actions) et le dossier `RoboConfigurator`. Elle se branche comme second lecteur CD.
- FlyOOBE se lance depuis l'écran de configuration de Windows (OOBE) : Shift + F10, puis `E:\Flyoobe.exe`. Il ne fonctionne pas sur l'écran de langue de l'installateur, car .NET 4.8 y est absent.

## Prochaines étapes

1. Recréer la VM avec les réglages ci-dessus.
2. Lancer `Lancer.bat -Reset` et valider la désinstallation d'Edge.
3. Valider l'installation d'Office et l'étape Activation.
4. Valider l'assistant, le renommage, le redémarrage automatique et relire le rapport généré.
5. Si tout fonctionne, passer la v1.2.0 en version principale.
