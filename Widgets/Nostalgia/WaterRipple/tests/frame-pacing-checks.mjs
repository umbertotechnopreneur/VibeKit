// SPDX-License-Identifier: GPL-3.0-or-later
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const source = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/frame-pacing.js', import.meta.url), 'utf8');
const {FramePacer} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
for (const display of [60, 120, 144]) {
    for (const limit of [15, 24, 30, 60, 120]) {
        const pacer = new FramePacer(limit);
        let frames = 0;
        for (let i = 0; i < display * 10; i++) {
            // Sub-millisecond callback jitter must not halve a 60 FPS limit.
            const time = i / display + Math.sin(i * 1.7) * 0.0007;
            if (pacer.shouldRender(time)) frames++;
        }
        assert(Math.abs(frames - Math.min(display, limit) * 10) <= 2, `${display} Hz / ${limit}: ${frames}`);
    }
}
const pacer = new FramePacer(60);
assert(pacer.shouldRender(0));
assert(pacer.shouldRender(2), 'Late frame resumes on wall time');
assert(!pacer.shouldRender(2), 'No catch-up burst at the same timestamp');
pacer.setRate(30);
assert(pacer.shouldRender(2));
assert(!pacer.shouldRender(2.016));
assert(pacer.shouldRender(2.033));
console.log('PASS native-frame pacing at 60/120/144 Hz, jitter, live limits and missed-frame recovery');
