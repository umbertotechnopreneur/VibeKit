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
console.log('PASS Linux model: live application, CRUD, persistence, validation, corrupt input and portable exchange');
