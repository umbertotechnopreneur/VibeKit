> **File name:** `README.md`<br>
> **Full path (workspace file):** `E:\vibekit\README.md`<br>
> **Full path (published document):** `https://github.com/umbertotechnopreneur/VibeKit/blob/main/README.md`<br>
> **Relative path (VibeKit repository root):** `README.md`

<h1 align="center">VibeKit</h1>

<p align="center">
  <strong>Small tools. Real problems.</strong><br>
  Your computer deserves a little breathing room.
</p>

<p align="center">
  By <a href="https://umbertogiacobbi.biz">Umberto Giacobbi</a> and contributors<br>
  <a href="https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml"><img src="https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main" alt="VibeKit CI"></a>
</p>

Music should make room for your voice. A desk controller should put useful buttons under your fingers.

VibeKit is my workbench for turning those everyday annoyances into applications and desktop utilities. Built with AI, guided by human judgment, and open for you to explore, adapt and improve.

<p align="center">
  <a href="#find-your-next-tool"><strong>Choose a project</strong></a> ·
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery">Take the visual tour</a> ·
  <a href="#want-to-help">Contribute</a> ·
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">VibeWare manifesto</a>
</p>

## Meet the projects

A little hardware, a few desktop windows, and plenty of curiosity. **[Open the 20-image carousel](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)** for the large images and full-screen gallery.

