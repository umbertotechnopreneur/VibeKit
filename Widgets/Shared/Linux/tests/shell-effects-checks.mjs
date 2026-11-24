// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const signals = new Map();
const timers = new Map();
let activeTimeline = null;
let number = 0;
let enumerated = 0;
let sampled = 0;
let low = true;
let time = 0;
let raised = null;
const stage = {
    get_last_child: () => raised,
    set_child_above_sibling(actor) { raised = actor; },
};
const layout = {
    connect(signal, callback) {
        assert.equal(signal, 'monitors-changed');
        signals.set(++number, callback);
        return number;
    },
    disconnect(id) { assert(signals.delete(id)); },
};
const GLib = {
    get_monotonic_time: () => time * 1e6,
    timeout_add_seconds(_priority, interval, callback) {
        assert.equal(interval, 3);
        timers.set(++number, callback);
        return number;
    },
    source_remove(id) { assert(timers.delete(id)); },
};
const Clutter = {Timeline: {new_for_actor(actor) {
    assert.equal(actor, stage);
    assert.equal(activeTimeline, null);
    return {
        set_repeat_count(count) { assert.equal(count, -1); },
        connect(_signal, cb) { this.frame = cb; return 1; },
        start() { activeTimeline = this; },
        stop() { activeTimeline = null; },
        disconnect() {},
        set_actor() {},
    };
}}};
const getShellMonitors = () => [{id: String(++enumerated)}];
const shellPressure = () => { ++sampled; return low ? {availableBytes: 100} : null; };

// Packages load separate module copies into the same Shell process.
const source = readFileSync(new URL('../shell-effects.js', import.meta.url), 'utf8')
    .replace(/^import .*;\n/gm, '').replace('export function ', 'function ');
const getCopy = () => vm.runInNewContext(source + '\nacquireShellEffects;', {GLib, Clutter, global: {}, shellPressure, getShellMonitors});
const notifications = [];
const ripple = getCopy()(stage, layout, () => notifications.push('ripple'));
const crt = getCopy()(stage, layout, () => notifications.push('crt'));
const vignette = getCopy()(stage, layout, () => notifications.push('vignette'));
assert.equal(signals.size, 1);
const first = ripple.displays();
assert.equal(crt.displays(), first);
assert.equal(vignette.displays(), first);
assert.equal(enumerated, 1);
signals.values().next().value();
assert.deepEqual(notifications, ['ripple', 'crt', 'vignette']);
assert.notEqual(ripple.displays(), first);
assert.equal(crt.displays(), ripple.displays());
assert.equal(enumerated, 2);

const frames = [];
const stopRipple = ripple.subscribeFrames(now => frames.push(['ripple', now]));
const stopCrt = crt.subscribeFrames(now => frames.push(['crt', now]));
time = 2;
activeTimeline.frame();
assert.deepEqual(frames, [['ripple', 2], ['crt', 2]]);
stopRipple();
assert(activeTimeline);
stopCrt();
assert.equal(activeTimeline, null);

const stops = [];
const unwatchRipple = ripple.subscribePressure(() => { stops.push('ripple'); unwatchRipple(); });
const unwatchCrt = crt.subscribePressure(() => { stops.push('crt'); unwatchCrt(); });
const firstPoll = [...timers.values()][0];
firstPoll();
const unwatchVignette = vignette.subscribePressure(() => { stops.push('vignette'); unwatchVignette(); });
assert.equal(timers.size, 1);
firstPoll();
assert.deepEqual(stops, ['ripple', 'crt']);
assert.equal(sampled, 2);
firstPoll();
assert.deepEqual(stops, ['ripple', 'crt', 'vignette']);
assert.equal(timers.size, 0);

const overlay = {get_parent: () => stage};
vignette.registerPostEffect(overlay);
assert.equal(raised, overlay);
raised = null;
crt.raisePostEffect();
assert.equal(raised, overlay);
vignette.release();
crt.release();
assert.equal(signals.size, 1);
ripple.release();
assert.equal(signals.size, 0);
assert.equal(Object.getOwnPropertySymbols(stage).length, 0);
console.log('PASS shared display enumeration, frame clock, pressure sampling, overlay order and hot unload');
