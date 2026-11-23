// SPDX-License-Identifier: MIT
export const MAX_PIXELS = 9000000;
export const MAX_DESKTOP_PIXELS = 16 * 1024 * 1024;
export const MAX_STAGE_PIXELS = 32 * 1024 * 1024;
export function surfaceAllowed(width, height, scale, limit = MAX_PIXELS, maxDimension = 8192) {
    return [width, height, scale].every(n => Number.isFinite(n) && n > 0) &&
        width * scale <= maxDimension && height * scale <= maxDimension &&
        width * height * scale * scale <= limit;
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
