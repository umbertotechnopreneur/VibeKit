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

// Static looks share neutral optics and leave animation opt-in.
function displayLook(id, name, overrides) {
    return {id: `builtin:${id}`, name, values: {...DEFAULTS,
        preset: Palette.ARCADE, intensity: 0.25, scanlines: 0, glow: 0,
        'glow-radius': 0.75, mask: PhosphorMask.NONE, 'mask-strength': 0,
        vignette: 0, grain: 0, contrast: 1, 'black-level': 0,
        'persistence-enabled': false, 'glitches-enabled': false,
        ...overrides,
    }};
}

// Monitor-inspired looks use stronger optics and more of the processed signal.
export const BUILTINS = [
    displayLook('commodore-1702-composite', 'Commodore 1702 Composite', {
        intensity: 0.75, scanlines: 0.42, 'scanline-spacing': 5, 'scanline-width': 1.5, glow: 0.18,
        'glow-radius': 2, vignette: 0.38, mask: PhosphorMask.DOTS, 'mask-strength': 0.18, contrast: 1.07}),
    displayLook('commodore-1902-rgbi', 'Commodore 1902 RGBI', {
        intensity: 0.7, scanlines: 0.25, 'scanline-spacing': 3, 'scanline-width': 0.75, vignette: 0.06,
        mask: PhosphorMask.DOTS, 'mask-strength': 0.23, 'black-level': -0.025, contrast: 1.22}),
    displayLook('commodore-pet-green', 'Commodore PET Green', {
        intensity: 0.66, scanlines: 0.31, 'scanline-spacing': 4, 'scanline-width': 1.25, preset: Palette.GREEN,
        glow: 0.15, 'glow-radius': 1.75, vignette: 0.27, contrast: 1.16}),
    displayLook('amiga-1084s-rgb', 'Amiga 1084S RGB', {
        intensity: 0.74, scanlines: 0.4, 'scanline-spacing': 4, 'scanline-width': 1.25, glow: 0.1,
        'glow-radius': 1.5, vignette: 0.15, mask: PhosphorMask.DOTS, 'mask-strength': 0.24, contrast: 1.18}),
    displayLook('amiga-workbench-blue', 'Amiga Workbench Blue', {
        intensity: 0.58, scanlines: 0.23, 'scanline-spacing': 3, 'scanline-width': 1, 'custom-color': true,
        'phosphor-color': '#889dff', glow: 0.07, 'glow-radius': 1.25, vignette: 0.08, 'black-level': 0.025,
        contrast: 1.06}),
    displayLook('amiga-500-pal', 'Amiga 500 PAL', {
        intensity: 0.7, scanlines: 0.43, 'scanline-spacing': 6, 'scanline-width': 1.75, glow: 0.25,
        'glow-radius': 2.5, vignette: 0.26, grain: 0.018, 'black-level': 0.015, contrast: 0.96}),
    displayLook('tv-composite-pal', 'TV Composite PAL', {
        intensity: 0.82, scanlines: 0.47, 'scanline-spacing': 6, 'scanline-width': 2, glow: 0.28,
        'glow-radius': 2.75, vignette: 0.48, grain: 0.03, mask: PhosphorMask.DOTS, 'mask-strength': 0.19,
        'black-level': -0.025, contrast: 1.09}),
    displayLook('tv-black-and-white', 'TV Black and White', {
        intensity: 0.82, scanlines: 0.27, 'scanline-spacing': 4, 'scanline-width': 1.25, 'custom-color': true,
        'phosphor-color': '#e5edf7', glow: 0.16, 'glow-radius': 2, vignette: 0.42, grain: 0.019,
        contrast: 1.24}),
    displayLook('tv-rf-soft-focus', 'TV RF Soft Focus', {
        intensity: 0.76, scanlines: 0.32, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.35,
        'glow-radius': 3.25, vignette: 0.58, grain: 0.065, 'black-level': 0.035, contrast: 0.84}),
    displayLook('trinitron-aperture-grille', 'Trinitron Aperture Grille', {
        intensity: 0.8, scanlines: 0.33, 'scanline-spacing': 4, 'scanline-width': 1.25, vignette: 0.1,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.4, contrast: 1.12}),
    displayLook('trinitron-fine-pitch', 'Trinitron Fine Pitch', {
        intensity: 0.67, scanlines: 0.18, 'scanline-spacing': 2, 'scanline-width': 0.75, vignette: 0.03,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.29, 'black-level': -0.01, contrast: 1.1}),
    displayLook('trinitron-broadcast-rgb', 'Trinitron Broadcast RGB', {
        intensity: 0.74, scanlines: 0.5, 'scanline-spacing': 5, 'scanline-width': 1.75, vignette: 0.07,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.31, 'black-level': -0.035, contrast: 1.24}),
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
