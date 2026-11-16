import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';

const source = await readFile(new URL('../Linux/extension/ripple@vibeware.local/recovery.js', import.meta.url), 'utf8');
const {readResourceSample, RecoveryPolicy, retryDelay} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const mem = 'MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\n';
assert.equal(readResourceSample('bad', mem), null);
assert.equal(readResourceSample('cpu 1 2 3 4', 'bad'), null);
assert.deepEqual([1, 2, 3, 4, 5].map(retryDelay), [60, 120, 240, 300, 300]);

let policy = new RecoveryPolicy(0, 60);
const sample = (total, idle, memoryAvailable = 4000000) => ({total, idle, memoryTotal: 8000000, memoryAvailable});
for (let t = 0; t < 60; t += 5)
    assert.equal(policy.sample(t, sample(t * 20, t * 15)), false);
assert.equal(policy.sample(60, sample(1200, 900)), true, 'Healthy resources recover after the cooldown');

policy = new RecoveryPolicy(0, 60);
for (let t = 0; t <= 60; t += 5)
    assert.equal(policy.sample(t, sample(t * 20, t * 2)), false, 'CPU saturation blocks recovery');
for (let t = 65; t <= 100; t += 5)
    assert.equal(policy.sample(t, sample(t * 20, 120 + (t - 60) * 15, 200000)), false, 'Low memory blocks recovery');
for (let t = 105; t < 135; t += 5)
    assert.equal(policy.sample(t, sample(t * 20, 120 + (t - 60) * 15)), false);
assert.equal(policy.sample(135, sample(2700, 1245)), true, 'Resources must be healthy for 30 seconds');
policy = new RecoveryPolicy(0, 0);
assert.equal(policy.sample(0, sample(0, 0)), false);
assert.equal(policy.sample(5, null), false, 'Missing metrics fail closed');
assert.equal(policy.sample(20, sample(400, 300)), false, 'A delayed main loop resets stability');
console.log('PASS recovery cooldown, load, memory, stalls, missing metrics and backoff');
