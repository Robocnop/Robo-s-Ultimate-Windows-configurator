# Brag plan — Robo's Ultimate Windows Configurator (v2.3)

**Format :** vertical 1080×1920, 30 fps, 24,3 s. **Langues :** anglais (`brag.mp4`) et français (`brag.fr.mp4`), mêmes timings et même bande-son. **Ton :** `default` (punchy, propre, un brin moqueur envers la corvée post-install).

## Réponses
- **C'est quoi ?** Une appli portable, `RoboConfigurator.exe`, qui configure un PC juste après l'installation de Windows 10/11 : logiciels, nettoyage, thème sombre, Edge retiré, barre des tâches rangée, Office, activation.
- **Pour qui ?** Ceux qui réinstallent des PC (le leur, ceux de la famille, des clients) et refont toujours les mêmes clics.
- **Ce qui le distingue :** un seul exe, rien à installer, il suit le thème de Windows et reprend où il s'était arrêté.
- **Nouveauté mise en avant (v2.2) :** l'étape « Barre des tâches » épingle Explorateur, Firefox, Terminal et PowerShell dans cet ordre, à la place des épingles de Windows.
- **Vrai produit montré :** la vraie fenêtre « Configuration en cours » (étapes de `MainWindow.xaml.cs`, détail « Réglage n/47 » de CrapFixer, vrais noms de tweaks de `Wintweak2.ini`).

## Identité visuelle
- Fond Windows 11 avec bloom, qui passe du clair au sombre quand CrapFixer applique « Enable Dark Theme » (la fenêtre de l'appli bascule avec, comme la vraie).
- Bleu Windows `#0078D4` / `#4cc2ff`, dégradé magenta → violet → bleu pour le titre.
- Polices : Segoe UI Variable (titres et appli), Cascadia Mono (lien GitHub).

## Storyboard
| # | Temps | Scène | Texte à l'écran | Son |
|---|---|---|---|---|
| 1 | 0–3,9 s | **Hook.** Bureau clair, les corvées tombent une à une. | « Windows vient de s'installer. », 5 corvées dont « Ranger la barre des tâches », « …et tu fais tout à la main ? » | Pad qui monte, ticks |
| 2 | 4–7,4 s | **Reveal.** Icône `RoboConfigurator.exe`, double-clic, titre. | « Un double-clic. Il fait le reste. » | Clics, le beat démarre |
| 3 | 7,5–14,3 s | **L'appli.** Fenêtre « Configuration en cours » : Ninite, CrapFixer (Réglage 1→47, bascule en sombre), Edge, Barre des tâches (badge NOUVEAU), Office, Activation, Nom du PC, Rapport. | Pastilles : « 47 tweaks », « thème sombre », « Edge viré », « Office installé » | Ticks par étape, swell au passage en sombre |
| 4 | 14,4–20 s | **Nouveau · v2.2.** Barre des tâches : Edge, Store, Explorateur tombent, puis Explorateur, Firefox, Terminal, PowerShell arrivent un par un. | « La barre des tâches, rangée pour toi. » / « Les épingles de Windows dégagent. Prêt dès la prochaine session. » | Chute, 4 notes montantes |
| 5 | 20,35–24,3 s | **Outro.** Titre, `RoboConfigurator.exe`, lien GitHub. | « Windows 10 & 11 · un seul exe, rien à installer » | Accord final, fade |

## Régénérer
Dans `brag-output/work` : `VIDEO_LANG=en node capture.mjs frames` (ou `fr`), `python synth.py`, puis ffmpeg (images + `audio.wav` → `brag.mp4` / `brag.fr.mp4`). Les textes des deux langues sont dans l'objet `S` de `video.html`.
`README.md` affiche `brag.mp4` et `README.fr.md` affiche `brag.fr.mp4`, envoyés sur GitHub (adresses user-attachments) : après une nouvelle version, glisser les nouveaux mp4 dans un ticket et remplacer les adresses.
