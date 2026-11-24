// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {alphaAt, createPixels, TEXTURE_SIZE, validateAppearance} from '../Linux/extension/vignette@vibeware.local/gradient.js';
import {isHistoryCapture, paintHistory} from '../../Shared/Linux/capture-pass.js';

const catalog = JSON.parse(readFileSync(new URL('../Shared/pipelines.json', import.meta.url), 'utf8'));
assert.equal(catalog.length, 15);
assert.equal(new Set(catalog.map(item => item.id)).size, 15);
assert.equal(new Set(catalog.map(item => item.name)).size, 15);
assert.equal(new Set(catalog.map(item => JSON.stringify(validateAppearance(item)))).size, 15);

const rgb = color => [1, 3, 5].map(offset => parseInt(color.slice(offset, offset + 2), 16));
const luminance = ([r, g, b]) => .2126 * r + .7152 * g + .0722 * b;
assert(catalog.some(preset => luminance(rgb(preset.color)) > 210));
assert(catalog.some(preset => luminance(rgb(preset.color)) < 30));
const read = (pixels, x, y) => pixels.slice(4 * (y * TEXTURE_SIZE + x), 4 * (y * TEXTURE_SIZE + x + 1));
for (const pipeline of catalog) {
    const preset = validateAppearance(pipeline);
    const pixels = createPixels(preset);
    assert.equal(pixels.length, TEXTURE_SIZE ** 2 * 4);
    assert.deepEqual([...read(pixels, 256, 256)], [0, 0, 0, 0]);
    const corner = read(pixels, 0, 0);
    assert(corner[3] > 0);
    assert.deepEqual(corner, read(pixels, TEXTURE_SIZE - 1, 0));
    assert.deepEqual(corner, read(pixels, 0, TEXTURE_SIZE - 1));
    assert(corner[0] <= corner[3] && corner[1] <= corner[3] && corner[2] <= corner[3]);
    for (let i=0; i<100; i++)
        assert(alphaAt(i / 100, i / 100, preset) <= alphaAt((i+1) / 100, (i+1) / 100, preset));
}

const stage = {};
assert.equal(isHistoryCapture(stage), false);
assert.throws(() => paintHistory(stage, () => {
    assert(isHistoryCapture(stage));
    paintHistory(stage, () => assert(isHistoryCapture(stage)));
    assert(isHistoryCapture(stage));
    throw Error('aborted paint');
}), /aborted paint/);
assert.equal(isHistoryCapture(stage), false);
console.log('PASS gradient catalog, alpha symmetry/falloff, compositing and nested history exclusion');
