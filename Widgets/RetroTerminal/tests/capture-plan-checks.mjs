// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const source = readFileSync(new URL('../Linux/extension/retro-terminal@vibeware.local/capture-plan.js', import.meta.url), 'utf8');
const {planCapture} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const maxPixels = 9_000_000;
assert.deepEqual(planCapture(1920, 1080, 1, maxPixels), {width: 1920, height: 1080, scale: 1, reduced: false});
assert.deepEqual(planCapture(1280, 800, 0.5, maxPixels), {width: 640, height: 400, scale: 0.5, reduced: false});
for (const [logicalWidth, logicalHeight, nativeScale] of [
    [4480, 1440, 1.5], [3840, 2160, 2], [9000, 100, 1], [8192, 8192, 4],
    [1920, 1080, 1.25], [4479, 1439, 1.3333333333333333], [1, 1, 10_000],
]) {
    const plan = planCapture(logicalWidth, logicalHeight, nativeScale, maxPixels);
    assert(plan.width * plan.height <= maxPixels && plan.width <= 8192 && plan.height <= 8192);
    assert(plan.scale > 0 && plan.scale <= nativeScale, 'History must never upsample the native capture');
    for (const [logical, physical] of [[logicalWidth, plan.width], [logicalHeight, plan.height]]) {
        assert(physical >= logical * plan.scale && physical - logical * plan.scale < 1,
            'One scale must cover the complete desktop without changing its aspect');
    }
}
const currentLayout = planCapture(4480, 1440, 1.5, maxPixels);
assert(currentLayout.reduced && currentLayout.width > 4480, 'Mixed-scale desktop should retain useful history detail');
for (const bad of [0, -1, Infinity, NaN]) {
    for (let index = 0; index < 5; index++) {
        const args = [4480, 1440, 1.5, maxPixels, 8192];
        args[index] = bad;
        assert.throws(() => planCapture(...args));
    }
}
assert.throws(() => planCapture(4480, 1440, 1.5, 1.5));
console.log(`PASS bounded history sizing, rounding, aspect, invalid inputs; mixed-scale layout ${currentLayout.width}×${currentLayout.height}`);
