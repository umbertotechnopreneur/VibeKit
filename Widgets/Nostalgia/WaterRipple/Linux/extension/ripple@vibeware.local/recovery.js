// SPDX-License-Identifier: GPL-3.0-or-later
// Keep the policy independent of GNOME so it can be checked without a desktop.
export function readResourceSample(stat, meminfo) {
    const cpu = /^cpu\s+(.+)$/m.exec(stat)?.[1].trim().split(/\s+/).slice(0, 8).map(Number);
    const totalMemory = /^MemTotal:\s+(\d+)\s+kB$/m.exec(meminfo);
    const availableMemory = /^MemAvailable:\s+(\d+)\s+kB$/m.exec(meminfo);
    if (!cpu || cpu.length < 4 || cpu.some(value => !Number.isFinite(value)) || !totalMemory || !availableMemory)
        return null;
    return {
        total: cpu.reduce((sum, value) => sum + value, 0),
        idle: cpu[3] + (cpu[4] || 0),
        memoryTotal: Number(totalMemory[1]),
        memoryAvailable: Number(availableMemory[1]),
    };
}

export class RecoveryPolicy {
    constructor(startedAt, delaySeconds) {
        this.readyAt = startedAt + delaySeconds;
        this.previous = null;
        this.previousAt = null;
        this.stableSince = null;
    }

    sample(time, sample) {
        let healthy = false;
        if (sample && this.previous && this.previousAt !== null) {
            const total = sample.total - this.previous.total;
            const idle = sample.idle - this.previous.idle;
            const elapsed = time - this.previousAt;
            healthy = elapsed > 0 && elapsed <= 7 && total > 0 && idle >= 0 && idle <= total &&
                1 - idle / total < 0.85 && sample.memoryTotal > 0 &&
                sample.memoryAvailable >= Math.max(sample.memoryTotal * 0.10, 512 * 1024);
        }
        this.previous = sample;
        this.previousAt = time;
        if (!healthy)
            this.stableSince = null;
        else if (this.stableSince === null)
            this.stableSince = time;
        return time >= this.readyAt && this.stableSince !== null && time - this.stableSince >= 30;
    }
}

export function retryDelay(failures) {
    return Math.min(300, 60 * 2 ** Math.max(0, failures - 1));
}
