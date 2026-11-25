<!-- VBWR B

Project: UlanziDeck
Repository: https://github.com/umbertotechnopreneur/ulanzi-dock-vibeware
Creator: Umberto Giacobbi | https://umbertogiacobbi.biz

VibeWare: Human intent, AI execution, and plenty of tokens
Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto


Copyright (c) 2026 Umberto Giacobbi
SPDX-License-Identifier: MIT
License: MIT - see LICENSE

VBWR E -->

> **File name** INSTALLATION.md<br>
> **Full path** `E:\vibekit\Apps\UlanziDeck\docs\INSTALLATION.md`<br>
> **Path relative to the VibeKit repository root** `Apps/UlanziDeck/docs/INSTALLATION.md`

# Install and set up UlanziDeck

## Use the Windows installer

Choose the MSI for your Windows processor (x64 or ARM64) from the project build artifacts, or [build your own installer](#build-your-own-installer). The MSI installs UlanziDeck for your Windows account.

1. Open the MSI and wait for setup to finish.
2. Open **UlanziDeck** from Start.
3. Follow the first-run setup. Right-click the tray icon, then choose **Settings** to change your choices later.

The tray icon is the small app icon near the Windows clock. Starting the controller can update a connected dock. If startup fails, an error window explains why.

To start UlanziDeck at sign-in, use the app's startup setting or run `ulanzi-dock-vibeware.exe startup --enable` from its installation folder. Use `startup --disable` before uninstalling if you enabled sign-in startup.

A locally signed installer needs a trusted certificate from its maker. Check who made it before trusting that certificate.

## Run without installing

Extract the whole Windows ZIP into a writable folder. Open `ulanzi-dock-vibeware.exe`. Keep `ulanzi-dock-launcher.exe` beside it for startup at sign-in.

On Windows, `run`, `configure`, `brightness` and portable sign-in startup use the default profile beside the app. Use `--config` with `run`, `configure` and `brightness` to work with another profile.

The Debian package adds UlanziDeck to the Linux application menu. Portable Linux archives use a terminal. See the [platform guide](PLATFORMS.md).

## Change and back up settings

On Windows, choose **Settings** from the tray menu. On Linux, open UlanziDeck from the application menu or choose **Settings** from its tray icon where supported. Theme and enabled pages update while the controller runs. On Windows, foreground preferences also update live. Restart the controller after changing app mappings or shortcuts.

The settings buttons open two text files:

- `settings.json`: pages, button shortcuts, brightness and automatic switching preferences.
- `applications.yaml`: application names, executable names and optional app commands.

The controller also keeps the last regular page in `settings.session.json`, beside `settings.json`. The selected theme stays in `settings.json`. On the next run, the boot picture fades out and that page and theme return. A removed or disabled page falls back to the normal home page, and the temporary page index is not saved. With a custom settings path such as `my-dock.json`, the session file is `my-dock.session.json`.

Office shortcuts start with US English defaults. Adjust them for your keyboard and application language.

The installed app saves these files beside the executables in `%LOCALAPPDATA%\Programs\UlanziDeck\`. The portable app uses its extracted folder. Local builds in `artifacts/` use the repository's settings.

Updates keep existing preferences. Back up the two settings files, the application catalogue and the session file before uninstalling or resetting. If a file uses an unsupported format, keep it as a reference and create a separate current file with `init --config new-settings.json`. Copy the shortcuts you want into that file; use the current application catalogue too.

### Add OBS Creator to existing settings

Fresh settings already contain OBS Creator. An update does not insert it into a settings file you already use. Close the app and follow [Add the page](../presets/obs-creator/README.md#add-the-page), using your actual settings path. The preset installer makes a backup.

With automatic switching on, OBS must be the app in front to select its page. Having it open in the background is not enough.

## Build your own installer

Follow [development setup](DEVELOPMENT.md). Install WiX 5.0.2 and have Windows SDK SignTool available for signing.

Import the [shared VibeWare development certificate](../../../docs/windows-development-signing.md) first.
The script selects the registered certificate with publisher `CN=Umberto Giacobbi`.

```powershell
dotnet tool install --global wix --version 5.0.2
cargo build --locked --release --bins --target x86_64-pc-windows-msvc
pwsh -NoProfile -File scripts/ulanzi.ps1 -Command package -Target x86_64-pc-windows-msvc -Label windows-x64 -Tag v0.4.0
```

Use the version in `Cargo.toml` for the tag. The package command writes the ZIP, MSI and their checksums to `dist/`. Pass `-WixPath` if WiX is outside PATH.

Use `aarch64-pc-windows-msvc` and `windows-arm64` for a native ARM64 package. Install the matching Rust target, Visual C++ tools and SDK libraries first.

Double-click the MSI to install it. For unattended installation, use `msiexec /i <package.msi> /quiet /norestart`.

See [platform coverage](PLATFORMS.md) for what has been checked on real hardware.

## Controller logs

When started through the Windows launcher, each run writes a local log in the
`logs/` folder beside settings.json. It includes errors, page/theme changes and
dock button events. Share only the relevant excerpt when reporting a problem;
the header includes your local settings path. USB write completion alone does
not prove that the device screen changed.

> **File name** INSTALLATION.md<br>
> **Full path** `E:\vibekit\Apps\UlanziDeck\docs\INSTALLATION.md`<br>
> **Path relative to the VibeKit repository root** `Apps/UlanziDeck/docs/INSTALLATION.md`
