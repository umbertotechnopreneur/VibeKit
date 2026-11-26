// SPDX-License-Identifier: MIT

const STABILITY_SECONDS = 30;
export const MAX_RECOVERY_ATTEMPTS = 2;

export function parseResources(cpuText, memoryText) {
    const aggregate = cpuText.split('\n').find(line => /^cpu\s/.test(line));
    const counters = aggregate?.trim().split(/\s+/).slice(1, 9).map(Number);
    const totalMemory = Number(/^MemTotal:\s+(\d+)\s+kB$/m.exec(memoryText)?.[1]);
    const freeMemory = Number(/^MemAvailable:\s+(\d+)\s+kB$/m.exec(memoryText)?.[1]);
    if (!counters || counters.length < 4 || !counters.every(Number.isSafeInteger) ||
        !Number.isSafeInteger(totalMemory) || totalMemory <= 0 || !Number.isSafeInteger(freeMemory)) return null;
    return {total: counters.reduce((total, value) => total + value, 0), idle: counters[3] + (counters[4] ?? 0),
        totalMemory, freeMemory};
}

export class RecoveryWindow {
    constructor(startedAt, delay, completedAttempts) {
        this.readyAt = startedAt + Math.min(300, delay * (completedAttempts + 1));
        this.previous = null;
        this.stableSince = null;
    }

    ready(time, resources) {
        const previous = this.previous;
        this.previous = resources ? {time, resources} : null;
        const interval = previous ? time - previous.time : 0;
        const total = previous && resources ? resources.total - previous.resources.total : 0;
        const idle = previous && resources ? resources.idle - previous.resources.idle : 0;
        const healthy = resources && interval > 0 && interval <= 7 && total > 0 && idle > total * 0.15 && idle <= total &&
            resources.freeMemory >= Math.max(resources.totalMemory * 0.1, 512 * 1024);
        if (!healthy) this.stableSince = null;
        else this.stableSince ??= time;
        return this.stableSince !== null && time - this.stableSince >= STABILITY_SECONDS && time >= this.readyAt;
    }
}
