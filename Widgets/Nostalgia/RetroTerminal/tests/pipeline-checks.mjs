// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const root = new URL('../Linux/extension/retro-terminal@vibeware.local/', import.meta.url);
const dataModule = source => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
const settingsModule = dataModule(readFileSync(new URL('settings-model.js', root), 'utf8'));
const {DEFAULTS, BUILTINS} = await import(settingsModule);
const {PipelineModel, PipelineConflictError, parsePipelines} = await import(dataModule(readFileSync(new URL('pipeline-model.js', root), 'utf8')
    .replace("'./settings-model.js'", JSON.stringify(settingsModule))));

function harness(initial = null) {
    const shared = {contents: initial, writes: 0, fail: false};
    let id = 0;
    function open(values = {...DEFAULTS}) {
        const state = {...values};
        const callbacks = new Map();
        let serial = 0;
        const adapter = {
            read: () => Object.fromEntries(Object.keys(DEFAULTS).map(key => [key, state[key]])),
            apply: values => { Object.assign(state, values); for (const key of Object.keys(values)) model.changed(key); },
        };
        const storage = {read: () => shared.contents, id: () => `personal-${++id}`, write(text, expected) {
            if (shared.fail) throw Error('Injected disk failure');
            if (shared.contents !== expected) throw new PipelineConflictError('Library changed elsewhere');
            shared.contents = text; shared.writes++;
        }};
        const scheduler = {schedule: callback => { callbacks.set(++serial, callback); return serial; }, cancel: token => callbacks.delete(token)};
        const model = new PipelineModel(adapter, storage, scheduler);
        return {model, state, callbacks, edit(key, value) { state[key] = value; model.changed(key); },
            fire() { const pending = [...callbacks.values()]; callbacks.clear(); pending.forEach(callback => callback()); }};
    }
    return {shared, open};
}

const h = harness();
const a = h.open();
assert.equal(a.model.items.length, BUILTINS.length);
assert.equal(a.model.selected, null);
assert.equal(h.shared.writes, 0, 'Opening an empty panel must not create a library.');
a.model.select(a.model.items[0]);
assert.deepEqual(a.state, BUILTINS[0].values);
const builtin = a.model.selected;
a.model.rename(builtin, 'Forbidden');
assert.match(a.model.error, /Duplicate/);
assert.equal(a.model.selected, builtin);
a.model.delete(builtin);
assert.equal(a.model.items.length, BUILTINS.length);
a.model.duplicate(builtin);
assert.equal(a.model.selected.name, `${builtin.name} copy`);
assert(!a.model.builtIn(a.model.selected));
const selectedId = a.model.selected.id;
const writeStart = h.shared.writes;
for (let i = 0; i < 100; i++) a.edit('scanlines', i / 200);
assert.equal(h.shared.writes, writeStart, 'Slider updates should remain live without one disk write per tick.');
assert.equal(a.callbacks.size, 1);
assert.equal(a.model.selected.values.scanlines, 0.495);
a.fire();
assert.equal(h.shared.writes, writeStart + 1);
assert.equal(JSON.parse(h.shared.contents).pipelines[0].values.scanlines, 0.495);
a.edit('show-gpu-jokes', false);
a.edit('active', true);
assert.equal(a.callbacks.size, 0, 'Runtime and UI-only controls cannot change a pipeline.');
const reopened = h.open(a.state);
assert.equal(reopened.model.selected.id, selectedId);
assert.equal(h.open({...a.state, contrast: 1.5}).model.selected, null, 'Opening never reapplies an unmatched saved look.');

const stale = h.open(a.state);
a.model.rename(a.model.selected, 'My green');
const saved = h.shared.contents;
stale.model.create('Stale copy');
assert.match(stale.model.error, /changed/);
assert.equal(h.shared.contents, saved);
assert.equal(stale.model.items.length, BUILTINS.length + 1);
assert(!stale.model.writable && !stale.model.selected, 'A stale writer must detach until the panel is reopened.');

h.shared.fail = true;
const before = {...a.state};
a.model.select(a.model.items[1]);
assert.match(a.model.error, /disk failure/);
assert.equal(a.model.selected.id, selectedId);
assert.deepEqual(a.state, before, 'Failed selection must restore all prior values.');
a.model.create('Failed creation');
assert.equal(a.model.items.length, BUILTINS.length + 1);
a.model.rename(a.model.selected, 'Failed rename');
assert.equal(a.model.selected.name, 'My green');
a.model.delete(a.model.selected);
assert.equal(a.model.items.length, BUILTINS.length + 1);
a.edit('phosphor-color', '#eecc11');
a.fire();
assert.match(a.model.error, /disk failure/);
assert.equal(a.state['phosphor-color'], before['phosphor-color']);
assert.equal(a.model.selected.values['phosphor-color'], before['phosphor-color']);
assert.equal(h.shared.contents, saved);
h.shared.fail = false;
a.model.rename(a.model.selected, '  My cyan  ');
assert.equal(a.model.selected.name, 'My cyan');
a.edit('phosphor-color', '#00ccff');
a.model.dispose();
assert.equal(JSON.parse(h.shared.contents).pipelines[0].values['phosphor-color'], '#00ccff', 'Close must flush pending edits.');
assert.equal(a.callbacks.size, 0);
a.model.dispose();

