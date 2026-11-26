// SPDX-License-Identifier: GPL-3.0-or-later
// Limit native frame-clock updates without accumulating timer drift or issuing
// catch-up bursts. Wave age remains wall-clock time, independent of this gate.
export class FramePacer {
    constructor(fps) { this.setRate(fps); }
    setRate(fps) {
        this.period = 1 / Math.max(15, Math.min(120, fps));
        this.next = null;
    }
    shouldRender(time) {
        if (this.next === null) this.next = time;
        const tolerance = Math.min(0.002, this.period * 0.2);
        if (time + tolerance < this.next) return false;
        this.next += this.period;
        if (this.next <= time + tolerance)
            this.next += (Math.floor((time + tolerance - this.next) / this.period) + 1) * this.period;
        return true;
    }
}
