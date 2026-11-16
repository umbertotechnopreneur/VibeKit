# Project assets

## Application icon

`HelpMe.ico` is the Windows executable icon: a geometric `hm_` terminal mark on a dark tile. It contains 32-bit PNG frames at 16, 24, 32, 48, 64, 128, and 256 pixels, including transparent corners.

The 128- and 256-pixel frames have a Matrix-inspired green phosphor treatment: green `#68FF90` on near-black `#020805`, with restrained glow and scanlines. The smaller frames retain the original cyan `#67E8F9` and navy `#0B1220` artwork unchanged for clarity at small sizes. MSIX tiles are derived from the 256-pixel frame.

The icon was drawn for HelpMe with simple vector strokes. It uses no font files, external artwork, or image-generation service. The icon and its source are covered by the repository's [MIT license](../LICENSE).

To reproduce the icon on Windows with PowerShell 7:

```powershell
pwsh -NoProfile -File .\assets\generate-icon.ps1 -PreviewDirectory .\artifacts\icon-preview
```

The script replaces only `assets/HelpMe.ico` and writes optional PNG previews under the ignored `artifacts` directory. It uses Windows' built-in drawing APIs and installs nothing. `HelpMe.csproj` references the icon through `ApplicationIcon` so subsequent builds include it in the executable.
