// SPDX-License-Identifier: MIT
// Independently packaged extensions share this registry through the Shell stage.
const stopKey = Symbol.for('biz.umbertogiacobbi.VibeWare.effectStop.v1');

export function registerEffectStop(stage, stop) {
    const stops = stage[stopKey] ??= new Set();
    stops.add(stop);
    return () => {
        stops.delete(stop);
        if (!stops.size && stage[stopKey] === stops) delete stage[stopKey];
    };
}

export function stopAllEffects(stage) {
    const stops = stage[stopKey];
    if (!stops) return;
    for (const stop of [...stops]) {
        if (!stops.has(stop)) continue;
        try { stop(); }
        catch (error) { console.error('VibeWare effect stop:', error); }
    }
}
