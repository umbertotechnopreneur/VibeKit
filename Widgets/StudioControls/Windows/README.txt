STUDIO CONTROLS — WINDOWS 11 x64

Open the MSI and then choose Studio Controls in Start. The installer includes
the .NET and WinUI runtimes, assets, scene template and licences. Install OBS
Studio separately. The installed utility runs without Python.


FIRST USE
1. Open OBS and enable its WebSocket server with password authentication.
2. In Studio Controls, enter the local host/port/password and choose Connect.
3. Choose Prepare OBS scene collection. Import the NEW file through OBS's
   Scene Collection > Import menu. Existing collections are not overwritten.
4. In OBS, select your screen, webcam, hardware camera and audio devices in
   source Properties. The portable collection begins on Privacy, muted, with
   capture devices unassigned. Enable only the inputs you intend to use.
5. Use Project & cards to prepare the introduction, credits and links. For
   native camera framing, explicitly refresh/select a camera and Open controls.

The controller never starts OBS, recording or a recording resume automatically.
Privacy hides video; audio stays under your explicit mute controls. Closing
the controller does not stop OBS. Check OBS itself if the connection is lost.

SETTINGS AND UPDATES
Preferences, DPAPI-protected passwords, generated cards and bounded diagnostics
are kept under the current user's LocalAppData\VibeKit\StudioControls folder.
Run the newer version of the MSI to update. The installer keeps preferences,
logs and generated project cards in the existing per-user location.
Your OBS recordings and existing scene collections remain managed by OBS.

VALIDATION
CI builds the native Windows application and can run synthetic artwork/settings
checks (--verify) and an isolated native UI/theme check (--ui-smoke). These modes
do not contact OBS, enumerate cameras or capture media. CI success is separate
from real-device compatibility: validate camera controls, monitor selection,
audio and a short recording manually on your Windows computer.

SUPPORT AND LICENSES
Source: https://github.com/umbertotechnopreneur/VibeKit
Website: https://umbertogiacobbi.biz/
Use Export diagnostics to create a ZIP excluding settings and credentials.
Keep LICENSE, THIRD-PARTY-NOTICES.md, Licenses/ and Assets/CREDITS.txt with the app.
The CRT footage attribution must remain visible in the Test card overlay.
