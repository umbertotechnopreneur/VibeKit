#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Generate the original sound bank or prepare the user's local example profiles."""
import argparse
import array
import json
import math
from pathlib import Path
import random
import re
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


def original(directory):
    for name in ("startup", "idle", "shutdown"):
        seconds = {"startup": 3.1, "idle": 2.0, "shutdown": 2.5}[name]
        phase = 0
        pcm = []
        for n in range(round(seconds * RATE)):
            t = n / RATE
            if name == "startup":
                speed = .24 + .76 * (1 - math.exp(-t / .65))
                envelope = min(t / .05, 1, (seconds - t) / .20)
            elif name == "shutdown":
                speed = max(.09, (1 - t / seconds) ** 1.55)
                envelope = min(t / .05, 1) * max(0, 1 - t / seconds) ** 1.3
            else:
                speed, envelope = 1, 1
            phase += math.tau * 90 * speed / RATE
            sound = math.sin(phase) * .16 + math.sin(2 * phase) * .09 + math.sin(5 * phase) * .035
            if name == "startup":
                sound += .035 * math.sin(12 * phase) * math.exp(-t / 1.25)
            pcm.append(sound * envelope)
        wav(directory / f"{name}.wav", pcm)

    for number in range(1, 5):
        rng = random.Random(3425 + number)
        pcm = []
        return_at = .012 + .009 * number
        for n in range(round(.15 * RATE)):
            t = n / RATE
            pitch = 360 + 92 * number
            def click(elapsed):
                return (math.sin(math.tau * pitch * elapsed) * .47
                        + math.sin(math.tau * (1450 + 125 * number) * elapsed) * .28
                        + rng.uniform(-.22, .22)) * math.exp(-elapsed / .010)
            value = click(t) * min(1, t / .001)
            if t > return_at:
                value += .52 * click(t - return_at) * min(1, (t - return_at) / .001)
            pcm.append(value)
        wav(directory / f"seek{number}.wav", pcm)


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
    args = parser.parse_args()
    if args.examples:
        examples(args.examples, args.output)
    else:
        original(args.output)
