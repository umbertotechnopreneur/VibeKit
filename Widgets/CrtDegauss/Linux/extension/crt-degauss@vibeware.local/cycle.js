// SPDX-License-Identifier: MIT
export const DEGAUSS_SECONDS = 1.8;

export class MagnetizationCycle {
    start(now) {
        this.started = now;
        this.degaussing = false;
        this.prompted = false;
    }

    degauss(now, minutes) {
        if (this.degaussing) return false;
        this.beforeDegauss = this._strength(now, minutes);
        this.degaussStarted = now;
        this.degaussing = true;
        return true;
    }

    sample(now, minutes) {
        if (this.degaussing) {
            const progress = Math.max(0, (now - this.degaussStarted) / DEGAUSS_SECONDS);
            if (progress < 1) {
                return {amount: this.beforeDegauss * (1 - smooth(progress)), pulse: progress,
                    remaining: 0, due: false, notify: false};
            }
            this.start(now);
        }
        const remaining = Math.max(0, this.started + minutes * 60 - now);
        const due = remaining === 0;
        const notify = due && !this.prompted;
        this.prompted ||= due;
        return {amount: this._strength(now, minutes), pulse: -1, remaining, due, notify};
    }

    _strength(now, minutes) {
        return smooth(Math.min(1, Math.max(0, (now - this.started) / (minutes * 60))));
    }
}

function smooth(t) {
    return t * t * (3 - 2 * t);
}
