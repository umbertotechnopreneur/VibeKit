#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Generate small original coil and relay sound effects for the degauss pulse."""
import argparse
import array
import math
from pathlib import Path
import random
import shutil
import sys
import wave

RATE = 24000
DURATION = 1.8
VARIANTS = {
    "dry-relay": (60, .12, .35, .43, 670),
    "low-coil": (50, .44, .30, .22, 142),
    "long-buzz": (60, .49, .53, .18, 248),
}


def render(parameters, seed):
    hertz, decay, grit, impact, ring = parameters
    rng = random.Random(seed)
    noise = 0
    result = []
    for index in range(round(DURATION * RATE)):
        t = index / RATE
        noise += .09 * (rng.uniform(-1, 1) - noise)
        phase = math.tau * hertz * t
        coil = (.12 * math.sin(phase) + .24 * math.sin(2 * phase)
                + grit * (.19 * math.sin(4 * phase + .3) + .24 * noise))
        coil *= (1 - math.exp(-t / .007)) * math.exp(-t / decay)
        def relay(age):
            if age < 0:
                return 0
            return (1 - math.exp(-age / .001)) * math.exp(-age / .018) * (
                math.sin(math.tau * ring * age) + .30 * math.sin(math.tau * 2.4 * ring * age))
        strike = impact * relay(t - .020)
        result.append((coil + strike) * min(1, (DURATION - t) / .08))
    peak = max(map(abs, result))
    pcm = array.array("h", (round(sample * (.48 / peak) * 32767) for sample in result))
    if sys.byteorder == "big":
        pcm.byteswap()
    return pcm


parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
for number, (name, parameters) in enumerate(VARIANTS.items()):
    with wave.open(str(args.output / f"{name}.wav"), "wb") as sound:
        sound.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        sound.writeframes(render(parameters, 411 + number).tobytes())

# CC0 excerpts. Both clips are mono, 24 kHz and 1.8 seconds after editing.
# Skyrite, 0.06–1.86 s: https://freesound.org/people/Skyrite/sounds/667514/
# Fission9, 0.02–1.82 s: https://freesound.org/people/Fission9/sounds/693860/
recordings = Path(__file__).resolve().parents[1] / "Shared/Recordings"
for name in ("desktop-monitor.wav", "pal-television.wav"):
    shutil.copyfile(recordings / name, args.output / name)
