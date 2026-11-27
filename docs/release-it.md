# Come preparo una release di VibeKit

Dalla radice del repository eseguo `python scripts/release/release.py` per pubblicare **un solo sottoprogetto**. Il canale `dev` (alias `test`) produce una prerelease dal commit di `dev` su GitHub. Il canale `stable` pubblica dal commit di `main` su GitHub. Ogni release contiene i pacchetti del progetto e `SHA256SUMS.txt`.

## Prima pubblicazione

Uso Python 3.11 o successivo e GitHub CLI (`gh`). Con `gh auth status` verifico di essere collegato a GitHub con l'account autorizzato a leggere i run, avviare i workflow e scrivere tag e release su VibeKit.

Per rendere disponibile un nuovo workflow manuale su GitHub, il suo file deve comparire sul branch predefinito. Porto le modifiche su `dev`, apro una PR da `dev` a `main` e la unisco dopo i controlli. Da quel momento posso avviare anche su `dev` i nuovi workflow di packaging. Prima di pubblicare controllo che i miei commit siano sul branch remoto scelto. Il comando usa quel branch anche quando il checkout locale è su un altro branch.

## Provare una build

Per vedere commit, tag, workflow e pacchetti previsti prima di avviare la build:

```powershell
python scripts/release/release.py voiceducker --channel dev --dry-run
```

Per costruire o riutilizzare i pacchetti e pubblicare la prerelease:

```powershell
python scripts/release/release.py voiceducker --channel dev
```

Il comando cerca un run con esito positivo per lo stesso branch e commit, dal workflow di packaging previsto o dal dispatcher VibeKit per i progetti compatibili. Accetta i run di push o avvio manuale del repository, con tutti gli artefatti necessari, ancora disponibili e muniti di hash. In assenza di una build idonea avvia il workflow del solo progetto. Se il branch cambia durante l'avvio e il run riceve un altro commit, l'operazione si ferma. Riparto dal nuovo piano.

## Pubblicare la stabile

Dopo aver promosso su `main` la versione che intendo distribuire:

```powershell
python scripts/release/release.py voiceducker --channel stable --dry-run
python scripts/release/release.py voiceducker --channel stable
```

Una stabile usa il tag `release/<progetto>/v<versione>`. Il tag di test aggiunge `-test.<sha>` alla versione. Questi prefissi evitano di avviare di nuovo i vecchi workflow su tag. I tag e le release già esistenti restano disponibili ai loro indirizzi.

Il comando scarica gli artefatti in una directory temporanea, controlla i digest SHA-256 comunicati da Actions e seleziona i file previsti dal catalogo. Prepara una bozza, verifica i digest degli asset caricati e pubblica solo dopo la verifica. Il link finale apre la release. Sul PC di destinazione, per confrontare un download con `SHA256SUMS.txt`:

```powershell
Get-FileHash .\VoiceDucker-windows-x64.msi -Algorithm SHA256
```

## Opzioni per sottoprogetto

| Identificativo | Pacchetti distribuiti o condizione |
| --- | --- |
| `voiceducker` | MSI Windows x64 e ZIP Windows x64 e ARM64. |
| `ulanzideck` | ZIP Windows x64 e ARM64, archivio e DEB Linux x64. |
| `worktrail` | ZIP portatili Windows x64 e ARM64. Per i test il pacchetto può usare una versione 0.x, per la stabile il packaging del progetto richiede una versione sorgente con major almeno 1. |
| `markvibe` | MSI e ZIP Windows x64 e ARM64. Specifico la versione completa con `--version`, maggiore della revisione registrata nel sorgente. |
| `reporadar` | MSI e ZIP portatile per Windows x64, DEB per Linux amd64. |
| `helpme` | DEB Linux amd64 e arm64. Le note della release contengono un link autenticato all'MSI Windows x64 del run, valido fino alla scadenza dell'artefatto Actions. Il canale commerciale Windows usa Microsoft Store. |
| `ghostty-shaders` | Archivio Linux con shader, gestore e licenze. Per la stabile specifico `--version X.Y.Z`. Per un test basta il commit. |
| `thermalright` | MSI e ZIP Windows x64 e ARM64 e DEB Linux amd64. Il canale test usa pacchetti Debug. La stabile richiede la configurazione Release sui tre target. |
| `openai-usage`, `desktop-ripple`, `retro-terminal`, `studio-controls`, `vignette` | MSI Windows x64 e pacchetto Debian del widget scelto. |

Esempi con versione esplicita:

```powershell
python scripts/release/release.py markvibe --channel dev --version 0.1.0.64 --dry-run
python scripts/release/release.py ghostty-shaders --channel stable --version 1.0.0 --dry-run
```

## Riprendere un'operazione

Se il caricamento si interrompe, uso l'ID del run stampato dal comando. Ripeto lo stesso comando, con gli stessi parametri e `--run-id`. Per esempio, sostituisco il numero con quello del run scelto:

```powershell
python scripts/release/release.py voiceducker --channel dev --run-id 123456789
```

Per riprendere, il commit deve corrispondere ancora alla punta del branch. Gli asset già presenti devono avere i digest attesi. In caso di discrepanza il comando si ferma e lascia la bozza disponibile per l'ispezione. Per aggiungere un sottoprogetto aggiorno la mappa CI in `scripts/ci/projects.json` e i nomi degli artefatti e dei pacchetti in `scripts/release/projects.json`.
