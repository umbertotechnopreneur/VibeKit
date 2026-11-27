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

## Pacchetti per sottoprogetto

Per ogni applicazione Windows preparo quattro file, tutti compilati in configurazione **Release**. Il canale `dev` sceglie i sorgenti di sviluppo e contrassegna la pubblicazione come anteprima. Anche su questo canale i pacchetti sono ottimizzati. Le build Debug si preparano sul PC di sviluppo.

| Sul PC di destinazione | Intel o AMD a 64 bit | Windows su ARM |
| --- | --- | --- |
| Voglio estrarre il programma in una cartella | ZIP portatile x64 | ZIP portatile ARM64 |
| Voglio installare il programma | MSI x64 | MSI ARM64 |

| Sottoprogetti | Download disponibili |
| --- | --- |
| `voiceducker`, `markvibe`, `worktrail` | I quattro pacchetti Windows. |
| `reporadar`, `ulanzideck`, `thermalright` | I quattro pacchetti Windows e il DEB Linux amd64. UlanziDeck offre anche il suo archivio Linux. |
| `openai-usage`, `desktop-ripple`, `retro-terminal`, `studio-controls`, `vignette` | I quattro pacchetti Windows e il DEB del widget per Linux. |
| `helpme` | DEB pubblici per Linux amd64 e arm64. Per Windows accedo a GitHub e seguo i link nelle note della release ai quattro artefatti Actions. I link valgono fino alla scadenza degli artefatti. La versione commerciale si trova nel Microsoft Store. |
| `ghostty-shaders` | ZIP per Ghostty su Linux con shader, gestore e licenze. |

### Primo avvio su un PC Windows

1. In **Impostazioni → Sistema → Informazioni sul sistema** leggo il tipo di processore. Scelgo i download **x64** per Intel o AMD a 64 bit, **ARM64** per Windows su ARM.
2. Per provare tutte le app Windows installo una volta **.NET Desktop Runtime 10.0**, scegliendo la stessa architettura dei pacchetti e l’ultima patch disponibile. Include anche il runtime di base richiesto dalle app .NET. Vignette usa inoltre il framework desktop WPF. UlanziDeck è nativo e può essere eseguito senza .NET. [Download ufficiali Microsoft](https://dotnet.microsoft.com/it-it/download/dotnet/10.0)
3. In PowerShell eseguo `dotnet --info` per controllare l’architettura dell’host e `dotnet --list-runtimes` per verificare la presenza di `Microsoft.NETCore.App 10.0.x` e `Microsoft.WindowsDesktop.App 10.0.x`.
4. Installo l’MSI oppure estraggo **l’intero ZIP** in una cartella e avvio l’eseguibile da lì. Se su un PC ARM uso un pacchetto x64 in emulazione, serve anche il runtime x64.

Le applicazioni .NET per Windows usano il runtime di sistema sia con lo ZIP sia con l’MSI. Le app WinUI possono includere le proprie librerie **Windows App SDK**. Le note della release HelpMe collegano i pacchetti Windows agli artefatti Actions. Gli utenti che accedono a GitHub possono scaricarli fino alla scadenza. Per gli MSIX destinati allo Store si usa la procedura dedicata.

Per MarkVibe specifico una versione a quattro componenti maggiore della revisione nei sorgenti. WorkTrail permette le versioni 0.x sul canale `dev` e richiede almeno 1.x per la stabile. Per la stabile di Ghostty Shaders scelgo esplicitamente una versione con `--version`.

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
