// SPDX-License-Identifier: MIT
import assert from 'node:assert/strict';
import {getShellMonitors, parseDisplayState, selectMonitors, updateMonitorSelection} from '../monitor-selection.js';

const devices = [
    ['DP-2', 'Main display', 1],
    ['eDP-1', 'Built-in display', 0],
    ['HDMI-1', 'Preview display', 1],
    ['DP-3', 'Disabled display', -1],
];
const manager = {
    get_monitors: () => devices.map(([connector, name]) => ({
        get_connector: () => connector,
        get_display_name: () => name,
    })),
    get_monitor_for_connector: connector => devices.find(device => device[0] === connector)[2],
};
const geometry = [
    {index: 0, x: 0, y: 0, width: 1440, height: 900},
    {index: 1, x: 1440, y: 0, width: 2560, height: 1440},
];
const shell = getShellMonitors(geometry, {get_monitor_scale: index => index === 0 ? 1.5 : 1}, manager);
const physical = devices.map(([connector, name]) => [[connector, '', '', ''], [], {'display-name': name}]);
const panel = parseDisplayState([17, physical, [
    [0, 0, 1.5, 0, true, [['eDP-1', '', '', '']], {}],
    [1440, 0, 1, 0, false, [['DP-2', '', '', ''], ['HDMI-1', '', '', '']], {}],
], {}]);

assert.deepEqual(shell.map(display => display.ids), panel.map(display => display.ids));
assert.deepEqual(shell.map(display => display.scale), [1.5, 1]);
assert.deepEqual(shell.map(display => display.width), [1440, 2560]);
assert.equal(panel[0].name, 'Built-in display');
assert.equal(panel[1].name, 'Main display / Preview display');
assert.deepEqual(selectMonitors(shell, ['*']), shell);
assert.deepEqual(selectMonitors(shell, []), []);
assert.deepEqual(selectMonitors(shell, ['HDMI-1', 'DP-2']), [shell[1]]);
assert.deepEqual(selectMonitors(shell, ['DP-3']), []);
assert.deepEqual(updateMonitorSelection(panel, ['*'], panel[1], false), ['eDP-1']);
assert.deepEqual(updateMonitorSelection(panel, ['eDP-1'], panel[0], false), []);
assert.deepEqual(updateMonitorSelection(panel, ['DP-3'], panel[1], true), ['DP-2', 'DP-3', 'HDMI-1']);
assert.deepEqual(updateMonitorSelection(panel, ['DP-3', 'HDMI-1'], panel[1], false), ['DP-3']);

const anonymous = parseDisplayState([17, [], [[1440, 0, 1, 0, true, [], {}]], {}]);
assert.deepEqual(selectMonitors(anonymous, [anonymous[0].id]), anonymous);
assert.deepEqual(updateMonitorSelection(anonymous, ['*'], anonymous[0], false), []);

console.log('GNOME monitor selection checks passed');