const roundtrip = h.open({...a.state});
const text = roundtrip.model.export();
assert.equal(JSON.parse(text).selectedId, undefined, 'Exports do not select or activate a pipeline on another machine.');
roundtrip.model.import(text);
assert.equal(roundtrip.model.items.length, BUILTINS.length + 2);
assert.equal(roundtrip.model.items.at(-1).name, 'My cyan 2');
assert.notEqual(roundtrip.model.items.at(-1).id, selectedId);
assert.equal(roundtrip.model.selected.id, selectedId);
roundtrip.model.detach();
assert.equal(roundtrip.model.selected, null);
const oldText = h.shared.contents;
roundtrip.edit('contrast', 1.4);
assert.equal(h.shared.contents, oldText, 'Custom edits must not mutate a saved pipeline.');
roundtrip.model.create('Current custom');
assert.equal(roundtrip.model.selected.values.contrast, 1.4);
roundtrip.model.delete(roundtrip.model.selected);
assert.equal(roundtrip.model.selected, null);
roundtrip.model.select(roundtrip.model.items[0]);
roundtrip.edit('glow', 0.65);
assert.equal(roundtrip.model.selected, null, 'Editing a built-in switches to Custom.');
assert.equal(roundtrip.model.items[0].values.glow, BUILTINS[0].values.glow);
roundtrip.fire();

const valid = JSON.parse(text);
for (const mutate of [
    d => { d.app = 'ripple'; },
    d => { d.pipelines[0].values = {}; },
    d => { delete d.pipelines[0].values.scanlines; },
    d => { d.selectedId = 'missing-pipeline'; },
    d => { d.pipelines[0].values.active = true; },
    d => { d.pipelines[0].values.glow = 500; },
    d => { d.pipelines[0].values['phosphor-color'] = 'oops'; },
    d => { d.pipelines[0].values['custom-color'] = 1; },
    d => { d.pipelines[0].id = 'builtin:0'; },
    d => { d.pipelines.push({...d.pipelines[0]}); },
]) {
    const document = structuredClone(valid); mutate(document);
    assert.throws(() => parsePipelines(JSON.stringify(document)));
}
assert.throws(() => parsePipelines('{broken'));
assert.throws(() => parsePipelines(' '.repeat(512 * 1024 + 1)));
const corrupt = harness('{broken').open();
assert(!corrupt.model.writable);
corrupt.model.create('Never overwrite');
assert.equal(corrupt.model.items.length, BUILTINS.length);
assert.throws(() => corrupt.model.export());

const capacity = harness().open();
for (let i = 0; i < 64; i++) assert(capacity.model.create(`Pipeline ${i}`));
assert(!capacity.model.create('Overflow'));
assert.match(capacity.model.error, /64/);
const locked = harness().open();
locked.model.adapter.apply = () => { throw Error('Settings backend is read-only'); };
assert.doesNotThrow(() => locked.model.select(locked.model.items[1]));
assert.match(locked.model.error, /could not be restored/);
assert.equal(locked.model.selected, null);

// Unlike independent test adapters, real GSettings notifies every open panel.
// A losing writer must not reapply the old value and make the winner save it.
function sharedSettingsPanels() {
    let contents = null;
    let serial = 0;
    let blocked = null;
    const values = {...DEFAULTS};
    const panels = [];
    const update = changes => {
        for (const [key, value] of Object.entries(changes)) {
            if (values[key] === value) continue;
            values[key] = value;
            for (const panel of panels) panel.model.changed(key);
        }
    };
    const open = name => {
        const pending = new Map();
        let timer = 0;
        const model = new PipelineModel({read: () => ({...values}), apply: update}, {
            id: () => `shared-${++serial}`, read: () => contents,
            write(text, expected) {
                if (blocked === name) throw new PipelineConflictError('Library is busy; reopen this panel');
                if (contents !== expected) throw new PipelineConflictError('Library changed; reopen this panel');
                contents = text;
            },
        }, {schedule: callback => { pending.set(++timer, callback); return timer; }, cancel: id => pending.delete(id)});
        const panel = {model, pending}; panels.push(panel); return panel;
    };
    return {open, update, values, get contents() { return contents; }, block: name => { blocked = name; }};
}
for (const conflict of ['stale', 'busy']) {
    const shared = sharedSettingsPanels();
    const winner = shared.open('winner');
    winner.model.create('Shared look');
    const observer = shared.open('observer');
    shared.update({intensity: 0.42});
    assert.equal(winner.pending.size, 1);
    assert.equal(observer.pending.size, 1);
    if (conflict === 'stale') winner.model.flush();
    else shared.block('observer');
    assert.equal(observer.model.flush(), false);
    assert.equal(shared.values.intensity, 0.42, `${conflict} observer reverted global settings`);
    assert.equal(observer.model.selected, null);
    assert.equal(observer.model.writable, false);
    assert.equal(winner.model.selected.values.intensity, 0.42, `${conflict} observer undid the winning edit`);
    winner.model.flush();
    assert.equal(JSON.parse(shared.contents).pipelines[0].values.intensity, 0.42);
    shared.update({intensity: 0.53});
    assert.equal(observer.pending.size, 0, 'Conflicted observers must remain detached.');
    assert.equal(observer.model.select(observer.model.items[1]), false);
    assert.equal(shared.values.intensity, 0.53, 'A frozen panel still applied a pipeline.');
    winner.model.flush();
    assert.equal(JSON.parse(shared.contents).pipelines[0].values.intensity, 0.53);
    observer.model.dispose(); winner.model.dispose();
}
console.log('PASS CRT pipelines: CRUD, read-only built-ins, coalesced edits, rollback, stale writers, selection restore, portable import/export and validation');
