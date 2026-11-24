// SPDX-License-Identifier: MIT
export const TEXTURE_SIZE = 512;
export const PARAMETERS = [
    ['intensity', 'Intensity', 0, .85, .01],
    ['radius', 'Clear center', .1, .9, .01],
    ['softness', 'Falloff width', .08, .9, .01],
    ['aspect', 'Ellipse aspect', .65, 1.55, .01],
];

export function validateAppearance(values) {
    if (typeof values?.color !== 'string' || !/^#[0-9a-f]{6}$/i.test(values.color))
        throw Error('Use a color in #RRGGBB format.');
    const result = {color: values.color.toUpperCase()};
    for (const [key, , min, max] of PARAMETERS) {
        const number = values[key];
        if (!Number.isFinite(number) || number < min || number > max)
            throw Error(`Invalid vignette ${key}.`);
        result[key] = number;
    }
    return result;
}

export function alphaAt(x, y, {intensity, radius, softness, aspect}) {
    const distance = Math.sqrt(((x / aspect) ** 2 + (y * aspect) ** 2) / 2);
    const t = Math.min(1, Math.max(0, (distance - radius) / softness));
    return intensity * t * t * (3 - 2 * t);
}

export function createPixels(appearance, size = TEXTURE_SIZE) {
    const rgb = [1, 3, 5].map(offset => parseInt(appearance.color.slice(offset, offset + 2), 16));
    const palette = Array.from({length: 256}, (_, alpha) =>
        [...rgb.map(channel => Math.round(channel * alpha / 255)), alpha]);
    const pixels = new Uint8Array(size * size * 4);
    const half = Math.ceil(size / 2);
    for (let y = 0; y < half; y++) {
        const ny = 2 * y / (size - 1) - 1;
        for (let x = 0; x < half; x++) {
            const alpha = Math.round(alphaAt(2 * x / (size - 1) - 1, ny, appearance) * 255);
            const color = palette[alpha];
            const right = size - 1 - x, bottom = size - 1 - y;
            pixels.set(color, (y * size + x) * 4);
            pixels.set(color, (y * size + right) * 4);
            pixels.set(color, (bottom * size + x) * 4);
            pixels.set(color, (bottom * size + right) * 4);
        }
    }
    return pixels;
}
