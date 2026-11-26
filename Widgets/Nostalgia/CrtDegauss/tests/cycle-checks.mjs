// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';

const source = new URL('../Linux/extension/crt-degauss@vibeware.local/cycle.js', import.meta.url);
const {DEGAUSS_SECONDS, MagnetizationCycle} = await import(
    `data:text/javascript;base64,${(await readFile(source)).toString('base64')}`);

const cycle = new MagnetizationCycle();
cycle.start(100);
assert.equal(cycle.sample(100, 15).amount, 0);
assert.equal(cycle.sample(550, 15).amount, 0.5);
assert.equal(cycle.sample(999, 15).notify, false);
assert.deepEqual(cycle.sample(1000, 15), {amount: 1, pulse: -1, remaining: 0, due: true, notify: true});
assert.equal(cycle.sample(1200, 15).notify, false);
assert.equal(cycle.sample(1200, 20).due, false);
assert.equal(cycle.sample(1301, 20).notify, false);
assert.equal(cycle.degauss(1500, 15), true);
assert.equal(cycle.degauss(1500.2, 15), false);
const pulse = cycle.sample(1500 + DEGAUSS_SECONDS / 2, 15);
assert.ok(Math.abs(pulse.amount - 0.5) < 1e-9);
assert.ok(Math.abs(pulse.pulse - 0.5) < 1e-9);
assert.deepEqual(cycle.sample(1502, 15), {amount: 0, pulse: -1, remaining: 900, due: false, notify: false});
assert.equal(cycle.sample(2402, 15).notify, true);
cycle.start(3000);
assert.equal(cycle.sample(3059, 1).notify, false);
assert.equal(cycle.sample(3060, 1).notify, true);
cycle.start(4000);
assert.equal(cycle.showNow(4001, 15), true);
assert.deepEqual(cycle.sample(4001, 15), {amount: 1, pulse: -1, remaining: 0, due: true, notify: false});
assert.equal(cycle.degauss(4002, 15), true);
assert.equal(cycle.showNow(4002.1, 15), false);
assert.equal(cycle.sample(4004, 15).amount, 0);
console.log('PASS GNOME magnetization cycle and one reminder per cycle');
