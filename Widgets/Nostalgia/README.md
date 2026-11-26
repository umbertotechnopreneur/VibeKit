> **File name** `README.md`<br>
> **Full path** `E:\vibekit\Widgets\Nostalgia\README.md`<br>
> **Path relative to the VibeKit repository root** `Widgets/Nostalgia/README.md`

# Nostalgia desktop effects

Water Ripple, Vignette, Retro Terminal, CRT Degauss and Retro Sound are available as a suite and as individual packages for Windows and Debian-based Linux. Each effect owns its settings, engine, icon and release version.

| Location | Responsibility |
| --- | --- |
| [Shared/Windows](Shared/Windows/) | WinUI 3 library for the studio layout, About, close dialog, tray presentation and display selection. |
| [Shared/Native](Shared/Native/) | Windows bridge between the CRT Degauss and Retro Sound WinUI studios and their C++ engines. |
| [Shared/Linux](Shared/Linux/) | GNOME Shell integration and Linux preferences components. Retro Sound has its own Python/GTK process. |
| [Shared/Assets](Shared/Assets/) | VibeWare artwork and its license notice. Product-specific icons, recordings and shaders live with the effect. |
| [build](build/) | Package entry points and staging conventions. |

## Build from source

Run these commands from this directory. Windows requires the .NET 10 SDK, WinUI build tools, MSVC x64, the Windows SDK, Python and WiX 5.0.2. The local channel signs the executable and MSI with the registered VibeWare development certificate. Use `-Channel public` to produce the unsigned Windows distribution.

```powershell
.\build\Build-Windows.ps1 -Widget CrtDegauss -WixPath <path-to-wix.exe>
```

Replace `CrtDegauss` with any product directory name. The default destination for this example is `artifacts/packages/local/windows/x64/CrtDegauss/`.

To build the five individual MSI files and the suite MSI in one run:

```powershell
.\build\Build-Windows.ps1 -Widget All -WixPath <path-to-wix.exe>
```

The suite uses the x64 .NET Desktop Runtime 10 because Vignette requires it. The MSI installs five distinct Start menu entries and has one uninstall entry, **Nostalgia**. Uninstall the individual effects before installing the suite. To return to individual packages, uninstall the suite first.

On a Debian build host, invoke the product's Linux builder. The four GNOME extensions use Debian architecture `all`. Retro Sound compiles its mixer for the host architecture. The builders require the Debian packaging tools and the dependencies declared by each product.

```sh
python3 RetroSound/Linux/build-package.py
```

To build the five individual Debian packages and a single Debian package that contains all five payloads:

```sh
python3 build/build-linux.py
```

For example, on amd64 install the suite with:

```sh
sudo apt install ./artifacts/packages/local/linux/amd64/Nostalgia/vibeware-nostalgia_1.0.0_amd64.deb
```

APT resolves the operating-system dependencies, including GNOME Shell 50. The suite contains four extensions and the Retro Sound GTK application. The package installs the launchers and extensions. GNOME controls when each extension is enabled. The suite and the individual packages are alternative installation choices.

All builders accept `NOSTALGIA_ARTIFACTS` to change the artifact root and `NOSTALGIA_CHANNEL` to select `local` or `public`. Override just the final destination with `-OutputDirectory` on Windows or `--output-dir` on Linux. Keep payloads separate per effect: the native Windows engines use the same DLL filename.

| Under `artifacts/` | Contents |
| --- | --- |
| `packages/<channel>/<os>/<arch>/<product>/` | Completed packages. The filename identifies the product and package version. |
| `work/` | Project-specific compiler, restore and native build intermediates. |
| `stage/` | Private packaging runs. A completed installer replaces its destination only after packaging and any required signing succeed. |

## Add an effect

Create a sibling product directory. On Windows, reference [Nostalgia.WinUI](Shared/Windows/Nostalgia.WinUI.csproj), supply the product identity and current icon, and connect the shared close choices to the engine's quit and tray actions. Add one [Windows catalog entry](build/windows-products.json) for the project, installer identity, runtime and artwork. Both the compiler entry point and the MSI generator read it. On Linux, supply the appropriate GNOME extension or GTK application and use the [Linux package workspace](build/linux_package.py). Register the product and any shared-source dependencies in the [CI selector](../../scripts/ci/select_projects.py).

Runtime coordination is a separate interface from UI sharing. Windows effects participate once per process using the [public desktop-effects contract](../Shared/desktop-effects-contract.md). The Linux extensions share shell-session modules; Retro Sound manages its audio device and process separately.

> **File name** `README.md`<br>
> **Full path** `E:\vibekit\Widgets\Nostalgia\README.md`<br>
> **Path relative to the VibeKit repository root** `Widgets/Nostalgia/README.md`
