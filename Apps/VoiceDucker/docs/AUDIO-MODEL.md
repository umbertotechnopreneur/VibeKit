> **File name** `AUDIO-MODEL.md`<br>
> **Full path** `E:\vibekit\Apps\VoiceDucker\docs\AUDIO-MODEL.md`<br>
> **Path relative to the VibeKit repository** `Apps\VoiceDucker\docs\AUDIO-MODEL.md`

# Audio application model

`VoiceDuckerModel` is the application state owner. It loads and saves preferences,
serializes user operations, checks licensing, controls startup behavior and owns
the audio worker. `ApplicationState` is an immutable snapshot for presentation.
The window renders that snapshot and sends typed commands. Focus, editing buffers,
scroll, window geometry and animations remain view concerns.

## State and audio ownership

```mermaid
flowchart TB
    accTitle: VoiceDucker ownership across the UI, model and audio worker
    accDescr: The view sends typed commands and receives an immutable snapshot. One worker owns microphone detection, playback session state and mixer writes.
    UI["WinUI window<br/>Controls, status and actual volume slider"]
    MODEL["VoiceDuckerModel<br/>Preferences, licensing and command serialization"]
    SETTINGS["Settings file<br/>Preferences and next-launch microphone opt-in"]
    subgraph WORKER["One app-lifetime audio worker"]
        MIC["Capture adapter<br/>Accept scalar observations"]
        GATE["VoiceGate<br/>Gain, threshold, hysteresis and release"]
        PLAY["Playback sessions<br/>Startup, normal, target and actual gain"]
    end
    INPUT["Windows microphone capture"]
    OUTPUT["Windows per-app Core Audio sessions"]

    UI <-->|"Typed commands and immutable ApplicationState"| MODEL
    MODEL <-->|"Load and save preferences"| SETTINGS
    MODEL -->|"Listening settings and playback commands"| WORKER
    WORKER -->|"Detection phase, readback and errors"| MODEL
    INPUT -->|"RMS, timestamp, sequence and generation"| MIC
    MIC -->|"Current observation"| GATE
    GATE -->|"Speaking decision"| PLAY
    PLAY -->|"Faded gain and application EventContext"| OUTPUT
    OUTPUT -->|"Readback and external mixer events"| PLAY

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef external fill:#fff0d8,stroke:#9b6218,color:#51330e
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class UI native
    class MODEL,MIC,GATE,PLAY model
    class INPUT,OUTPUT external
    class SETTINGS storage
```

## Audio path

The audio path is linear:

1. Capture publishes raw RMS, monotonic observation time, sequence and generation.
2. The worker applies software sensitivity once, then evaluates threshold,
   hysteresis and the release delay against that same settings snapshot.
3. The detector returns a phase and a speaking decision. Repeated polling never
   renews a sample or its release deadline. Stale capture fails safely.
4. Playback computes a target from a stable normal volume and advances its fade.
5. Native readback, actual attenuation and errors become a new application snapshot.

```mermaid
flowchart TB
    accTitle: Voice detection, attenuation and restoration
    accDescr: Fresh microphone samples feed the gate. Voice and its release hold lower the normal volume. Silence returns to normal. Disable or capture failure requests startup restoration.
    TICK["Audio worker tick while monitoring"]
    DISABLE["Disable or exit"]
    FRESH{"Capture is healthy and within the age limit?"}
    GATE{"Voice above threshold,<br/>held by hysteresis or in release delay?"}
    DOWN["Target normal gain × configured reduction factor"]
    NORMAL["Target normal gain"]
    SAFETY["Stop capture and request startup restoration<br/>Show failures in status"]
    WRITE["Advance fade or restoration<br/>Read actual Core Audio gain"]
    STATE["Publish detection phase, actual gain and errors"]

    TICK --> FRESH
    FRESH -->|"Yes"| GATE
    FRESH -->|"Stale or failed"| SAFETY
    DISABLE --> SAFETY
    GATE -->|"Speaking"| DOWN
    GATE -->|"Quiet after release"| NORMAL
    DOWN --> WRITE
    NORMAL --> WRITE
    SAFETY --> WRITE
    WRITE --> STATE

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef external fill:#fff0d8,stroke:#9b6218,color:#51330e
    class TICK,FRESH,GATE,DOWN,NORMAL,SAFETY model
    class WRITE external
    class DISABLE,STATE native
```

Each playback session has four different values:

| Value | Meaning | Changes when |
| --- | --- | --- |
| Startup | Safety restoration level | First observation only |
| Normal | User's ordinary listening volume | An explicit slider or current external mixer command |
| Target | Current fade destination | Detection, normal volume or settings change |
| Actual | Windows session gain | Fresh readback |

For normal volume 36% and reduction 50%, voice targets 18% and silence targets
36%. The slider renders Actual and sends an explicit playback-volume command.
The worker consumes that intent with the gate and settings used for the same
playback update. During lowering it derives Normal from requested Actual divided
by the configured gain, clamped to 100%; otherwise the request becomes Normal.
It never divides by an intermediate fade level. At 100% reduction, editing is
unavailable and a queued command is rejected without replacing Normal.
An unfinished fade never becomes a new normal volume. Disable and exit
restore Startup for sessions changed by the application, while keeping Normal
available for another Enable in the same application lifetime.

All Core Audio objects, session discovery, callback subscriptions and mixer
writes belong to one app-lifetime worker. Native callbacks publish only scalar
state and exclude this process's EventContext. The controller validates external
events against current native state before adopting a normal-volume change.
No monitoring tick writes a volume that is already applied. Muted restoration
waits; unavailable or failed restoration is reported rather than certified.

**Enable microphone on startup** is a saved opt-in independent of Windows
autostart. It applies to every new launch after playback initialization and license
checks. Changing the checkbox saves the next-launch preference; it does not start
capture immediately. The existing JSON key is preserved so current choices survive
an in-place update.

The isolated microphone, playback and application-model harnesses use generated
buffers, clocks, adapters and in-memory mixer state. They do not prove acoustic
behavior, device compatibility or visual acceptance.

> **File name** `AUDIO-MODEL.md`<br>
> **Full path** `E:\vibekit\Apps\VoiceDucker\docs\AUDIO-MODEL.md`<br>
> **Path relative to the VibeKit repository** `Apps\VoiceDucker\docs\AUDIO-MODEL.md`
