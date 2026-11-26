// SPDX-License-Identifier: GPL-3.0-or-later
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

class Toggle {
    constructor() { this.state = false; }
    connect(_signal, callback) { this.changed = callback; }
    setToggleState(state) {
        if (state === this.state) return;
        this.state = state;
        this.changed?.(this, state); // GNOME emits this even on programmatic changes.
    }
    setSensitive() {}
}

class MenuItem {
    constructor(text) { this.label = {text}; }
    setSensitive() {}
}

const source = readFileSync(new URL('../Linux/extension/ripple@vibeware.local/panel-menu.js', import.meta.url), 'utf8')
    .replace(/^import .*;\n/gm, '').replace('export class ', 'class ');
let managerCallback;
const manager = {
    connect(_, callback) { managerCallback = callback; return 1; },
    lookup() { return null; },
    enableExtension() { return true; },
    disconnect() { managerCallback = null; },
};
const PopupMenu = {PopupSwitchMenuItem: Toggle, PopupMenuItem: MenuItem, PopupSeparatorMenuItem: class {}};
const PanelMenu = {Button: class {
    menu = {addAction: () => new MenuItem(''), addMenuItem() {}, connect() {}};
    add_child() {}
    destroy() {}
}};
const timers = new Map();
let nextTimer = 0;
const GLib = {
    timeout_add_seconds(_priority, _seconds, call) { timers.set(++nextTimer, call); return nextTimer; },
    source_remove(id) { assert(timers.delete(id)); },
};
const Gio = {
    Cancellable: class { cancel() {} },
    FileIcon: class { constructor({file}) { this.file = file; } },
    DBus: {session: {}}, BusNameOwnerFlags: {}, BusNameWatcherFlags: {},
    bus_own_name_on_connection() { return 1; },
    bus_unown_name() {},
    bus_watch_name_on_connection() { return 2; },
    bus_unwatch_name() {},
};
const Main = {extensionManager: manager, panel: {addToStatusArea() {}}};
const RipplePanelMenu = vm.runInNewContext(source + '\nRipplePanelMenu;', {
    Gio, GLib, Main, PopupMenu, PanelMenu, St: {Icon: class {}},
    PANEL_BUS_NAME: 'Ripple.Panel', CONTROLLER_ID: 'Ripple', ExtensionState: {ACTIVE: 1},
});

const values = new Map([['active', false]]);
const settings = {
    get_string: () => 'Ready', get_boolean: key => values.get(key) ?? false,
    set_boolean: (key, value) => values.set(key, value), is_writable: () => true,
};
const menu = new RipplePanelMenu({dir: {resolve_relative_path: name => name}}, settings, () => true, () => settings.set_boolean('active', false));
menu._controllerAction = () => {};
const crt = {uuid: 'retro-terminal@vibeware.local', state: 1};
const vignette = {uuid: 'vignette@vibeware.local', state: 1};
const peer = extension => {
    let active = false;
    return {extension, settings: {
        get_boolean: () => active,
        set_boolean(_key, value) { active = value; menu.update(); },
        disconnect() {},
    }};
};
const peers = [peer(crt), peer(vignette)];
for (const value of peers) menu._peers.set(value.extension.uuid, value);

menu._all.setToggleState(true);
assert(peers.every(({settings: peerSettings}) => peerSettings.get_boolean('active')));
assert.equal(settings.get_boolean('active'), true);
menu._effect.setToggleState(false);
assert(peers.every(({settings: peerSettings}) => peerSettings.get_boolean('active')));
assert.equal(settings.get_boolean('active'), false);
assert.equal(menu._all.state, false);
menu._all.setToggleState(true);
menu._all.setToggleState(false);
assert(peers.every(({settings: peerSettings}) => !peerSettings.get_boolean('active')));
assert.equal(settings.get_boolean('active'), false);

vignette.state = 2;
menu._all.setToggleState(true);
assert.equal(timers.size, 1);
menu._all.setToggleState(false); // Cancel the pending activation before the extension becomes active.
vignette.state = 1;
managerCallback(manager, vignette);
assert.equal(peers[1].settings.get_boolean('active'), false);
assert.equal(timers.size, 0);
menu.destroy();
console.log('PASS native-menu state synchronization, individual effect, all effects and pending cancellation');
