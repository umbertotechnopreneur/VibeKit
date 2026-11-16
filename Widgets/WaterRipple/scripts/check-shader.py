#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Render exact Ripple GLSL on a real FBO, check controls and compare classic output.

Requires a C compiler, GL/GLX headers, X11/Xwayland and Python Pillow.
Only synthetic text/grid imagery is used; no desktop capture occurs.
"""
import argparse
import hashlib
from pathlib import Path
import re
import subprocess
from PIL import Image, ImageDraw, ImageFont


def wrap(source):
    declarations = re.search(r'const DECLARATIONS = `(.*?)`;', source, re.S).group(1)
    body = re.search(r'const CODE = `(.*?)`;', source, re.S).group(1)
    body = body.replace('cogl_tex_coord_in[0].xy', 'gl_TexCoord[0].xy')
    body = body.replace('cogl_color_out', 'gl_FragColor').replace('cogl_color_in', 'gl_Color')
    return '#version 120\n' + declarations + '\nvoid main() {\n' + body + '\n}\n'


def fixture():
    image = Image.new('RGB', (512, 512), '#15171b')
    draw = ImageDraw.Draw(image)
    for x in range(0, 512, 16):
        draw.line((x, 0, x, 511), fill='#333945')
    for y in range(0, 512, 16):
        draw.line((0, y, 511, y), fill='#333945')
    try:
        font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf', 16)
    except OSError:
        font = ImageFont.load_default()
    for i in range(15):
        text = f'{i:02d} Ripple / preset {i % 3 + 1}'
        draw.text((20, 24 + i*30), text, font=font, fill=['#f2f4f7','#65caff','#ffc060'][i%3])
    return image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--baseline-ref', default='ad97314c^', help='Git revision containing the original classic effect.js')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    repository = root.parents[1]
    effect = root / 'Linux/extension/ripple@vibeware.local/effect.js'
    source = effect.read_text()
    reference = subprocess.check_output(['git', 'show', f'{args.baseline_ref}:{effect.relative_to(repository).as_posix()}'], cwd=repository, text=True)
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    (output / 'production.frag').write_text(wrap(source))
    (output / 'baseline.frag').write_text(wrap(reference))
    image = fixture()
    image.save(output / 'source.png')
    (output / 'source.rgba').write_bytes(image.convert('RGBA').tobytes())
    executable = output / 'shader-regression'
    subprocess.run(['cc', '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror', str(root/'tests/shader-regression.c'), '-lGL', '-lX11', '-o', str(executable)], check=True)
    result = subprocess.run([str(executable), str(output/'production.frag'), str(output/'baseline.frag'), str(output/'source.rgba'), str(output)], capture_output=True, text=True, timeout=45)
    report = f'Production source SHA-256: {hashlib.sha256(source.encode()).hexdigest()}\nBaseline git revision: {args.baseline_ref}\n' + result.stdout + result.stderr
    (output/'measurements.txt').write_text(report)
    print(report, end='')
    for path in output.glob('*.rgba'):
        Image.frombytes('RGBA', (512,512), path.read_bytes()).save(path.with_suffix('.png'))
    result.check_returncode()
    print(f'Images and measurements: {output}')


if __name__ == '__main__':
    main()
