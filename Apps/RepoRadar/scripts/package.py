"""Build the Debian amd64 package and the Windows x64 portable ZIP."""
from __future__ import annotations

import argparse
import gzip
import io
from pathlib import Path
import tarfile
import zipfile
import xml.etree.ElementTree as ET

APP = Path(__file__).resolve().parents[1]
VERSION = ET.parse(APP / "src/RepoRadar.csproj").findtext(".//Version")
LICENSES = ("LICENSE", "THIRD-PARTY-NOTICES")
MANAGED = ("repo-radar.dll", "repo-radar.runtimeconfig.json", "repo-radar.deps.json",
           "Spectre.Console.dll", "Spectre.Console.Ansi.dll")
DEB_LAUNCH = b'#!/bin/sh\nexec dotnet /usr/lib/repo-radar/repo-radar.dll "$@"\n'


def tar_gz(files):
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode="w", format=tarfile.USTAR_FORMAT) as package:
        for name, (data, mode) in files.items():
            info = tarfile.TarInfo(name)
            info.mode, info.size = mode, len(data)
            info.uname = info.gname = "root"
            package.addfile(info, io.BytesIO(data))
    return gzip.compress(archive.getvalue(), mtime=0)


def deb_file(members):
    result = bytearray(b"!<arch>\n")
    for name, data in members.items():
        header = f"{name + '/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(data):<10}`\n"
        result.extend(header.encode("ascii") + data)
        if len(data) % 2:
            result.extend(b"\n")
    return bytes(result)


def write_package(path, data):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_bytes(data)
    temporary.replace(path)
    print(path)


def debian_package(publish, output):
    managed = {name: ((publish / name).read_bytes(), 0o644) for name in MANAGED}
    notices = {name: ((APP / name).read_bytes(), 0o644) for name in LICENSES}
    data = {f"./usr/lib/repo-radar/{name}": item for name, item in managed.items()}
    data["./usr/bin/repo-radar"] = (DEB_LAUNCH, 0o755)
    data["./usr/share/doc/repo-radar/copyright"] = (
        b"\n".join(notices[name][0] for name in LICENSES), 0o644)
    control = f"""Package: repo-radar
Version: {VERSION}-1
Architecture: amd64
Section: vcs
Priority: optional
Maintainer: Umberto Giacobbi <umbertotechnopreneur@users.noreply.github.com>
Depends: dotnet-runtime-10.0, git, libsqlite3-0
Conflicts: vibe-repo
Replaces: vibe-repo
Homepage: https://umbertogiacobbi.biz
Description: Repo Radar, a full-screen Git repository dashboard
 Discover nested Git workspaces and inspect their local and remote state.
 By Umberto Giacobbi.
"""
    write_package(output / f"repo-radar_{VERSION}-1_amd64.deb", deb_file({
        "debian-binary": b"2.0\n",
        "control.tar.gz": tar_gz({"./control": (control.encode(), 0o644)}),
        "data.tar.gz": tar_gz(data)}))


def windows_zip(publish, output):
    target = output / f"RepoRadar-{VERSION}-win-x64-portable.zip"
    temp = target.with_suffix(".zip.tmp")
    with zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED) as package:
        for name in (*MANAGED, "repo-radar.exe"):
            package.write(publish / name, name)
        for name in (*LICENSES, "repo-radar.ps1", "repo-radar.cmd", "preflight.psm1"):
            package.write(APP / name, name)
        package.writestr("portable.flag", "Keep catalog, index and preferences beside the application.\n")
    temp.replace(target)
    print(target)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("channel", choices=("debian", "windows"))
    parser.add_argument("publish", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    (debian_package if args.channel == "debian" else windows_zip)(args.publish, args.output)
