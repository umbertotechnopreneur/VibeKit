#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Copy recorded sound banks or prepare the user's local example profiles."""
import argparse
import array
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import wave
import zipfile

RATE = 24000


def wav(path, pcm):
    path.parent.mkdir(parents=True, exist_ok=True)
    values = array.array("h", (max(-32767, min(32767, round(value * 32767))) for value in pcm))
    if sys.byteorder == "big":
        values.byteswap()
    with wave.open(str(path), "wb") as stream:
        stream.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        stream.writeframes(values.tobytes())


def original(directory, variant="classic"):
    source = Path(__file__).resolve().parents[1] / "Shared" / "Samples" / variant
    directory.mkdir(parents=True, exist_ok=True)
    for name in ("startup.wav", "idle.wav", "shutdown.wav", "seek1.wav", "seek2.wav", "seek3.wav", "seek4.wav", "error.wav"):
        shutil.copyfile(source / name, directory / name)


def decode(data):
    pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", "pipe:0", "-ac", "1", "-ar", str(RATE),
                          "-f", "s16le", "pipe:1"], input=data, check=True, capture_output=True).stdout
    result = array.array("h")
    result.frombytes(pcm)
    if sys.byteorder == "big":
        result.byteswap()
    return result


def normalized(pcm, peak, fade=0):
    multiplier = peak / max(1, max(map(abs, pcm)))
    return (value * multiplier * (min(1, i/fade, (len(pcm)-1-i)/fade) if fade else 1)
            for i, value in enumerate(pcm))


def examples(source, output):
    with zipfile.ZipFile(source) as archive:
        index = next(name for name in archive.namelist() if name.endswith("/samples.json"))
        root = index.rsplit("/", 1)[0] + "/"
        manifest = json.loads(archive.read(index))
        samples = manifest["samples"]
        def pcm(sample):
            return decode(archive.read(root + sample["file"]))
        idle = next(item for item in samples if item.get("loop"))
        shutdown = next(item for item in samples if item["kind"] == "shutdown")
        seeks = [item for item in samples if "random-" in item["file"]][:4]
        common = [("idle.wav", list(normalized(pcm(idle), .35))),
                  ("shutdown.wav", list(normalized(pcm(shutdown), .54, 180)))]
        for number, sample in enumerate(seeks, 1):
            signal = pcm(sample)
            duration = int(RATE * .19)
            start = max(range(0, len(signal) - duration, RATE // 30),
                        key=lambda index: sum(abs(value) for value in signal[index:index + duration:8]))
            common.append((f"seek{number}.wav", list(normalized(signal[start:start + duration], .66, 150))))
        for startup in (item for item in samples if item["kind"] == "startup"):
            label = re.sub(r'[<>:"/\\|?*]', "-", startup["title"].removesuffix(" - Start-up"))
            folder = output / label
            wav(folder / "startup.wav", normalized(pcm(startup), .59, 180))
            for name, samples_pcm in common:
                wav(folder / name, samples_pcm)
        (output / "sources.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--examples", type=Path)
    parser.add_argument("--variant", choices=("classic", "warm", "computer"), default="classic")
    args = parser.parse_args()
    if args.examples:
        examples(args.examples, args.output)
    else:
        original(args.output, args.variant)
