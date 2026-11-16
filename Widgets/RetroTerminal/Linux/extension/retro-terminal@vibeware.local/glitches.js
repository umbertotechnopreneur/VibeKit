// SPDX-License-Identifier: MIT
// Pure scheduling model: no timers, settings or scene dependencies.
// These IDs match crtGlitch.x in shader.js; zero means no episode.
export const GlitchType = Object.freeze({NONE: 0, RGB: 1, TEAR: 2, NOISE: 3, BAND: 4});
const GLITCH_SETTINGS = [
    {key: 'glitch-rgb', type: GlitchType.RGB},
    {key: 'glitch-tear', type: GlitchType.TEAR},
    {key: 'glitch-noise', type: GlitchType.NOISE},
    {key: 'glitch-band', type: GlitchType.BAND},
];
export const MIN_REST = 2;
export class GlitchSequence {
    constructor(random = Math.random) {
        this.random = random;
        this.event = null;
        this.restUntil = 0;
        this.next = Infinity;
    }
    configure(values, time) {
        this.values = values;
        this.cancel(time);
        this.schedule(time);
    }
    allowed() {
        return GLITCH_SETTINGS.filter(({key}) => this.values[key]).map(({type}) => type);
    }
    range(low, high) { return Math.min(low, high) + this.random() * Math.abs(high - low); }
    schedule(time) {
        this.next = this.values['glitches-enabled'] && this.allowed().length && this.values['glitch-intensity'] > 0
            ? Math.max(time, this.restUntil) + this.range(this.values['glitch-interval-min'], this.values['glitch-interval-max'])
            : Infinity;
    }
    start(time, manual = false) {
        if (this.event || time < this.restUntil || (!manual && time < this.next)) return false;
        const allowed = this.allowed();
        if (!allowed.length || this.values['glitch-intensity'] <= 0) return false;
        const choiceIndex = Math.min(allowed.length - 1, Math.floor(this.random() * allowed.length));
        const durationMs = this.range(this.values['glitch-duration-min'], this.values['glitch-duration-max']);
        this.event = {type: allowed[choiceIndex], born: time, duration: durationMs / 1000,
            intensity: this.values['glitch-intensity'], seed: this.random() * 1000};
        this.next = Infinity;
        return true;
    }
    frame(time) {
        if (!this.event) return null;
        const progress = Math.max(0, (time - this.event.born) / this.event.duration);
        if (progress >= 1) {
            this.event = null;
            this.restUntil = time + MIN_REST;
            this.schedule(time);
            return null;
        }
        return {...this.event, progress};
    }
    cancel(time) {
        if (this.event) this.restUntil = time + MIN_REST;
        this.event = null;
        this.next = Infinity;
    }
}
