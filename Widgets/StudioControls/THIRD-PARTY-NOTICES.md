# Third-party notices

The MIT license covers the Studio Controls code and original artwork/cue by
Umberto Giacobbi. It does not relicense the following materials.

- `Assets/CRT-test.mp4`: ten-second, silent H.264 excerpt of **TV Test Patterns**
  by Ivan Bridgewater. Source: https://archive.org/details/IMB_SF_R30_C8 .
  License: https://creativecommons.org/licenses/by/3.0/ . The excerpt was
  trimmed and re-encoded; its audio was removed. Keep the attribution visible
  in the Test card overlay and in distributed credits.
- `Assets/OBS-logo.png`: official OBS Studio 512px icon, used to identify OBS
  Studio in the Made with OBS Studio credit. The PNG is byte-identical to
  [`frontend/cmake/linux/icons/obs-logo-512.png` in OBS Studio 33.0.0](https://github.com/obsproject/obs-studio/blob/33.0.0/frontend/cmake/linux/icons/obs-logo-512.png).
  It originally came from the system OBS package. The OBS Project / OBS Studio
  contributors distribute the upstream repository under
  [GPL-2.0-or-later](https://github.com/obsproject/obs-studio/blob/33.0.0/README.rst).
  The unchanged upstream [COPYING](https://github.com/obsproject/obs-studio/blob/33.0.0/COPYING)
  is included as `Assets/OBS-COPYING.txt`; these third-party terms apply to the
  logo asset, outside this project's MIT license. OBS, OBS Studio, Open Broadcaster
  Software and the OBS Studio logo are registered trademarks of Wizards of OBS LLC
  ([official site](https://obsproject.com/)). Studio Controls is independently
  developed; no affiliation or endorsement is claimed.
- `Assets/VibeWare-logo.png`: owner-supplied VibeWare branding.
  https://umbertogiacobbi.biz/vibeware/manifesto .
- `Linux/_vendor/websocket`: websocket-client 1.9.2; the complete upstream
  license is preserved in its distribution metadata under `_vendor`.
- Pillow, GTK/PyGObject, DejaVu fonts, Microsoft Windows App SDK and .NET are
  separate runtime/build dependencies, under their own licenses. No
  obs-backgroundremoval binary or model is redistributed here.

The Windows ZIP and MSI include the WinUI runtime files. Both use the shared
Microsoft .NET Runtime 10 installed for the target architecture. Upstream licence/notice files from the resolved NuGet packages are kept
under `Licenses/`, together with a package/version inventory containing public
package URLs. This inventory contains no local NuGet cache paths.

See `Assets/CREDITS.txt` for the media provenance and edit details.
