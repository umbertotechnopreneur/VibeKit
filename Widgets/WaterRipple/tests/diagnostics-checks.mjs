// Run with Node; GNOME dependencies are mocked so no desktop state is changed.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const recoverySource = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/recovery.js', import.meta.url), 'utf8');
const {RecoveryPolicy, readResourceSample, retryDelay} = await import(`data:text/javascript;base64,${Buffer.from(recoverySource).toString('base64')}`);
const source = name => readFileSync(new URL(`../Linux/extension/ripple@vibeware.local/${name}`, import.meta.url), 'utf8')
    .replace(/^import .*;\n/gm, '');
const timers = new Map();
const logs = [];
const notifications = [];
let nextTimer = 0;
let clock = 100;
let cpu = 0;
const GLib = {
    get_monotonic_time: () => clock * 1e6,
    file_get_contents: path => [true, new TextEncoder().encode(path === '/proc/stat'
        ? `cpu ${cpu} 0 0 ${cpu * 3} 0 0 0 0\n`
        : 'MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\n')],
    timeout_add: (_priority, _delay, callback) => { timers.set(++nextTimer, callback); return nextTimer; },
    timeout_add_seconds: (_priority, _delay, callback) => { timers.set(++nextTimer, callback); return nextTimer; },
    source_remove: id => timers.delete(id),
};
const Extension = vm.runInNewContext(source('extension.js')
    .replace('export default class RippleExtension', 'class RippleExtension') + '\nRippleExtension;', {
    GLib, Extension: class {}, RecoveryPolicy, readResourceSample, retryDelay, TextDecoder,
    Clutter: {ModifierType: {BUTTON1_MASK: 1}},
    console: {log: message => logs.push(JSON.parse(message.slice('Ripple: '.length)))},
    Main: {wm: {removeKeybinding() {}}, overview: {}, layoutManager: {}, sessionMode: {},
        notify: (title, body) => notifications.push({title, body})},
    global: {stage: {}, get_pointer: () => [0, 0, 0]},
});
const values = {active: true, status: 'Active', target: 'both', fps: 30, 'lease-until': 107.5};
let settingsChanged;
const settings = {
    get_boolean: key => values[key] ?? false,
    get_double: key => values[key] ?? 0,
    get_int: key => values[key] ?? 0,
    get_value: key => ({deep_unpack: () => values[key] ?? 0}),
    set_boolean(key, value) { values[key] = value; settingsChanged?.(this, key); },
    set_string(key, value) { values[key] = value; settingsChanged?.(this, key); },
};
const extension = new Extension();
extension.getSettings = () => settings;
extension._connect = (object, signal, callback) => {
    if (object === settings && signal === 'changed') settingsChanged = callback;
};
extension.enable();
assert.equal(logs.length, 1);
assert.equal(logs[0].event, 'extension-enabled');
assert.equal(logs[0].changed, undefined, 'Startup must not dump all parameters');
const heartbeatTimers = timers.size;
settingsChanged(settings, 'lease-until');
assert.equal(timers.size, heartbeatTimers, 'Heartbeat must not schedule logging');
values.strength = 12;
settingsChanged(settings, 'strength');
values.strength = 14;
settingsChanged(settings, 'strength');
values.speed = 3;
settingsChanged(settings, 'speed');
assert.equal(timers.size, heartbeatTimers + 1, 'Slider changes must coalesce');
extension._logDiagnostic('settings-changed');
assert.deepEqual(JSON.parse(JSON.stringify(logs.at(-1).changed)), {strength: [0, 14], speed: [0, 3]});
const count = logs.length;
extension._logDiagnostic('settings-changed');
assert.equal(logs.length, count, 'Unchanged state must not be logged');
values.active = true;
values.status = 'Active';
extension._previousFrame = 99.7;
extension._tick();
assert.equal(values.active, false);
assert.equal(logs.at(-1).event, 'effect-stopped');
assert.equal(logs.at(-1).delayMs, 300);
assert.equal(logs.at(-1).leaseRemainingMs, 7500);
assert.equal(extension._diagnosticTimer, 0, 'Stop must flush pending settings');
values.active = true;
values['lease-until'] = 90;
timers.get(extension._leaseTimer)();
assert.equal(logs.at(-1).reason, 'Controller heartbeat expired or invalid');
assert.equal(logs.at(-1).leaseRemainingMs, -10000);
extension.disable();
assert.equal(logs.at(-1).event, 'extension-disabled');
assert.equal(timers.size, 0, 'Disable must clean up timers');

