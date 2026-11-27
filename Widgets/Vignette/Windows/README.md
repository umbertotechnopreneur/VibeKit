# Vignette for Windows

Install **.NET Desktop Runtime 10** for the same architecture as Vignette (x64 or ARM64). Vignette uses WPF and Windows Forms. The MSI checks this prerequisite during installation. The portable ZIP uses the same shared runtime. [Microsoft .NET 10 runtime downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0/runtime)

To install Vignette for your user account, open the MSI. To use the portable edition, extract the ZIP into an empty directory and run `Vignette.exe`.

From this directory, build both Release packages for one architecture with PowerShell, the .NET 10 SDK, Python 3 and WiX 5.0.2.

```powershell
.\Packaging\Build-MSI.ps1 -WixPath (Get-Command wix).Source -Architecture arm64
```

Use `-Architecture x64` for x64. Both packages appear in `Widgets/Vignette/dist/`.
