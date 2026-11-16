// SPDX-License-Identifier: MIT
export const NUMERIC = [
    ['intensity', 'Effect mix', 0, 1, 0.01, 0.9],
    ['scanlines', 'Scanline strength', 0, 0.6, 0.01, 0.22],
    ['scanline-spacing', 'Scanline spacing · px', 2, 12, 0.25, 3],
    ['scanline-width', 'Scanline thickness · px', 0.25, 6, 0.25, 1],
    ['glow', 'Phosphor glow', 0, 0.8, 0.01, 0.35],
    ['glow-radius', 'Glow radius · px', 0.5, 8, 0.25, 2],
    ['mask-strength', 'Phosphor mask strength', 0, 0.5, 0.01, 0.16],
    ['black-level', 'Black level', -0.2, 0.2, 0.01, 0],
    ['contrast', 'Contrast', 0.5, 2, 0.05, 1],
    ['vignette', 'Darkened edges', 0, 0.6, 0.01, 0.25],
    ['grain', 'Static grain', 0, 0.1, 0.005, 0.015],
    ['persistence-duration', 'Phosphor decay · seconds', 0.04, 0.5, 0.01, 0.15],
    ['persistence-strength', 'Afterglow strength', 0, 0.6, 0.01, 0.25],
    ['glitch-intensity', 'Glitch intensity', 0, 1, 0.01, 0.25],
    ['glitch-interval-min', 'Minimum interval · seconds', 5, 300, 1, 20],
    ['glitch-interval-max', 'Maximum interval · seconds', 5, 300, 1, 60],
    ['glitch-duration-min', 'Minimum duration · ms', 80, 1200, 10, 100],
    ['glitch-duration-max', 'Maximum duration · ms', 80, 1200, 10, 250],
];
export const BOOLEAN = [
    ['custom-color', false], ['persistence-enabled', false], ['glitches-enabled', false],
    ['glitch-rgb', true], ['glitch-tear', true], ['glitch-noise', true], ['glitch-band', true],
];
export const DEFAULTS = Object.freeze({
    ...Object.fromEntries(NUMERIC.map(([key, , , , , value]) => [key, value])),
    ...Object.fromEntries(BOOLEAN), preset: 0, mask: 0, 'phosphor-color': '#33ff6b',
});
export const BUILTINS = [
    {name: 'Clean green', values: {...DEFAULTS, scanlines: 0.12, glow: 0.18, grain: 0}},
    {name: 'Worn terminal', values: {...DEFAULTS, preset: 1, scanlines: 0.25,
        'glitches-enabled': true, 'glitch-intensity': 0.2, 'glitch-band': false}},
    {name: 'Arcade', values: {...DEFAULTS, preset: 2, mask: 1, 'mask-strength': 0.18, glow: 0.4}},
    {name: 'Long phosphor', values: {...DEFAULTS, 'persistence-enabled': true,
        'persistence-duration': 0.3, 'persistence-strength': 0.45}},
];

export function validateValues(values) {
    if (!values || typeof values !== 'object' || Array.isArray(values)) throw Error('Invalid preset');
    const clean = {...DEFAULTS};
    for (const [key, value] of Object.entries(values)) {
        if (!Object.hasOwn(DEFAULTS, key)) throw Error('Unknown preset setting');
        const numeric = NUMERIC.find(p => p[0] === key);
        if (numeric) {
            if (!Number.isFinite(value) || value < numeric[2] || value > numeric[3]) throw Error('Invalid numeric setting');
        } else if (key === 'phosphor-color') {
            if (typeof value !== 'string' || !/^#[\da-f]{6}$/i.test(value)) throw Error('Invalid phosphor color');
        } else if (key === 'preset' || key === 'mask') {
            if (!Number.isInteger(value) || value < 0 || value > 2) throw Error('Invalid style');
        } else if (typeof value !== 'boolean') throw Error('Invalid switch');
        clean[key] = value;
    }
    return clean;
}

export function readLibrary(text) {
    if (text.length > 131072) throw Error('Preset library is too large');
    const items = JSON.parse(text);
    if (!Array.isArray(items) || items.length > 32) throw Error('Invalid preset library');
    const names = new Set();
    return items.map(item => {
        if (!item || typeof item.name !== 'string' || !item.name.trim() || item.name.length > 48 || names.has(item.name))
            throw Error('Invalid or duplicate preset name');
        names.add(item.name);
        return {name: item.name, values: validateValues(item.values)};
    });
}

export function savePreset(text, name, values) {
    const items = readLibrary(text);
    name = name.trim();
    if (!name || name.length > 48) throw Error('Choose a name with 1–48 characters');
    if (items.some(p => p.name === name)) throw Error('That name already exists; choose a new name');
    if (items.length >= 32) throw Error('The library already contains 32 presets');
    return JSON.stringify([...items, {name, values: validateValues(values)}]);
}

export function readValues(settings) {
    return Object.fromEntries(Object.keys(DEFAULTS).map(key => [key, settings.get_value(key).deep_unpack()]));
}
export function applyValues(settings, values) {
    for (const [key, value] of Object.entries(validateValues(values))) {
        if (typeof value === 'boolean') settings.set_boolean(key, value);
        else if (typeof value === 'string') settings.set_string(key, value);
        else if (key === 'preset' || key === 'mask') settings.set_int(key, value);
        else settings.set_double(key, value);
    }
}
