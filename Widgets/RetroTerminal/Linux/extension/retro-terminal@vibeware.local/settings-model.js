// SPDX-License-Identifier: MIT
// Numeric IDs are persisted in GSettings and consumed by the shader.
export const Palette = Object.freeze({GREEN: 0, AMBER: 1, ARCADE: 2});
export const PhosphorMask = Object.freeze({NONE: 0, RGB_STRIPES: 1, DOTS: 2});
// Named descriptors keep UI bounds, validation and defaults on the same contract.
export const NUMERIC = Object.freeze([
    {key: 'intensity', title: 'Effect mix', min: 0, max: 1, step: 0.01, defaultValue: 0.9, unit: 'ratio'},
    {key: 'scanlines', title: 'Scanline strength', min: 0, max: 0.6, step: 0.01, defaultValue: 0.22, unit: 'ratio'},
    {key: 'scanline-spacing', title: 'Scanline spacing · px', min: 2, max: 12, step: 0.25, defaultValue: 3, unit: 'px'},
    {key: 'scanline-width', title: 'Scanline thickness · px', min: 0.25, max: 6, step: 0.25, defaultValue: 1, unit: 'px'},
    {key: 'glow', title: 'Phosphor glow', min: 0, max: 0.8, step: 0.01, defaultValue: 0.35, unit: 'ratio'},
    {key: 'glow-radius', title: 'Glow radius · px', min: 0.5, max: 8, step: 0.25, defaultValue: 2, unit: 'px'},
    {key: 'mask-strength', title: 'Phosphor mask strength', min: 0, max: 0.5, step: 0.01, defaultValue: 0.16, unit: 'ratio'},
    {key: 'black-level', title: 'Black level', min: -0.2, max: 0.2, step: 0.01, defaultValue: 0, unit: 'ratio'},
    {key: 'contrast', title: 'Contrast', min: 0.5, max: 2, step: 0.05, defaultValue: 1, unit: 'ratio'},
    {key: 'vignette', title: 'Darkened edges', min: 0, max: 0.6, step: 0.01, defaultValue: 0.25, unit: 'ratio'},
    {key: 'grain', title: 'Static grain', min: 0, max: 0.1, step: 0.005, defaultValue: 0.015, unit: 'ratio'},
    {key: 'persistence-duration', title: 'Phosphor decay · seconds', min: 0.04, max: 0.5, step: 0.01, defaultValue: 0.15, unit: 'seconds'},
    {key: 'persistence-strength', title: 'Afterglow strength', min: 0, max: 0.6, step: 0.01, defaultValue: 0.25, unit: 'ratio'},
    {key: 'glitch-intensity', title: 'Glitch intensity', min: 0, max: 1, step: 0.01, defaultValue: 0.25, unit: 'ratio'},
    {key: 'glitch-interval-min', title: 'Minimum interval · seconds', min: 5, max: 300, step: 1, defaultValue: 20, unit: 'seconds'},
    {key: 'glitch-interval-max', title: 'Maximum interval · seconds', min: 5, max: 300, step: 1, defaultValue: 60, unit: 'seconds'},
    {key: 'glitch-duration-min', title: 'Minimum duration · ms', min: 80, max: 1200, step: 10, defaultValue: 100, unit: 'ms'},
    {key: 'glitch-duration-max', title: 'Maximum duration · ms', min: 80, max: 1200, step: 10, defaultValue: 250, unit: 'ms'},
].map(Object.freeze));
export const BOOLEAN = [
    ['custom-color', false], ['persistence-enabled', false], ['glitches-enabled', false],
    ['glitch-rgb', true], ['glitch-tear', true], ['glitch-noise', true], ['glitch-band', true],
];
export const DEFAULTS = Object.freeze({
    ...Object.fromEntries(NUMERIC.map(({key, defaultValue}) => [key, defaultValue])),
    ...Object.fromEntries(BOOLEAN), preset: Palette.GREEN, mask: PhosphorMask.NONE, 'phosphor-color': '#33ff6b',
});

// A low-mix, static starting point keeps the desktop's original colors dominant.
// Each named look changes only the finish it needs; animation stays opt-in.
function subtleLook(name, overrides) {
    return {name, values: {...DEFAULTS,
        preset: Palette.ARCADE, intensity: 0.25, scanlines: 0, glow: 0,
        'glow-radius': 0.75, mask: PhosphorMask.NONE, 'mask-strength': 0,
        vignette: 0, grain: 0, contrast: 1, 'black-level': 0,
        'persistence-enabled': false, 'glitches-enabled': false,
        ...overrides,
    }};
}

