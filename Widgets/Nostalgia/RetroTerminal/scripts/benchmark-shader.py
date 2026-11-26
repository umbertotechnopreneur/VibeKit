#!/usr/bin/env python3
"""Render the production GLSL into a real FBO, verify pixels and time GPU draws.

Requires cc, GL/GLX development headers, X11/Xwayland and Python Pillow.
No desktop capture is performed; all images use a generated synthetic desktop.
"""
# SPDX-License-Identifier: MIT
import argparse
import hashlib
from pathlib import Path
import re
import subprocess
from PIL import Image, ImageDraw, ImageFont


def synthetic_desktop(width, height):
    image = Image.new('RGB', (width, height))
    draw = ImageDraw.Draw(image)
    for y in range(height):
        t = y / height
        draw.line((0, y, width, y), fill=(int(15 + 35*t), int(24 + 21*t), int(60 + 65*t)))
    font_path = '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf'
    try:
        font = ImageFont.truetype(font_path, max(14, width // 80))
    except OSError:
        font = ImageFont.load_default()
    draw.rectangle((0, 0, width, height//24), fill=(20, 22, 26))
    draw.text((width//40, height//120), 'Activities        Retro Terminal - synthetic test desktop', font=font, fill=(235, 237, 240))
    box = (width//10, height//6, width*9//10, height*5//6)
    draw.rounded_rectangle(box, radius=12, fill=(24, 29, 35), outline=(100, 110, 130), width=2)
    draw.text((box[0]+30, box[1]+20), 'Terminal   -   render validation', font=font, fill=(228, 230, 240))
    lines = [('user@desktop:~$ ./retro-terminal --demo', (90, 220, 140)),
             (f'Render target: {width} x {height} / full desktop', (230, 235, 240)),
             ('Shader: phosphor + scanlines + spatial glow', (115, 190, 255)),
             ('Stop: Super + Shift + F12', (255, 194, 87)),
             ('No capture or private desktop content.', (230, 235, 240)),
             ('0123456789  abcdefghijklmnopqrstuvwxyz', (230, 235, 240)),
             ('ABCDEFGHIJKLMNOPQRSTUVWXYZ  []{}()<>', (230, 235, 240))]
    for i, (text, color) in enumerate(lines):
        draw.text((box[0]+30, box[1]+height//10+i*height//22), text, font=font, fill=color)
    for i, color in enumerate([(255, 50, 60), (60, 255, 120), (80, 120, 255), (255, 255, 255), (20, 20, 20)]):
        x = box[0]+30+i*width//9
        draw.rectangle((x, box[3]-height//10, x+width//11, box[3]-height//25), fill=color)
    return image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--width', type=int, default=1920)
    parser.add_argument('--height', type=int, default=1080)
    args = parser.parse_args()
    if not (64 <= args.width <= 3840 and 64 <= args.height <= 2160):
        parser.error('Dimensions must be 64..3840 by 64..2160')
    root = Path(__file__).resolve().parents[1]
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = (root/'Linux/extension/retro-terminal@vibeware.local/shader.js').read_text()
    blocks = [re.search(rf'export const {name} = `(.*?)`;', source, re.S).group(1)
              for name in ('DECLARATIONS', 'CODE')]
    # Only replace Cogl's integration identifiers; production equations stay unchanged.
    body = blocks[1].replace('cogl_tex_coord_in[0].xy', 'gl_TexCoord[0].xy')
    body = body.replace('cogl_color_out', 'gl_FragColor').replace('cogl_color_in', 'gl_Color')
    shader = output/'production.frag'
    shader.write_text('#version 120\n'+blocks[0]+'\nvoid main() {\n'+body+'\n}\n')
    image = synthetic_desktop(args.width, args.height)
    image.save(output/'source.png')
    rgba = output/'source.rgba'
    rgba.write_bytes(image.convert('RGBA').tobytes())
    executable = output/'shader-bench'
    subprocess.run(['cc', '-std=c11', '-O2', '-Wall', '-Wextra', str(root/'tests/shader-bench.c'),
                    '-lGL', '-lX11', '-o', str(executable)], check=True)
    digest = hashlib.sha256(source.encode()).hexdigest()
    try:
        result = subprocess.run([str(executable), str(shader), str(rgba), str(output),
                                 str(args.width), str(args.height)], text=True, capture_output=True, timeout=45)
    except subprocess.TimeoutExpired as error:
        partial = error.stdout or b''
        if isinstance(partial, bytes):
            partial = partial.decode('utf-8', errors='replace')
        (output/'measurements.txt').write_text(f'Production shader.js SHA-256: {digest}\n'
            'INCOMPLETE: timed out after 45 seconds; no completed validation result.\n'+partial)
        raise SystemExit(f'Benchmark timed out; partial diagnostics in {output / "measurements.txt"}') from error
    print(result.stdout, end='')
    print(result.stderr, end='')
    (output/'measurements.txt').write_text(f'Production shader.js SHA-256: {digest}\n'
        +f'Exit status: {result.returncode}\n'+result.stdout+result.stderr)
    result.check_returncode()
    for path in output.glob('*.ppm'):
        with Image.open(path) as rendered:
            rendered.save(path.with_suffix('.png'))
    print(f'Artifacts and measurement scope: {output}')


if __name__ == '__main__':
    main()
