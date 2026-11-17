# Ghostty Shaders

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Linux · Ghostty](https://img.shields.io/badge/platform-Linux_%C2%B7_Ghostty-0078D4)
[![License: see notices](https://img.shields.io/badge/license-see_notices-8A6D3B)](README.md#attribution-and-status)

A collection of GLSL post-processing effects for [Ghostty](https://ghostty.org/docs/config/reference#custom-shader). Ghostty is the first target; other terminals and compositors need their own integration.

## Built with care

Visual effects should earn their GPU time. I pay attention to rendering cost and review third-party sources and attribution; actual performance depends on the effect and your hardware.

## Effects

- [CRT](shaders/crt.glsl): configurable curvature, overscan, bloom, blur, grayscale, black level, refresh line, scanlines, tint and grain. Its defaults follow the locally customized Windows Terminal CRT shader. The GLSL adaptation is not pixel-identical: Ghostty has different coordinate, scale and background inputs; overscan outside the image is opaque black, and scanline width is specified in physical pixels.
- [Soft CRT](shaders/soft-crt.glsl): static, subtle scanlines and edge shading that preserve the terminal's colors.

> [!NOTE]
> **Ghostty on your Mac? Help check the phosphor.**
>
> This collection and its manager are maintained for Linux. **macOS support is not maintained or tested here**, because I do not have a Mac. Ghostty itself being available there does not validate these shaders or the manager.
>
> [Contributors with a Mac are welcome](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BGhostty%20Shaders%5D%20), especially for shader compilation, display scaling and configuration handling. Please include your Ghostty version and graphics hardware.

## Interactive manager (Linux)

Requires PowerShell 7 (`pwsh`) and Ghostty. From this project directory:

```sh
pwsh -NoProfile -File scripts/ghostty-shaders.ps1
```

The **UmbertoCiccobi.biz Retro Lab** menu uses colors and emoji, with these actions:

| Key | Action |
| --- | --- |
| S | Select a bundled shader or personal copy |
| P | Edit numeric parameters and switches |
| A | Apply the selected shader and parameter changes |
| C | Duplicate the current shader, including pending parameters |
| E | Open a personal copy in your preferred editor, then reload it |
| R | Reload the selected source, discarding pending parameter changes |
| D | Disable the managed effect |
| Q | Quit |

Five stylized presets are included; they evoke old monitors rather than emulate their hardware:

| Preset | Look |
| --- | --- |
| `commodore-64` | Blue background, lavender text, subtle scanlines |
| `apple-ii` | Green phosphor, soft glow and scanning refresh |
| `amber-vt` | Warm amber phosphor with light grain |
| `ibm-green` | Crisp green text with static scanlines; no animation loop |
| `arcade-crt` | Original terminal colors, blur, bloom and stronger scanlines |

The first four map source luminance to foreground/background colors: semantic ANSI colors are intentionally reduced to monochrome. Glow is spatial, not phosphor persistence across frames. All presets are self-contained commented GLSL files under `shaders/`, and can be edited directly. Use the menu's palette mix, background RGB and tint RGB controls to change their colors. Tint RGB also supplies the foreground of the retro palette even when the separate tint switch is off.

Personal copies are stored under `$XDG_CONFIG_HOME/ghostty/vibekit-shaders` (or `~/.config/ghostty/vibekit-shaders`). Duplication never overwrites an existing name. Editor changes are saved to the personal copy; parameter changes are written to the active generated shader on Apply, or included when duplicating. Selecting a different shader or reloading its source discards unapplied parameters. Restarting the manager loads the last applied shader.

The editor uses `-Editor` / `-EditorArguments` when supplied, otherwise `$VISUAL`, `$EDITOR`, then Linux's `xdg-open` association. Environment preferences accept an executable and literal arguments, for example `code --wait`; shell pipelines and substitutions are not evaluated. After saving, return to the menu and press Enter to load the file, then **A** to apply it. Shipped presets are copied before editing. If there is no associated editor, set `VISUAL` or `EDITOR` first.

```sh
# Apply one preset without opening the menu.
pwsh -NoProfile -File scripts/ghostty-shaders.ps1 -Action Apply -Preset commodore-64
# Choose an explicit editor executable.
pwsh -NoProfile -File scripts/ghostty-shaders.ps1 -Editor nano
```

Switches use `0` or `1`; numeric options show their allowed range. Grayscale modes are intensity (0), gleam (1), luminance (2), and luma (3); as in the source, bloom falls back to luminance for gleam and luma.

The manager writes a generated `vibekit-crt.glsl` under `$XDG_CONFIG_HOME/ghostty` (or `~/.config/ghostty`) and adds a marked block to Ghostty's `config` if it exists, otherwise `config.ghostty`. It leaves existing shader entries in place: Ghostty chains shaders in configuration order. It creates a timestamped backup before changing an existing configuration. Included configuration files may still change the final settings. Reload Ghostty with **Ctrl+Shift+,** on Linux, or open a new window. The menu does not close or restart your session.

For automation, the manager also supports `-Action Apply`, `-Action Disable` and `-Action Status`. `-ConfigRoot` can point to an isolated XDG configuration directory for testing. Animated refresh and grain enable Ghostty's animation loop; disabling both turns it off for this managed block. Existing later configuration may override it.

If the effect makes the terminal unreadable, run the manager with `-Action Disable`, or remove the block between `# BEGIN VIBEKIT GHOSTTY SHADERS` and `# END VIBEKIT GHOSTTY SHADERS` from the config and reload. The generated shader is retained for restoring your values. Compilation errors appear in Ghostty's logs.

To try Soft CRT without changing configuration:

```sh
ghostty --custom-shader="$PWD/shaders/soft-crt.glsl" --custom-shader-animation=false
```

## Attribution and status

CRT is adapted from the locally modified `crt.hlsl` in [Hammster/windows-terminal-shaders](https://github.com/Hammster/windows-terminal-shaders), based on revision `2f56929`; the original collection's CC0 text and the Microsoft Terminal MIT notice for scanline-derived code are in [licenses](licenses/). The local shader has modifications after that revision. No other collection shaders have been ported. Visual appearance and performance depend on display scale, colors, GPU and transparency; CRT effects can reduce text legibility.

The notices in `licenses/` cover the attributed upstream material; this folder does not declare a single license for the whole collection.

## Verification

Run `python3 tests/check-manager.py` to exercise the PowerShell menu, all presets, persistence, duplicate protection, editor argument handling, disable and malformed-marker handling in temporary directories. It requires PowerShell 7 and Python 3, with no third-party Python packages.

The five presets were smoke-tested in Ghostty under Xvfb with Mesa software rendering and visually inspected as a contact sheet. The renderer emits an uninitialized-parameter warning for the Arcade CRT preset; no compilation failure was observed. Hardware GPU performance and different display scales remain to be checked.

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created Ghostty Shaders with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[Ghostty Shaders on GitHub](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/Ghostty%20Shaders) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore the VibeWare materials. Reuse is governed by the applicable licenses and notices; this footer does not grant a software license.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="https://raw.githubusercontent.com/umbertotechnopreneur/VibeWare/main/Branding/vibeware-logo.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>
