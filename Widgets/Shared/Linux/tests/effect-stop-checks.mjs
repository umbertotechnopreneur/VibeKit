// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../effect-stop.js', import.meta.url), 'utf8')
    .replaceAll('export function ', 'function ');
const copy = () => vm.runInNewContext(source + '\n({registerEffectStop, stopAllEffects});', {console});
const [ripple, crt, vignette] = [copy(), copy(), copy()];
const stage = {};
const stopped = [];
const removeRipple = ripple.registerEffectStop(stage, () => stopped.push('ripple'));
const removeCrt = crt.registerEffectStop(stage, () => stopped.push('crt'));
const removeVignette = vignette.registerEffectStop(stage, () => stopped.push('vignette'));
crt.stopAllEffects(stage);
assert.deepEqual(stopped, ['ripple', 'crt', 'vignette']);
removeCrt();
stopped.length = 0;
vignette.stopAllEffects(stage);
assert.deepEqual(stopped, ['ripple', 'vignette']);
removeRipple();
removeVignette();
assert.equal(Object.getOwnPropertySymbols(stage).length, 0);
console.log('PASS cross-package stop and unregister');
