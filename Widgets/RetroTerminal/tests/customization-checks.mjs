// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const moduleAt = async name => import(`data:text/javascript;base64,${Buffer.from(readFileSync(
    new URL(`../Linux/extension/retro-terminal@vibeware.local/${name}.js`, import.meta.url), 'utf8')).toString('base64')}`);
const {DEFAULTS, BUILTINS, PhosphorMask, readLibrary, savePreset, validateValues} = await moduleAt('settings-model');
const {GlitchSequence} = await moduleAt('glitches');
for (const preset of BUILTINS) assert.deepEqual(validateValues(preset.values), preset.values);
assert.equal(BUILTINS.length, 12);
assert.equal(new Set(BUILTINS.map(({id}) => id)).size, BUILTINS.length);
assert.equal(new Set(BUILTINS.map(({name}) => name)).size, BUILTINS.length);
for (const family of ['Commodore', 'Amiga', 'TV', 'Trinitron']) {
    const looks = BUILTINS.filter(({name}) => name.startsWith(family + ' '));
    assert.equal(looks.length, 3);
    for (const {name, values} of looks) {
        assert(values.intensity >= 0.55 && values.scanlines >= 0.18, name);
        assert(!values['glitches-enabled'] && !values['persistence-enabled'], name);
        if (family === 'Trinitron')
            assert.equal(values.mask, PhosphorMask.RGB_STRIPES, name);
        if (family === 'TV')
            assert(values.vignette >= 0.4, name);
    }
}
const serialized = savePreset('[]', 'My amber', {...DEFAULTS, preset: 1, 'phosphor-color': '#ffc030'});
assert.equal(readLibrary(serialized)[0].name, 'My amber');
assert.equal(readLibrary(serialized)[0].values['phosphor-color'], '#ffc030');
assert.throws(() => savePreset(serialized, 'My amber', DEFAULTS));
assert.throws(() => savePreset('corrupt', 'Do not erase', DEFAULTS));
assert.throws(() => validateValues({...DEFAULTS, active: true}));
assert.throws(() => validateValues({...DEFAULTS, 'glitch-intensity': 1.1}));
assert.throws(() => validateValues({...DEFAULTS, 'phosphor-color': 'not a color'}));
assert.throws(() => validateValues({...DEFAULTS, 'glow-radius': NaN}));
assert.throws(() => savePreset('[]', ' ', DEFAULTS));
const many = JSON.stringify(Array.from({length: 32}, (_, i) => ({name: `Preset ${i}`, values: DEFAULTS})));
assert.throws(() => savePreset(many, 'Overflow', DEFAULTS));

const sequence = new GlitchSequence(() => 0.5);
sequence.configure(DEFAULTS, 0);
assert.equal(sequence.next, Infinity, 'Random starts disabled');
assert(sequence.start(0, true), 'Manual preview works with random disabled');
assert.equal(sequence.event.duration, 0.175);
assert(!sequence.start(0.1, true), 'Episodes never overlap');
assert(sequence.frame(0.1).progress > 0);
assert.equal(sequence.frame(0.2), null);
assert(!sequence.start(2, true), 'A two second rest follows the end');
assert(sequence.start(2.3, true));
sequence.cancel(2.4);
assert.equal(sequence.event, null);
assert.equal(sequence.next, Infinity);
sequence.configure({...DEFAULTS, 'glitches-enabled': true}, 5);
assert.equal(sequence.next, 45);
assert(!sequence.start(44.9));
assert(sequence.start(45));
sequence.configure({...DEFAULTS, 'glitches-enabled': false}, 45.1);
assert.equal(sequence.event, null, 'Turning off random immediately cancels its episode');
assert.equal(sequence.next, Infinity);
sequence.configure({...DEFAULTS, 'glitches-enabled': true, 'glitch-rgb': false,
    'glitch-tear': false, 'glitch-noise': false, 'glitch-band': false}, 50);
assert.equal(sequence.next, Infinity);
assert(!sequence.start(50, true));
sequence.configure({...DEFAULTS, 'glitches-enabled': true, 'glitch-intensity': 0}, 55);
assert.equal(sequence.next, Infinity);
assert(!sequence.start(55, true));
sequence.configure({...DEFAULTS, 'glitches-enabled': true,
    'glitch-interval-min': 60, 'glitch-interval-max': 20}, 60);
assert.equal(sequence.next, 100, 'Reversed bounds are normalized');
console.log('PASS preset validation, corruption preservation, event scheduling, no overlap, stop, rest and type gating');
