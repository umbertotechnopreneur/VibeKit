import assert from 'node:assert/strict';
import {readFileSync, writeFileSync} from 'node:fs';
import {PipelineModel} from '../Shared/pipeline-model.js';

const catalog = JSON.parse(readFileSync(new URL('../Shared/pipeline-catalog.json', import.meta.url)));
let contents = null, id = 0;
const values = Object.fromEntries(catalog.parameters.map(p => [p.key, p.default]));
const model = new PipelineModel(catalog, {
    supports: () => true, get: key => values[key],
    clamp: (key, value) => { const p = catalog.parameters.find(p => p.key === key); return Math.max(p.min, Math.min(p.max, value)); },
    set(key, value) { values[key] = typeof value === 'number' ? this.clamp(key, value) : value; model.changed(key); },
}, {read: () => contents, write: value => { contents = value; }, id: () => `test-${++id}`});
model.select(model.items[0]); assert.equal(values.strength, 7);
values.strength = 8; model.changed('strength'); assert.equal(model.selected, null);
model.create('From Linux'); values.rings = 4; model.changed('rings');
const filter = catalog.groups[1]; model.applyFilterPreset(filter.presets[2]);
assert.equal(values['chromatic-separation'], 45); assert.equal(model.filterPresetIndex(filter), 3);
assert.equal(JSON.parse(contents).pipelines[0].values.rings, 4);
model.rename(model.selected, 'Renamed'); model.duplicate(model.selected);
assert.equal(model.selected.name, 'Renamed copy'); model.delete(model.selected);
const custom = model.items.find(p => p.name === 'Renamed'); model.rename(custom, 'From Linux');
const before = model.items.length;
for (const value of [null, true, 1000, '4']) {
    model.import(JSON.stringify({version: 1, pipelines: [{id: 'bad', name: 'Bad', values: {strength: value}}]}));
    assert.ok(model.error); assert.equal(model.items.length, before);
}
model.create('From Linux'); assert.ok(model.error); assert.equal(model.items.length, before);
assert.throws(() => model.parse('{"version":2,"pipelines":[]}'));
assert.throws(() => model.parse('{"version":1,"pipelines":[{"id":"builtin:x","name":"Bad","values":{}}]}'));
const exchange = process.env.RIPPLE_EXCHANGE;
if (exchange && process.argv.includes('--read')) {
    model.import(readFileSync(exchange, 'utf8')); assert.equal(model.error, '');
    assert.ok(model.items.some(p => p.name === 'Water' && p.values['liquid-enabled']));
} else if (exchange) writeFileSync(exchange, model.export());
let corrupt = 'broken';
const broken = new PipelineModel(catalog, model.adapter, {read: () => corrupt, write: value => { corrupt = value; }, id: () => 'safe'});
broken.create('Safe'); assert.ok(broken.error); assert.equal(corrupt, 'broken');
const empty = new PipelineModel(catalog, model.adapter, {read: () => '', write: () => { throw Error('Must not write'); }, id: () => 'empty'});
empty.create('Safe'); assert.ok(empty.error); assert.equal(empty.items.length, catalog.presets.length);
console.log('PASS Linux model: live application, CRUD, persistence, validation, corrupt input and portable exchange');

// Deterministic scheduling and compare-and-replace storage exercise complete transactions.
let durable = null, failWrite = false, writes = 0;
const callbacks = new Map();
const scheduler = {schedule: callback => { const token = ++id; callbacks.set(token, callback); return token; }, cancel: token => callbacks.delete(token)};
function fixture() {
    const state = Object.fromEntries(catalog.parameters.map(p => [p.key, p.default]));
    const adapter = {supports: () => true, get: key => state[key], clamp: (_key, value) => value,
        set: (key, value) => { state[key] = value; }};
    const storage = {id: () => `fixture-${++id}`, read: () => durable,
        write(contents, expected) {
            if (failWrite) throw Error('Disk full');
            if (durable !== expected) throw Error('Library changed');
            durable = contents; writes++;
        }};
    return {model: new PipelineModel(catalog, adapter, storage, scheduler), state};
}
const first = fixture(), stale = fixture();
first.model.create('Saved'); const original = durable;
stale.model.create('Must not overwrite');
assert.ok(stale.model.error); assert.equal(durable, original);
assert.equal(stale.model.items.length, catalog.presets.length); assert.equal(stale.model.selected, null);
const savedPipeline = first.model.selected;
failWrite = true;
for (const action of [() => first.model.rename(savedPipeline, 'Unsaved'), () => first.model.delete(savedPipeline),
    () => first.model.create('Unsaved'), () => first.model.duplicate(savedPipeline),
    () => first.model.import(JSON.stringify({version: 1, pipelines: [{id: 'import', name: 'Unsaved', values: {}}]})),
    () => first.model.select(first.model.items[0])]) {
    action(); assert.ok(first.model.error); assert.equal(durable, original);
    assert.equal(first.model.items.length, catalog.presets.length + 1);
    assert.equal(first.model.selected, savedPipeline); assert.equal(savedPipeline.name, 'Saved');
    assert.equal(first.state.strength, savedPipeline.values.strength);
}
failWrite = false;
writes = 0;
for (let i = 0; i < 120; i++) { first.state.strength = 1 + i / 100; first.model.changed('strength'); }
assert.equal(writes, 0); assert.equal(callbacks.size, 1); assert.equal(savedPipeline.values.strength, 2.19);
first.model.flush();
assert.equal(writes, 1); assert.equal(callbacks.size, 0);
assert.equal(JSON.parse(durable).pipelines[0].values.strength, 2.19);
first.state.strength = 7; first.model.changed('strength'); failWrite = true;
assert.equal(first.model.flush(), false); assert.equal(first.state.strength, 2.19); assert.equal(savedPipeline.values.strength, 2.19);
failWrite = false;
const concurrent = fixture();
first.state.strength = 8; first.model.changed('strength'); concurrent.model.rename(concurrent.model.items.at(-1), 'External');
const external = durable;
assert.equal(first.model.flush(), false); assert.equal(durable, external); assert.equal(first.state.strength, 2.19);
const closing = fixture(); closing.model.select(closing.model.items.at(-1));
closing.state.strength = 6; closing.model.changed('strength'); closing.model.dispose();
assert.equal(JSON.parse(durable).pipelines[0].values.strength, 6); assert.equal(callbacks.size, 0);
console.log('PASS pipeline conflicts, transaction rollback, 120 changes / one write, deferred errors and final flush');