| | | | |
| --- | --- | --- | --- |
| [![Cody, the round-screen AI companion](docs/assets/showcase/thumbs/01-cody.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**Cody** | [![Cody showing its QR pairing screen](docs/assets/showcase/thumbs/02-cody-pairing.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**Cody · pairing** | [![VoiceDucker microphone monitoring and application volume controls](docs/assets/showcase/thumbs/03-voiceducker.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**VoiceDucker** | [![VoiceDucker volume timing and restoration controls](docs/assets/showcase/thumbs/04-voice-controls.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**VoiceDucker · controls** |
| [![UlanziDeck controls for a coding workspace](docs/assets/showcase/thumbs/05-ulanzi-code.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**UlanziDeck · code** | [![UlanziDeck music and playback controls](docs/assets/showcase/thumbs/06-ulanzi-music.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**UlanziDeck · music** | [![ThermalRight Panel with system readings](docs/assets/showcase/thumbs/07-thermal-system.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**ThermalRight · system** | [![ThermalRight Panel with music and album artwork](docs/assets/showcase/thumbs/08-thermal-music.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**ThermalRight · music** |
| [![HelpMe terminal command suggestions and explanations](docs/assets/showcase/thumbs/13-helpme-command.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**HelpMe · commands** | [![HelpMe terminal help and command guide](docs/assets/showcase/thumbs/14-helpme-guide.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**HelpMe · guide** | [![WorkTrail world map with the boundary between day and night](docs/assets/showcase/thumbs/15-worktrail-map.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**WorkTrail · day & night** | [![WorkTrail Earth globe desktop view](docs/assets/showcase/thumbs/16-worktrail-globe.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**WorkTrail · globe** |
| [![MarkVibe Markdown reading workspace design](docs/assets/showcase/thumbs/17-markvibe.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**MarkVibe** | [![Gauge desktop AI usage widget design](docs/assets/showcase/thumbs/18-gauge.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**Gauge** | [![Desktop Ripple water effect over an alpine desktop](docs/assets/showcase/thumbs/19-ripple-alpine.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**Ripple · alpine** | [![Desktop Ripple water effect over a violet desktop](docs/assets/showcase/thumbs/20-ripple-violet.webp)](https://umbertogiacobbi.biz/vibeware/manifesto#vibekit-gallery)<br>**Ripple · violet** |

**Cody is a companion project, not part of this monorepo.** Its round-screen device and AI integrations live in [their own repository](https://github.com/umbertotechnopreneur/esp32-luna-shell). The rest of this tour comes from the VibeKit projects below.

## Find your next tool

Each project has its own setup, maturity and limitations. Start with its README. CI badges show the shared VibeKit workflow, whose selected checks depend on the changed projects.

### Applications

#### [Repo Radar](Apps/RepoRadar/README.md)

Scan a drive for Git repositories and review local changes, remote commits and branch status in terminal cards or a detailed table. By Umberto Giacobbi.biz. [MIT license](Apps/RepoRadar/LICENSE).

#### [VoiceDucker](Apps/VoiceDucker/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows_11-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/VoiceDucker/LICENSE)

**“Let me talk to my AI.”** Lower selected playing apps when the microphone detects sound, then bring their volume back gradually.

#### [UlanziDeck](Apps/UlanziDeck/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows · Linux](https://img.shields.io/badge/platform-Windows_%C2%B7_Linux-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/UlanziDeck/LICENSE)

**“That action deserves a button.”** Put apps, media controls and shortcuts on an Ulanzi D200H.

#### [ThermalRight Panel](Apps/ThermalRight%20Panel/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/ThermalRight%20Panel/LICENSE)

**“That little display could do more.”** Show music, artwork and system readings on supported Thermalright displays.

#### [HelpMe](Apps/HelpMe/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows · Linux](https://img.shields.io/badge/platform-Windows_%C2%B7_Linux-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/HelpMe/LICENSE)

**“I know what I want; I forgot the command.”** Describe a task in your terminal, read the explanation and review suggested commands.

#### [MarkVibe](Apps/MarkVibe/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · WebAssembly](https://img.shields.io/badge/platform-Windows_11_%C2%B7_WebAssembly-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/MarkVibe/LICENSE)

**“I just want to read this Markdown file.”** Open local documents with a document map, optional AI editing and HTML/PDF export.

#### [WorkTrail](Apps/WorkTrail/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows_11-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Apps/WorkTrail/LICENSE)

**“Is this a good time over there?”** Compare time zones and working hours, with world clocks, calendars and celestial desktop views.

#### [Ghostty Shaders](Apps/Ghostty%20Shaders/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Linux · Ghostty](https://img.shields.io/badge/platform-Linux_%C2%B7_Ghostty-0078D4)
[![License: see notices](https://img.shields.io/badge/license-see_notices-8A6D3B)](Apps/Ghostty%20Shaders/README.md#attribution-and-status)

**“A little phosphor, please.”** Add configurable CRT or subtle scanline effects to Ghostty through GLSL shaders.

### Desktop utilities

#### [Studio Controls](Widgets/StudioControls/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · Linux](https://img.shields.io/badge/platform-Windows_11_%C2%B7_Linux-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Widgets/StudioControls/LICENSE)

A native OBS control desk with twelve scenes, screen zoom, independent vignettes and editable project cards. Windows ZIP and Linux Debian preview packages include generic, device-unassigned scene templates.

#### [Gauge / OpenAI Usage](Widgets/OpenAI/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · Linux](https://img.shields.io/badge/platform-Windows_11_%C2%B7_Linux-0078D4)
[![License: see notes](https://img.shields.io/badge/license-see_notes-8A6D3B)](Widgets/OpenAI/README.md#licensing)

Keep AI usage visible in a desktop widget. Other providers are a future goal.

### [Nostalgia desktop effects](Widgets/Nostalgia/README.md)

Water Ripple, Vignette, Retro Terminal, CRT Degauss and Retro Sound install independently on Windows and Linux.

#### [Desktop Ripple](Widgets/Nostalgia/WaterRipple/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · GNOME](https://img.shields.io/badge/platform-Windows_11_%C2%B7_GNOME-0078D4)
[![License: see notes](https://img.shields.io/badge/license-see_notes-8A6D3B)](Widgets/Nostalgia/WaterRipple/README.md#licensing)

Give the desktop a water-like ripple effect; see also the [Linux edition](Widgets/Nostalgia/WaterRipple/Linux/README.md).

#### [CRT Filter / Retro Terminal](Widgets/Nostalgia/RetroTerminal/README.md)

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · GNOME 50](https://img.shields.io/badge/platform-Windows_11_%C2%B7_GNOME_50-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](Widgets/Nostalgia/RetroTerminal/LICENSE)

Give the GNOME desktop green phosphor, amber or arcade color, with controls for the effect and an emergency stop.

## Small tools, serious care

I prefer software that earns its place on your computer. That means native platform experiences, attention to memory and CPU use, and meaningful checks rather than promises about performance. Automatic features should explain what they are doing, respect a manual stop and leave you in control.

AI helps me build. I choose the problems, guide the work and remain responsible for reviewing the result. I review dependencies and their sources, investigate bottlenecks and document remaining limits. Read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto) for the thinking behind the work.

## Inside the kit

- **[Apps/](Apps/)** — nine self-contained applications, each with its own documentation and scripts.
- **[Widgets/](Widgets/)** — small desktop utilities and platform-specific implementations.
- **[scripts/](scripts/)** — shared, project-independent shell utilities.
- **[docs/](docs/)** — repository documentation, [import notes](docs/project-imports.md) and [CI guidance](docs/ci.md).

A change to an app should run that app's checks. [VibeKit CI](.github/workflows/monorepo.yml) selects pipelines by changed directories; the [CI and release guide](docs/ci.md) covers selection rules, namespaced release tags and publishing requirements.

Imported projects retain their histories and attribution. Each application keeps its own license; this collection does not relicense third-party code or artwork.

## Want to help?

Try a tool, report a reproducible bug, improve the documentation or contribute support for hardware we cannot test. Include the project, platform and steps to reproduce in an issue. Leave private information and credentials out of reports.

Our studio develops these projects in spare time and through its **20% time rule**, giving team members and collaborators room for personal projects. We share the work freely; community help lets it go further than our available time.

> [!TIP]
> **Have a Mac and a little time to contribute?**
>
> These tools target Windows and Linux as documented by each project. **macOS is unsupported** because I do not have a Mac for development or testing.
>
> Pick a project you would like to maintain and [open an issue](https://github.com/umbertotechnopreneur/VibeKit/issues) with its name and your proposed contribution. Native integrations, packaging and checks on real hardware are all useful ways to help.

Made by [Umberto Giacobbi](https://umbertogiacobbi.biz) and contributors, with curiosity and a healthy suspicion of apps that need half your RAM to show one button.

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created VibeKit with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[VibeKit on GitHub](https://github.com/umbertotechnopreneur/VibeKit) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore, adapt and reuse the open-source VibeWare materials under their respective licenses and notices — my contribution to the developer community.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="https://raw.githubusercontent.com/umbertotechnopreneur/VibeWare/main/Branding/vibeware-logo.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>

> **File name:** `README.md`<br>
> **Full path (workspace file):** `E:\vibekit\README.md`<br>
> **Full path (published document):** `https://github.com/umbertotechnopreneur/VibeKit/blob/main/README.md`<br>
> **Relative path (VibeKit repository root):** `README.md`