export const BUILTINS = [
    {name: 'Clean green', values: {...DEFAULTS, scanlines: 0.12, glow: 0.18, grain: 0}},
    {name: 'Worn terminal', values: {...DEFAULTS, preset: Palette.AMBER, scanlines: 0.25,
        'glitches-enabled': true, 'glitch-intensity': 0.2, 'glitch-band': false}},
    {name: 'Arcade', values: {...DEFAULTS, preset: Palette.ARCADE, mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.18, glow: 0.4}},
    {name: 'Long phosphor', values: {...DEFAULTS, 'persistence-enabled': true,
        'persistence-duration': 0.3, 'persistence-strength': 0.45}},
    // Append new entries: saved built-in IDs use the original array indices.
    subtleLook('Almost clear', {intensity: 0.15, scanlines: 0.02}),
    subtleLook('Fine raster', {intensity: 0.3, scanlines: 0.06,
        'scanline-spacing': 2, 'scanline-width': 0.5}),
    subtleLook('Wide raster', {intensity: 0.25, scanlines: 0.08,
        'scanline-spacing': 6, 'scanline-width': 0.5}),
    subtleLook('Quiet RGB', {intensity: 0.25, mask: PhosphorMask.RGB_STRIPES,
        'mask-strength': 0.04}),
    subtleLook('Quiet dots', {intensity: 0.25, mask: PhosphorMask.DOTS,
        'mask-strength': 0.04}),
    subtleLook('Soft glass', {intensity: 0.25, glow: 0.04, 'glow-radius': 0.5}),
    subtleLook('Edge shade', {intensity: 0.25, vignette: 0.08}),
    subtleLook('Paper grain', {intensity: 0.3, grain: 0.005}),
    subtleLook('Lifted shadows', {intensity: 0.3, 'black-level': 0.02}),
    subtleLook('Gentle contrast', {intensity: 0.3, contrast: 1.05}),
    subtleLook('Green whisper', {preset: Palette.GREEN, intensity: 0.12}),
    subtleLook('Green glass', {preset: Palette.GREEN, intensity: 0.18,
        glow: 0.03, vignette: 0.03}),
    subtleLook('Sage tint', {'custom-color': true, 'phosphor-color': '#b8d9bc',
        intensity: 0.16}),
    subtleLook('Mint raster', {'custom-color': true, 'phosphor-color': '#b5e5d0',
        intensity: 0.18, scanlines: 0.04}),
    subtleLook('Amber whisper', {preset: Palette.AMBER, intensity: 0.12}),
    subtleLook('Warm glass', {preset: Palette.AMBER, intensity: 0.18,
        glow: 0.03, vignette: 0.03}),
    subtleLook('Ivory phosphor', {'custom-color': true, 'phosphor-color': '#f5ecd8',
        intensity: 0.16}),
    subtleLook('Peach tint', {'custom-color': true, 'phosphor-color': '#f2c8ae',
        intensity: 0.14}),
    subtleLook('Ice phosphor', {'custom-color': true, 'phosphor-color': '#c4e1f2',
        intensity: 0.16, scanlines: 0.03}),
    subtleLook('Lavender dusk', {'custom-color': true, 'phosphor-color': '#d5c9eb',
        intensity: 0.14, vignette: 0.04}),
];

export function validateValues(values) {
    if (!values || typeof values !== 'object' || Array.isArray(values)) throw Error('Invalid preset');
    const clean = {...DEFAULTS};
    for (const [key, value] of Object.entries(values)) {
        if (!Object.hasOwn(DEFAULTS, key)) throw Error('Unknown preset setting');
        const numeric = NUMERIC.find(spec => spec.key === key);
        if (numeric) {
            if (!Number.isFinite(value) || value < numeric.min || value > numeric.max) throw Error('Invalid numeric setting');
        } else if (key === 'phosphor-color') {
            if (typeof value !== 'string' || !/^#[\da-f]{6}$/i.test(value)) throw Error('Invalid phosphor color');
        } else if (key === 'preset' || key === 'mask') {
            const allowed = key === 'preset' ? Palette : PhosphorMask;
            if (!Object.values(allowed).includes(value)) throw Error('Invalid style');
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