values['auto-restart'] = true;
values['lease-until'] = clock + 7.5;
settingsChanged = undefined; // The real extension disconnects its settings signals on disable.
extension.enable();
values.active = true;
extension._previousFrame = clock - 0.3;
extension._tick();
assert.equal(values['recovery-pending'], true, 'Compositor delays schedule recovery');
assert.equal(notifications.at(-1).title, 'Ripple paused');
assert.equal(values.active, false);
const timer = extension._recoveryTimer;
for (let i = 0; i < 12; i++) {
    clock += 5;
    cpu += 100;
    values['lease-until'] = clock + 7.5;
    timers.get(timer)();
}
assert.equal(values.active, true, 'Stable system resumes the effect');
assert.equal(notifications.at(-1).title, 'Ripple resumed');
assert.equal(values['recovery-pending'], false);
timers.delete(timer); // The real GLib removes a source after SOURCE_REMOVE.
extension._previousFrame = clock - 0.3;
extension._tick();
assert.equal(extension._recovery.readyAt, clock + 120, 'Repeated failures back off');
settings.set_boolean('recovery-pending', false);
assert.equal(extension._recoveryTimer, 0, 'Explicit stop cancels a pending restart');
assert.equal(values.status, 'Stopped');
values.active = true;
extension._previousFrame = clock - 0.3;
extension._tick();
extension._stop('Stopped by keyboard shortcut');
assert.equal(extension._recoveryTimer, 0, 'Emergency stop cancels recovery');
values.active = true;
extension._previousFrame = clock - 0.3;
extension._tick();
const leaseTimer = extension._recoveryTimer;
clock += 10;
timers.get(leaseTimer)();
timers.delete(leaseTimer);
assert.equal(values.active, false);
assert.equal(values['recovery-pending'], false, 'Expired heartbeat prevents recovery');
extension.disable();
assert.equal(timers.size, 0, 'Recovery timers are cleaned up');

function checkStartup(autostart) {
    let window, availability, actions;
    const handlers = {};
    class Application {
        connect(signal, callback) { handlers[signal] = callback; }
        get_dbus_connection() { return null; }
        activate() { handlers.activate(); }
        run(args) { handlers['command-line'](this, {get_arguments: () => args}); }
    }
    class Window {
        constructor() { window = this; this.visible = false; }
        connect() {}
        set_content() {}
        unminimize() {}
        present() { this.visible = true; }
        hide() { this.visible = false; }
    }
    class Button { connect() {} set_sensitive() {} }
    vm.runInNewContext(source('control.js').replaceAll('import.meta.url', "'file:///ripple/control.js'"), {
        ARGV: autostart ? ['--autostart'] : [],
        GLib, loadSchema: () => ({}),
        Gio: {File: {new_for_uri: () => ({get_parent: () => ({})})},
            ApplicationFlags: {}, Settings: {new_full: () => settings}},
        Adw: {Application, ApplicationWindow: Window,
            HeaderBar: class {pack_end() {}}, ToolbarView: class {add_top_bar() {}}},
        Gtk: {Button},
        buildControls: () => ({page: {}, active: {connect() {}, active: true, sensitive: true}}),
        TrayIndicator: class {
            constructor(callbacks, callback) { actions = callbacks; availability = callback; }
            setEffect() {}
        },
    }, {filename: 'control.js'});
    assert.equal(window.visible, !autostart);
    availability(false);
    availability(true);
    assert.equal(window.visible, !autostart, 'Tray startup must not reveal the panel');
    actions.show();
    assert.equal(window.visible, true, 'Explicit Open must restore the panel');
    window.hide();
    availability(false);
    assert.equal(window.visible, true, 'Tray loss after manual use must preserve access');
}
checkStartup(true);
checkStartup(false);
console.log('PASS compact diagnostics, heartbeat silence, stop context, cleanup and hidden autostart');
