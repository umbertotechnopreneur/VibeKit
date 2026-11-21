// SPDX-License-Identifier: MIT
export const MAX_PIXELS = 9000000;
export function surfaceAllowed(width, height, scale) {
    return [width, height, scale].every(n => Number.isFinite(n) && n > 0) &&
        width * scale <= 8192 && height * scale <= 8192 &&
        width * height * scale * scale <= MAX_PIXELS;
}

// A guard observes event-loop delays; these are not GPU frame timings.
export class DelayGuard {
    constructor(now) { this.previous = now; this.lastDelay = 0; this.strikes = 0; }
    sample(now) {
        const delay = this.lastDelay = now - this.previous;
        this.previous = now;
        this.strikes = delay > 0.5 ? this.strikes + 1 : Math.max(0, this.strikes - 1);
        return !Number.isFinite(delay) || delay < 0 || delay > 1.5 || this.strikes >= 3;
    }
}
