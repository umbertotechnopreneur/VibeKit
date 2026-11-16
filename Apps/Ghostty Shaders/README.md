# Ghostty Shaders

A collection of GLSL post-processing effects for [Ghostty](https://ghostty.org/docs/config/reference#custom-shader). Ghostty is the first target; other terminals and compositors need their own integration.

## Built with care

Visual effects should earn their GPU time. I pay attention to rendering cost and review third-party sources and attribution; actual performance depends on the effect and your hardware.

## Effects

- [CRT](shaders/crt.glsl): configurable curvature, overscan, bloom, blur, grayscale, black level, refresh line, scanlines, tint and grain. Its defaults follow the locally customized Windows Terminal CRT shader. The GLSL adaptation is not pixel-identical: Ghostty has different coordinate, scale and background inputs; overscan outside the image is opaque black, and scanline width is specified in physical pixels.
- [Soft CRT](shaders/soft-crt.glsl): static, subtle scanlines and edge shading that preserve the terminal's colors.

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

## Verification

Run `python3 tests/check-manager.py` to exercise the PowerShell menu, all presets, persistence, duplicate protection, editor argument handling, disable and malformed-marker handling in temporary directories. It requires PowerShell 7 and Python 3, with no third-party Python packages.

The five presets were smoke-tested in Ghostty under Xvfb with Mesa software rendering and visually inspected as a contact sheet. The renderer emits an uninitialized-parameter warning for the Arcade CRT preset; no compilation failure was observed. Hardware GPU performance and different display scales remain to be checked.
