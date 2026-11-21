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

// Keep the raster gentle and give each look a small amount of static grain.
export const BUILTINS = [
    // Existing monitor presets.
    displayLook('commodore-1702-composite', 'Commodore 1702 Composite', {
        intensity: 0.4, scanlines: 0.105, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.08,
        'glow-radius': 1.75, vignette: 0.06, grain: 0.06, mask: PhosphorMask.DOTS, 'mask-strength': 0.065}),
    displayLook('commodore-1902-rgbi', 'Commodore 1902 RGBI', {
        intensity: 0.36, scanlines: 0.085, 'scanline-spacing': 3, 'scanline-width': 0.75, grain: 0.045,
        mask: PhosphorMask.DOTS, 'mask-strength': 0.075, contrast: 1.035}),
    displayLook('commodore-pet-green', 'Commodore PET Green', {
        preset: Palette.GREEN, intensity: 0.31, scanlines: 0.09, 'scanline-spacing': 4, 'scanline-width': 1,
        glow: 0.08, 'glow-radius': 1.25, vignette: 0.055, grain: 0.06}),
    displayLook('amiga-1084s-rgb', 'Amiga 1084S RGB', {
        intensity: 0.39, scanlines: 0.095, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.05,
        'glow-radius': 1.25, grain: 0.05, mask: PhosphorMask.DOTS, 'mask-strength': 0.08, contrast: 1.025}),
    displayLook('amiga-workbench-blue', 'Amiga Workbench Blue', {
        'custom-color': true, 'phosphor-color': '#a4b5ff', intensity: 0.39, scanlines: 0.075,
        'scanline-spacing': 3, 'scanline-width': 1, glow: 0.05, grain: 0.05, 'black-level': 0.01}),
    displayLook('amiga-500-pal', 'Amiga 500 PAL', {
        intensity: 0.41, scanlines: 0.11, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.08,
        'glow-radius': 1.75, vignette: 0.045, grain: 0.075, contrast: 0.985}),
    displayLook('tv-composite-pal', 'TV Composite PAL', {
        intensity: 0.44, scanlines: 0.105, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.095,
        'glow-radius': 1.75, vignette: 0.08, grain: 0.085, mask: PhosphorMask.DOTS, 'mask-strength': 0.045,
        'black-level': 0.01}),
    displayLook('tv-black-and-white', 'TV Black and White', {
        'custom-color': true, 'phosphor-color': '#edf2f7', intensity: 0.46, scanlines: 0.09,
        'scanline-spacing': 4, 'scanline-width': 1, glow: 0.075, 'glow-radius': 1.5, vignette: 0.075,
        grain: 0.075, contrast: 1.03}),
    displayLook('tv-rf-soft-focus', 'TV RF Soft Focus', {
        intensity: 0.43, scanlines: 0.085, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.13,
        'glow-radius': 2.5, vignette: 0.1, grain: 0.1, 'black-level': 0.012, contrast: 0.97}),
    displayLook('trinitron-aperture-grille', 'Trinitron Aperture Grille', {
        intensity: 0.42, scanlines: 0.08, 'scanline-spacing': 4, 'scanline-width': 1, vignette: 0.025,
        grain: 0.055, mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.095}),
    displayLook('trinitron-fine-pitch', 'Trinitron Fine Pitch', {
        intensity: 0.37, scanlines: 0.075, 'scanline-spacing': 2, 'scanline-width': 0.75, grain: 0.05,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.08}),
    displayLook('trinitron-broadcast-rgb', 'Trinitron Broadcast RGB', {
        intensity: 0.43, scanlines: 0.09, 'scanline-spacing': 5, 'scanline-width': 1, grain: 0.055,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.085, contrast: 1.025}),

    // More Commodore and Amiga monitors.
    displayLook('commodore-64-luma', 'Commodore 64 Luma', {
        intensity: 0.38, scanlines: 0.12, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.095,
        'glow-radius': 1.5, vignette: 0.045, grain: 0.075, mask: PhosphorMask.DOTS, 'mask-strength': 0.05,
        contrast: 0.98}),
    displayLook('commodore-128-text', 'Commodore 128 Text', {
        intensity: 0.34, scanlines: 0.09, 'scanline-spacing': 3, 'scanline-width': 0.75, grain: 0.055,
        mask: PhosphorMask.DOTS, 'mask-strength': 0.06, contrast: 1.04}),
    displayLook('commodore-1084-composite', 'Commodore 1084 Composite', {
        intensity: 0.42, scanlines: 0.105, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.1,
        'glow-radius': 2, vignette: 0.045, grain: 0.07, mask: PhosphorMask.DOTS, 'mask-strength': 0.07}),
    displayLook('commodore-amber-phosphor', 'Commodore Amber Phosphor', {
        preset: Palette.AMBER, intensity: 0.32, scanlines: 0.085, 'scanline-spacing': 4, 'scanline-width': 1,
        glow: 0.075, 'glow-radius': 1.5, vignette: 0.045, grain: 0.065}),
    displayLook('amiga-workbench-grey', 'Amiga Workbench Grey', {
        'custom-color': true, 'phosphor-color': '#dddfe3', intensity: 0.37, scanlines: 0.065,
        'scanline-spacing': 3, 'scanline-width': 0.75, grain: 0.045}),
    displayLook('amiga-1200-rgb', 'Amiga 1200 RGB', {
        intensity: 0.38, scanlines: 0.085, 'scanline-spacing': 3, 'scanline-width': 0.75, glow: 0.045,
        'glow-radius': 1.25, grain: 0.05, mask: PhosphorMask.DOTS, 'mask-strength': 0.09}),
    displayLook('amiga-500-ntsc', 'Amiga 500 NTSC', {
        intensity: 0.41, scanlines: 0.095, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.075,
        'glow-radius': 1.5, vignette: 0.04, grain: 0.085, contrast: 0.99}),
    displayLook('amiga-genlock', 'Amiga Genlock', {
        intensity: 0.4, scanlines: 0.07, 'scanline-spacing': 5, 'scanline-width': 1, glow: 0.065,
        'glow-radius': 1.75, vignette: 0.03, grain: 0.075, 'black-level': 0.008, contrast: 1.015}),

    // More TV and Trinitron displays.
    displayLook('tv-composite-ntsc', 'TV Composite NTSC', {
        intensity: 0.43, scanlines: 0.105, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.085,
        'glow-radius': 1.75, vignette: 0.07, grain: 0.09, mask: PhosphorMask.DOTS, 'mask-strength': 0.06,
        'black-level': 0.008}),
    displayLook('tv-s-video', 'TV S-Video', {
        intensity: 0.37, scanlines: 0.09, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.055,
        'glow-radius': 1.25, vignette: 0.035, grain: 0.065, mask: PhosphorMask.DOTS, 'mask-strength': 0.065}),
    displayLook('tv-portable-mono', 'TV Portable Mono', {
        'custom-color': true, 'phosphor-color': '#dce7e4', intensity: 0.46, scanlines: 0.1,
        'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.09, 'glow-radius': 1.75, vignette: 0.085,
        grain: 0.09, 'black-level': 0.01}),
    displayLook('tv-studio-monitor', 'TV Studio Monitor', {
        intensity: 0.41, scanlines: 0.075, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.045,
        grain: 0.06, mask: PhosphorMask.DOTS, 'mask-strength': 0.07, contrast: 1.02}),
    displayLook('trinitron-desktop-vga', 'Trinitron Desktop VGA', {
        intensity: 0.36, scanlines: 0.07, 'scanline-spacing': 3, 'scanline-width': 0.75, grain: 0.05,
        mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.11, contrast: 1.02}),
    displayLook('trinitron-studio-pal', 'Trinitron Studio PAL', {
        intensity: 0.43, scanlines: 0.095, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.065,
        'glow-radius': 1.5, grain: 0.075, mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.075}),
    displayLook('trinitron-consumer-rgb', 'Trinitron Consumer RGB', {
        intensity: 0.4, scanlines: 0.095, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.075,
        'glow-radius': 1.5, vignette: 0.055, grain: 0.065, mask: PhosphorMask.RGB_STRIPES,
        'mask-strength': 0.085}),
    displayLook('trinitron-soft-grille', 'Trinitron Soft Grille', {
        intensity: 0.38, scanlines: 0.065, 'scanline-spacing': 5, 'scanline-width': 1.25, glow: 0.09,
        'glow-radius': 1.75, grain: 0.07, mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.07,
        contrast: 0.985}),

    // Terminal phosphors.
    displayLook('terminal-ibm-green', 'Terminal IBM Green', {
        preset: Palette.GREEN, intensity: 0.34, scanlines: 0.105, 'scanline-spacing': 4, 'scanline-width': 1,
        glow: 0.07, 'glow-radius': 1.5, grain: 0.065, contrast: 1.02}),
    displayLook('terminal-amber-glow', 'Terminal Amber Glow', {
        preset: Palette.AMBER, intensity: 0.35, scanlines: 0.085, 'scanline-spacing': 5, 'scanline-width': 1,
        glow: 0.115, 'glow-radius': 1.75, vignette: 0.06, grain: 0.065}),
    displayLook('terminal-ice-blue', 'Terminal Ice Blue', {
        'custom-color': true, 'phosphor-color': '#accfff', intensity: 0.4, scanlines: 0.075,
        'scanline-spacing': 4, 'scanline-width': 1, glow: 0.055, 'glow-radius': 1.25, grain: 0.05}),
    displayLook('terminal-paper-white', 'Terminal Paper White', {
        'custom-color': true, 'phosphor-color': '#f5ead6', intensity: 0.43, scanlines: 0.065,
        'scanline-spacing': 3, 'scanline-width': 0.75, grain: 0.055, contrast: 1.025}),
    displayLook('terminal-mint-phosphor', 'Terminal Mint Phosphor', {
        'custom-color': true, 'phosphor-color': '#a9ffcc', intensity: 0.41, scanlines: 0.08,
        'scanline-spacing': 4, 'scanline-width': 1, glow: 0.08, 'glow-radius': 1.5, grain: 0.06}),
    displayLook('terminal-violet-phosphor', 'Terminal Violet Phosphor', {
        'custom-color': true, 'phosphor-color': '#d5bbff', intensity: 0.42, scanlines: 0.085,
        'scanline-spacing': 5, 'scanline-width': 1, glow: 0.075, 'glow-radius': 1.5, grain: 0.065}),

    // Arcade and video signals.
    displayLook('arcade-shadow-mask', 'Arcade Shadow Mask', {
        intensity: 0.44, scanlines: 0.095, 'scanline-spacing': 4, 'scanline-width': 1, glow: 0.08,
        'glow-radius': 1.5, vignette: 0.06, grain: 0.06, mask: PhosphorMask.DOTS, 'mask-strength': 0.11}),
    displayLook('arcade-rgb-cabinet', 'Arcade RGB Cabinet', {
        intensity: 0.42, scanlines: 0.09, 'scanline-spacing': 5, 'scanline-width': 1, glow: 0.08,
        'glow-radius': 1.5, vignette: 0.07, grain: 0.065, mask: PhosphorMask.RGB_STRIPES, 'mask-strength': 0.08}),
    displayLook('arcade-fine-raster', 'Arcade Fine Raster', {
        intensity: 0.34, scanlines: 0.075, 'scanline-spacing': 3, 'scanline-width': 0.75, glow: 0.055,
        grain: 0.045, mask: PhosphorMask.DOTS, 'mask-strength': 0.065}),
    displayLook('arcade-warm-glass', 'Arcade Warm Glass', {
        'custom-color': true, 'phosphor-color': '#ffe1b8', intensity: 0.36, scanlines: 0.085,
        'scanline-spacing': 4, 'scanline-width': 1, glow: 0.105, 'glow-radius': 1.75, vignette: 0.07,
        grain: 0.07, contrast: 0.985}),
    displayLook('vhs-soft-signal', 'VHS Soft Signal', {
        intensity: 0.45, scanlines: 0.07, 'scanline-spacing': 6, 'scanline-width': 1.25, glow: 0.12,
        'glow-radius': 2, vignette: 0.055, grain: 0.1, 'black-level': 0.012, contrast: 0.965}),
    displayLook('crt-clean-signal', 'CRT Clean Signal', {
        intensity: 0.33, scanlines: 0.065, 'scanline-spacing': 4, 'scanline-width': 0.75, grain: 0.055}),
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
