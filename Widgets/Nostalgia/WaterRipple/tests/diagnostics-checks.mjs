// Run with Node; GNOME dependencies are mocked so no desktop state is changed.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const recoverySource = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/recovery.js', import.meta.url), 'utf8');
const {RecoveryPolicy, readResourceSample, retryDelay} = await import(`data:text/javascript;base64,${Buffer.from(recoverySource).toString('base64')}`);
const pacingSource = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/frame-pacing.js', import.meta.url), 'utf8');
const {FramePacer} = await import(`data:text/javascript;base64,${Buffer.from(pacingSource).toString('base64')}`);
const source = name => readFileSync(new URL(`../Linux/extension/ripple@vibeware.local/${name}`, import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/gm, '');
const timers = new Map();
let lastDelay = 0;
let useLatestDelay = true;
const logs = [];
const notifications = [];
let nextTimer = 0;
let clock = 100;
let cpu = 0;
let memoryPressure = null;
const GLib = {
    get_monotonic_time: () => clock * 1e6,
    file_get_contents: path => [true, new TextEncoder().encode(path === '/proc/stat'
        ? `cpu ${cpu} 0 0 ${cpu * 3} 0 0 0 0\n`
        : 'MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\n')],
    timeout_add: (_priority, _delay, callback) => { timers.set(++nextTimer, callback); return nextTimer; },
    timeout_add_seconds: (_priority, delay, callback) => {
        lastDelay = delay;
        timers.set(++nextTimer, callback);
        return nextTimer;
    },
    random_int_range: (start, end) => useLatestDelay ? end - 1 : start,
    source_remove: id => timers.delete(id),
};
const scheduleStartupGpuJoke = vm.runInNewContext(
    source('gpu-joke-notifications.js').replace(/^export /gm, '') + '\nscheduleStartupGpuJoke;',
    {GLib});
const Extension = vm.runInNewContext(source('extension.js')
    .replace('export default class RippleExtension', 'class RippleExtension') + '\nRippleExtension;', {
    GLib, Extension: class {}, RecoveryPolicy, readResourceSample, retryDelay, FramePacer, scheduleStartupGpuJoke,
    registerEffectStop: () => () => {},
    MONITOR_SELECTION_KEY: 'target-monitors',
    RipplePanelMenu: class { update() {} updateLease() {} destroy() {} },
    acquireShellEffects: () => ({
        displays: () => [{id: 'test-output'}],
        memoryPressure: () => memoryPressure,
        subscribePressure: () => () => {},
        release() {},
    }),
    selectMonitors: monitors => monitors,
    TextDecoder,
    Clutter: {ModifierType: {BUTTON1_MASK: 1}},
    console: {log: message => logs.push(JSON.parse(message.slice('Ripple: '.length))), warn() {}},
    Main: {wm: {removeKeybinding() {}}, overview: {}, layoutManager: {}, sessionMode: {},
        notify: (title, body) => notifications.push({title, body})},
    global: {stage: {}, get_pointer: () => [0, 0, 0]},
});
const values = {active: true, status: 'Active', target: 'both', fps: 30, 'lease-until': 107.5};
let settingsChanged, gpuJokesChanged;
const settings = {
    get_boolean: key => values[key] ?? false,
    get_double: key => values[key] ?? 0,
    get_int: key => values[key] ?? 0,
    get_strv: key => values[key] ?? ['*'],
    get_value: key => ({deep_unpack: () => values[key] ?? 0}),
    connect(signal, callback) {
        assert.equal(signal, 'changed::show-gpu-jokes');
        gpuJokesChanged = callback;
        return 1;
    },
    disconnect(id) { assert.equal(id, 1); gpuJokesChanged = undefined; },
    set_boolean(key, value) {
        values[key] = value;
        settingsChanged?.(this, key);
        if (key === 'show-gpu-jokes') gpuJokesChanged?.();
    },
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
extension._tick(clock);
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
extension._tick(clock);
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
extension._tick(clock);
assert.equal(extension._recovery.readyAt, clock + 120, 'Repeated failures back off');
settings.set_boolean('recovery-pending', false);
assert.equal(extension._recoveryTimer, 0, 'Explicit stop cancels a pending restart');
assert.equal(values.status, 'Stopped');
values.active = true;
extension._previousFrame = clock - 0.3;
extension._tick(clock);
extension._stop('Stopped by keyboard shortcut');
assert.equal(extension._recoveryTimer, 0, 'Emergency stop cancels recovery');
values.active = true;
extension._previousFrame = clock - 0.3;
extension._tick(clock);
const leaseTimer = extension._recoveryTimer;
clock += 10;
timers.get(leaseTimer)();
timers.delete(leaseTimer);
assert.equal(values.active, false);
assert.equal(values['recovery-pending'], false, 'Expired heartbeat prevents recovery');
extension.disable();
assert.equal(timers.size, 0, 'Recovery timers are cleaned up');

settingsChanged = undefined;
extension.enable();
settings.set_boolean('active', true);
memoryPressure = {availableBytes: 100};
const activationTimer = extension._startTimer;
const activate = timers.get(activationTimer);
timers.delete(activationTimer);
values['lease-until'] = clock + 7.5;
activate();
assert.equal(values.active, false, 'Low memory must refuse activation before starting pointer polling');
assert.equal(extension._pointerTimer, 0);
assert.match(values.status, /system memory is low/);
assert.equal(values['recovery-pending'], false, 'Low memory must not schedule recovery');
extension.disable();

const geometrySource = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/patch-geometry.js', import.meta.url), 'utf8');
const {clipRipplePatch, safePatchSize} = await import(`data:text/javascript;base64,${Buffer.from(geometrySource).toString('base64')}`);
const thinViewport = {x: 0, y: 0, width: 512, height: 100};
const thinPatch = clipRipplePatch(512, 100, 100, 50, thinViewport, safePatchSize(512, 3));
assert((thinPatch.width + 8) * 3 <= 1024, 'A narrow crop must respect the texture limit on its long side');
assert.equal(clipRipplePatch(512, 100, NaN, 50, thinViewport, 512), null);

settingsChanged = undefined;
const jokes = [];
const showJoke = joke => jokes.push(joke);
values['show-gpu-jokes'] = true;
const cancelGpuJokes = scheduleStartupGpuJoke(settings, showJoke);
assert.equal(lastDelay, 240, 'The random delay includes four minutes');
assert.equal(timers.size, 1, 'Startup schedules a single joke timer');
const [jokeTimer, notifyJoke] = timers.entries().next().value;
timers.delete(jokeTimer);
notifyJoke();
assert.deepEqual(jokes, ['Family time won. Gaming time lost. The GPU makes ripples now.']);
assert.equal(timers.size, 0, 'The startup joke must never schedule a follow-up');
assert.equal(gpuJokesChanged, undefined);
settings.set_boolean('show-gpu-jokes', false);
settings.set_boolean('show-gpu-jokes', true);
assert.equal(timers.size, 0, 'Re-enabling jokes after delivery cannot create a timer');
cancelGpuJokes();

useLatestDelay = false;
const cancelBeforeDelivery = scheduleStartupGpuJoke(settings, showJoke);
assert.equal(lastDelay, 120, 'The random delay includes two minutes');
settings.set_boolean('show-gpu-jokes', false);
assert.equal(timers.size, 0, 'Opting out cancels the pending startup joke');
const cancelWhileDisabled = scheduleStartupGpuJoke(settings, showJoke);
settings.set_boolean('show-gpu-jokes', true);
assert.equal(timers.size, 0, 'Opting back in applies at the next startup');
cancelBeforeDelivery();
cancelWhileDisabled();
const cancelOnShutdown = scheduleStartupGpuJoke(settings, showJoke);
assert.equal(timers.size, 1);
cancelOnShutdown();
assert.equal(timers.size, 0, 'Extension shutdown cancels the startup timer');
assert.equal(gpuJokesChanged, undefined);

function checkStartup(autostart) {
    let window, availability;
    const actions = new Map();
    const settingsSection = {expanded: false};
    const handlers = {};
    class Application {
        connect(signal, callback) { handlers[signal] = callback; }
        get_dbus_connection() { return null; }
        activate() { handlers.activate(); }
        add_action(action) { actions.set(action.name, action.activate); }
        run(args) {
            handlers.startup();
            handlers['command-line'](this, {get_arguments: () => args});
        }
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
        CONTROLLER_ID: 'local.vibeware.Ripple', PANEL_BUS_NAME: 'local.vibeware.Ripple.Panel',
        watchMemory: () => 0,
        Gio: {
            File: {new_for_uri: () => ({get_parent: () => ({})})},
            ApplicationFlags: {}, Settings: {new_full: () => settings},
            DBus: {session: {}}, BusNameWatcherFlags: {},
            bus_watch_name_on_connection(_bus, _name, _flags, acquired, lost) {
                availability = available => available ? acquired() : lost();
                return 1;
            },
            SimpleAction: class {
                constructor({name}) { this.name = name; }
                connect(_event, callback) { this.activate = callback; }
            },
        },
        Adw: {Application, ApplicationWindow: Window,
            HeaderBar: class {pack_end() {}}, ToolbarView: class {add_top_bar() {} add_bottom_bar() {}}},
        Gtk: {Button},
        buildControls: () => ({page: {}, footer: {}, settingsSection}),
    }, {filename: 'control.js'});
    assert.equal(window.visible, !autostart);
    availability(false);
    availability(true);
    assert.equal(window.visible, !autostart, 'Tray startup must not reveal the panel');
    actions.get('open')();
    assert.equal(window.visible, true, 'Explicit Open must restore the panel');
    window.hide();
    actions.get('preferences')();
    assert.equal(window.visible, true, 'Preferences must restore the panel');
    assert.equal(settingsSection.expanded, true);
    window.hide();
    availability(false);
    assert.equal(window.visible, true, 'Tray loss after manual use must preserve access');
}
checkStartup(true);
checkStartup(false);
console.log('PASS compact diagnostics, heartbeat silence, stop context, cleanup, hidden autostart and GPU joke scheduling');
