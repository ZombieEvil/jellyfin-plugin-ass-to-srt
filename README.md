# ASS to SRT — Jellyfin plugin

🇬🇧 [English](#english) · 🇫🇷 [Français](#français)

---

## English

**Your anime won't play smoothly on your TV because of ASS subtitles? This plugin fixes it.**

Many anime ship with **ASS/SSA** subtitles (colors, fonts, positions). Some Jellyfin clients cannot display them — **Jellyfin for Android TV / Fire TV, Roku**, some smart TVs. When you turn those subtitles on, the server has to **burn them into the video** (full transcoding). On a small server (NAS, Raspberry Pi, VM...) this means infinite loading or constant stuttering.

**ASS to SRT** adds an **SRT copy** of every ASS track, right next to the video. SRT is a simple format every client plays directly: no transcoding, no stutter. Jellyfin ranks external subtitles first, so the SRT is picked automatically.

### Features
- 🔍 **Scans your libraries** and converts every ASS/SSA track that has no SRT in the same language yet (embedded in MKV or external `.ass` files).
- ⚡ **Automatic**: new videos are converted about a minute after Jellyfin has analysed them.
- 🌙 **Scheduled task** every night (4:00) + a **"Convert now"** button.
- 🧹 **Clean SRT**: fonts, colors, karaoke and vector drawings removed; bold/italic and `{\an8}` positioning kept.
- 🏷️ Forced tracks become `.forced.srt`; "Signs / Songs" tracks are skipped (optional).
- ♻️ **Never touches your video files** and keeps the ASS tracks: it only adds `.srt` files. If the media folder is read-only, the SRT goes to Jellyfin's metadata folder instead.
- Uses Jellyfin's own ffmpeg: nothing else to install.

### Installation
1. Jellyfin → **Dashboard → Plugins → Repositories** → **+**
2. Name: `ASS to SRT` — URL:
   ```
   https://raw.githubusercontent.com/ZombieEvil/jellyfin-plugin-ass-to-srt/main/manifest.json
   ```
3. **Catalog** → **ASS to SRT** → **Install**, then **restart Jellyfin**.
4. Open the plugin settings, choose your libraries and click **Save and convert now**.

Requires **Jellyfin 12.0 or newer**.

### Output files
| Source track | Created file |
|---|---|
| French ASS, full | `Episode.fre.srt` |
| French ASS, forced | `Episode.fre.forced.srt` |
| External `Episode.fr.ass` | `Episode.fr.default.srt` |

### Limits
- SRT cannot keep ASS styling: positions (except top/bottom), colors, karaoke effects are lost. The ASS track is still there for clients that support it.
- Extracting an embedded track reads the whole video file once (it can take a minute for a large file on a network share).

---

## Français

**Tes animes saccadent ou chargent à l'infini sur ta télé à cause des sous-titres ASS ? Ce plugin règle le problème.**

Beaucoup d'animes ont des sous-titres **ASS/SSA** (couleurs, polices, positions). Certaines applis Jellyfin ne savent pas les afficher : **Jellyfin pour Android TV / Fire TV, Roku**, certaines télés. Quand tu actives ces sous-titres, le serveur doit les **incruster dans la vidéo** (transcodage complet). Sur un petit serveur (NAS, Raspberry Pi, VM...), ça donne un chargement infini ou des coupures en permanence.

**ASS to SRT** ajoute une **copie SRT** de chaque piste ASS, juste à côté de la vidéo. Le SRT est un format simple que toutes les applis lisent directement : zéro transcodage, zéro coupure. Jellyfin met les sous-titres externes en premier, donc le SRT est choisi automatiquement.

### Fonctionnalités
- 🔍 **Scanne tes médiathèques** et convertit chaque piste ASS/SSA qui n'a pas encore de SRT dans la même langue (intégrée au MKV ou fichier `.ass` séparé).
- ⚡ **Automatique** : les nouvelles vidéos sont converties environ une minute après leur analyse par Jellyfin.
- 🌙 **Tâche planifiée** chaque nuit (4 h) + un bouton **« Convertir maintenant »**.
- 🧹 **SRT propres** : polices, couleurs, karaoké et dessins vectoriels retirés ; gras/italique et position `{\an8}` conservés.
- 🏷️ Les pistes forcées deviennent `.forced.srt` ; les pistes « Signs / Songs » sont ignorées (réglable).
- ♻️ **Ne modifie jamais tes vidéos** et garde les pistes ASS : il ajoute seulement des `.srt`. Si le dossier est en lecture seule, le SRT va dans le dossier de métadonnées de Jellyfin.
- Utilise le ffmpeg de Jellyfin : rien d'autre à installer.

### Installation
1. Jellyfin → **Tableau de bord → Extensions → Dépôts** → **+**
2. Nom : `ASS to SRT` — URL :
   ```
   https://raw.githubusercontent.com/ZombieEvil/jellyfin-plugin-ass-to-srt/main/manifest.json
   ```
3. **Catalogue** → **ASS to SRT** → **Installer**, puis **redémarre Jellyfin**.
4. Ouvre les réglages du plugin, choisis tes médiathèques et clique sur **Save and convert now**.

Nécessite **Jellyfin 12.0 ou plus récent**.

### Limites
- Le SRT ne garde pas le style ASS : positions (sauf haut/bas), couleurs et karaoké sont perdus. La piste ASS reste disponible pour les applis qui la gèrent.
- Extraire une piste intégrée lit toute la vidéo une fois (ça peut prendre une minute pour un gros fichier sur un partage réseau).

---

## Build from source / Compiler

```bash
dotnet build Jellyfin.Plugin.AssToSrt/Jellyfin.Plugin.AssToSrt.csproj -c Release
```
Copy `Jellyfin.Plugin.AssToSrt.dll` into `<jellyfin-data>/plugins/ASS to SRT/` and restart Jellyfin.

Releases: publishing a GitHub release tagged `vX.Y.Z` builds the plugin and updates `manifest.json` automatically.

## License

GPL-3.0
