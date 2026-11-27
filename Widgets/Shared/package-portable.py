"""Build a portable widget ZIP after checking its Windows native payload."""

import argparse
from pathlib import Path
import struct
import sys
from zipfile import ZIP_DEFLATED, ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "packaging"))
from vibeware_msi import read_runtime


def check_machine(binary, architecture):
    with binary.open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise ValueError(f"Missing DOS header in {binary.name}")
        stream.seek(0x3C)
        pe_offset = int.from_bytes(stream.read(4), "little")
        stream.seek(pe_offset)
        expected = {"x64": 0x8664, "arm64": 0xAA64}[architecture]
        if stream.read(6) != b"PE\0\0" + struct.pack("<H", expected):
            raise ValueError(f"{binary.name} is not a Windows binary for {architecture}")


def package(payload, executable, architecture, output, native, runtime_type="core"):
    payload = payload.resolve(strict=True)
    output = output.resolve()
    if output.is_relative_to(payload):
        raise ValueError("Write the ZIP outside the payload directory")
    app = payload / executable
    for binary in (app, *(payload / name for name in native)):
        binary.resolve(strict=True).relative_to(payload)
        check_machine(binary, architecture)
    read_runtime(payload, executable, architecture, runtime_type)
    files = sorted(path for path in payload.rglob("*") if path.is_file() and path.suffix.lower() != ".pdb")
    for path in files:
        path.resolve(strict=True).relative_to(payload)
        if path.suffix.lower() in {".pfx", ".p12", ".snk", ".key"}:
            raise ValueError(f"The payload contains a signing key: {path.name}")
    with output.open("x+b") as stream:
        try:
            with ZipFile(stream, "w", compression=ZIP_DEFLATED) as archive:
                for path in files:
                    archive.write(path, path.relative_to(payload).as_posix())
            with ZipFile(stream) as archive:
                if archive.testzip() is not None:
                    raise ValueError("Portable ZIP failed its CRC check")
        except BaseException:
            stream.close()
            output.unlink()
            raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--executable", required=True)
    parser.add_argument("--architecture", choices=("x64", "arm64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime-type", choices=("core", "desktop"), default="core")
    parser.add_argument("--native", action="append", default=[])
    package(**vars(parser.parse_args()))
