// SPDX-License-Identifier: MIT
// Symbol.for shares the marker across the self-contained extension packages.
const captureDepth = Symbol.for('biz.umbertogiacobbi.VibeWare.historyCaptureDepth');

export function isHistoryCapture(stage) {
    return (stage[captureDepth] ?? 0) > 0;
}

export function paintHistory(stage, paint) {
    const previous = stage[captureDepth] ?? 0;
    stage[captureDepth] = previous + 1;
    try {
        return paint();
    } finally {
        stage[captureDepth] = previous;
    }
}
