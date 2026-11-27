"""Attach the common VibeWare UI and .NET prerequisite to an authored WiX MSI."""

import argparse
import json
from pathlib import Path
import re
import struct
import xml.etree.ElementTree as ET


WIX = "{http://wixtoolset.org/schemas/v4/wxs}"
NETFX = "http://wixtoolset.org/schemas/v4/wxs/netfx"
CONTACT = "https://umbertogiacobbi.biz/contacts/"
RUNTIME_FILES = {
    "coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll", "system.private.corelib.dll",
    "presentationcore.dll", "presentationframework.dll", "wpfgfx_cor3.dll", "system.windows.forms.dll",
}
RUNTIME_NAMES = {"core": "Microsoft.NETCore.App", "desktop": "Microsoft.WindowsDesktop.App"}
ARCHITECTURES = {"x64": "X64", "arm64": "A64"}


def write_setup_icon(logo, output):
    """Embed the original 64-pixel ICO frame without resizing or re-encoding it."""
    source = logo.read_bytes()
    header = struct.Struct("<HHH")
    directory = struct.Struct("<BBBBHHII")
    reserved, icon_type, count = header.unpack_from(source)
    directory_end = header.size + count * directory.size
    if (reserved, icon_type) != (0, 1) or directory_end > len(source):
        raise ValueError(f"Invalid ICO directory: {logo}")
    for entry in directory.iter_unpack(source[header.size:directory_end]):
        length, offset = entry[-2:]
        if entry[:2] == (64, 64) and length and directory_end <= offset <= len(source) - length:
            with output.open("xb") as icon:
                icon.write(header.pack(0, 1, 1))
                icon.write(directory.pack(*entry[:-1], header.size + directory.size))
                icon.write(source[offset:offset + length])
            return
    raise ValueError(f"No valid 64-pixel frame in {logo}")


def read_runtime(payload, executable, architecture, runtime_type="core"):
    """Validate a framework-dependent publish and return the minimum supported .NET patch."""
    app = payload / executable
    options = json.loads(app.with_suffix(".runtimeconfig.json").read_text(encoding="utf-8-sig"))["runtimeOptions"]
    expected = RUNTIME_NAMES[runtime_type]
    frameworks = options.get("frameworks", [])
    if "framework" in options:
        if "frameworks" in options:
            raise ValueError(f"{app.name} declares conflicting runtime configuration fields.")
        frameworks = [options["framework"]]
    names = {f["name"] for f in frameworks}
    versions = [f["version"] for f in frameworks]
    if (options.get("tfm") != "net10.0" or expected not in names
            or names - {expected, RUNTIME_NAMES["core"]} or len(names) != len(frameworks)
            or not all(re.fullmatch(r"10\.0\.[0-9]+", version) for version in versions)
            or "includedFrameworks" in options
            or options.get("rollForward", "Minor") not in ("Minor", "LatestPatch")
            or options.get("applyPatches", True) is not True):
        raise ValueError(f"{app.name} requires a framework-dependent {expected} 10.0 publish.")

    for file in payload.rglob("*"):
        if file.name.casefold() in RUNTIME_FILES:
            raise ValueError(f"{app.name} cannot include shared runtime file {file.relative_to(payload)}.")
    dependencies = json.loads(app.with_suffix(".deps.json").read_text(encoding="utf-8-sig"))
    if not dependencies["runtimeTarget"]["name"].endswith(f"/win-{architecture}"):
        raise ValueError(f"{app.name} requires a Windows {architecture} publish.")
    for library in dependencies.get("libraries", {}):
        if library.casefold().startswith(("runtimepack.microsoft.netcore.app.", "runtimepack.microsoft.windowsdesktop.app.")):
            raise ValueError(f"{app.name} declares a bundled runtime pack: {library}")
    return max(versions, key=lambda version: int(version.rsplit(".", 1)[1]))


def add_installer_ui(root, *, payload, executable, output, home_url, logo, architecture="x64", runtime_type="core"):
    """Add common native dialogs, metadata and the WiX 5 NetFx check to one package."""
    package = root.find(WIX + "Package")
    version = read_runtime(Path(payload), executable, architecture, runtime_type)
    runtime_label = ".NET Desktop Runtime" if runtime_type == "desktop" else ".NET Runtime"
    scope = "Per-machine installation" if package.get("Scope") == "perMachine" else "Per-user installation"
    display_version = "10.0" if version == "10.0.0" else version
    properties = {
        "VibeWareInstallScope": scope,
        "VibeWareRuntimeDescription": f"Microsoft {runtime_label} {display_version} ({architecture})",
        "ARPURLINFOABOUT": home_url,
        "ARPHELPLINK": CONTACT,
    }
    for name, value in properties.items():
        prop = package.find(f"{WIX}Property[@Id='{name}']")
        if prop is None:
            prop = ET.SubElement(package, WIX + "Property", Id=name)
        prop.set("Value", value)

    ET.register_namespace("", WIX[1:-1])
    ET.register_namespace("netfx", NETFX)
    ET.SubElement(package, f"{{{NETFX}}}DotNetCompatibilityCheck",
                  Property="VibeWareDotNetResult", Platform=architecture, RuntimeType=runtime_type,
                  RollForward="latestPatch", Version=version)
    action = f"override Wix4NetFxDotNetCompatibilityCheck_{ARCHITECTURES[architecture]}"
    for name, condition in (("InstallUISequence", "UILevel = 5 AND NOT Installed"),
                            ("InstallExecuteSequence", "NOT Installed")):
        sequence = package.find(WIX + name)
        if sequence is None:
            sequence = ET.SubElement(package, WIX + name)
        ET.SubElement(sequence, WIX + "Custom", Action=action, Before="LaunchConditions", Condition=condition)

    icon = Path(output).resolve().with_suffix(".setup.ico")
    write_setup_icon(Path(logo), icon)
    ET.SubElement(package, WIX + "Binary", Id="VibeWareSetupIcon", SourceFile=str(icon))
    ET.SubElement(package, WIX + "UIRef", Id="VibeWareInstallerUI")
    root.extend(ET.parse(Path(__file__).with_name("VibeWareInstallerUI.wxs")).getroot())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Verify an external .NET 10 runtime in a Windows publish directory.")
    parser.add_argument("payload", type=Path)
    parser.add_argument("executable")
    parser.add_argument("architecture", choices=ARCHITECTURES)
    parser.add_argument("--runtime-type", choices=RUNTIME_NAMES, default="core")
    args = parser.parse_args()
    version = read_runtime(args.payload, args.executable, args.architecture, args.runtime_type)
    print(f"{args.executable} requires {RUNTIME_NAMES[args.runtime_type]} {version} ({args.architecture})")
