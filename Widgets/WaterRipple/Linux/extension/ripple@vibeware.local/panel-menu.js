// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {ExtensionState} from 'resource:///org/gnome/shell/misc/extensionUtils.js';
import {CONTROLLER_ID, CONTROLLER_PATH, PANEL_BUS_NAME} from './panel-ipc.js';

const PEERS = [
    ['retro-terminal@vibeware.local', 'org.gnome.shell.extensions.vibeware-retro-terminal'],
    ['vignette@vibeware.local', 'org.gnome.shell.extensions.vibeware-vignette'],
];

export class RipplePanelMenu {
    constructor(extension, settings, leaseValid, stop) {
        this._extension = extension;
        this._settings = settings;
        this._leaseValid = leaseValid;
        this._stop = stop;
        this._peers = new Map();
        this._pending = new Set();
        this._enableTimeout = 0;
        this._syncing = false;
        this._changingAll = false;
        this._cancellable = new Gio.Cancellable();
        const button = this._button = new PanelMenu.Button(0, 'Water Ripple');
        button.add_child(new St.Icon({
            gicon: new Gio.FileIcon({file: extension.dir.get_child('ripple-symbolic.svg')}),
            style_class: 'system-status-icon',
        }));
        const {menu} = button;
        menu.addAction('Preferences', () => this._controllerAction('preferences'));
        menu.addAction('Open', () => this._controllerAction('open'));
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._effect = new PopupMenu.PopupSwitchMenuItem('Enable effect', false);
        this._effect.connect('toggled', (_item, enabled) => {
            if (!this._syncing) this._setRipple(enabled);
        });
        menu.addMenuItem(this._effect);
        this._all = new PopupMenu.PopupSwitchMenuItem('Enable all', false);
        this._all.connect('toggled', (_item, enabled) => {
            if (!this._syncing) this._setAll(enabled);
        });
        menu.addMenuItem(this._all);
        this._stopItem = menu.addAction('Stop', () => this._setRipple(false));
        this._status = new PopupMenu.PopupMenuItem('', {reactive: false});
        menu.addMenuItem(this._status);
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Quit', () => {
            this._setRipple(false);
            this._button.hide();
            this._controllerAction('quit');
        });
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Mini Utilities', () => Gio.AppInfo.launch_default_for_uri(
            'https://umbertogiacobbi.biz/mini-utilities/', global.create_app_launch_context(0, -1)));
        menu.addAction('About', () => this._controllerAction('about'));
        this._managerSignal = Main.extensionManager.connect('extension-state-changed', (_manager, changed) => {
            if (PEERS.some(([id]) => changed?.uuid === id))
                this._peerStateChanged(changed);
        });
        button.menu.connect('open-state-changed', (_menu, open) => {
            if (open) {
                this._discoverPeers();
                this.update();
            }
        });
        this._discoverPeers();
        this.update();
        Main.panel.addToStatusArea(extension.uuid, button);
        this._busOwner = Gio.bus_own_name_on_connection(Gio.DBus.session, PANEL_BUS_NAME,
            Gio.BusNameOwnerFlags.NONE, null, null);
        let hadController = false;
        this._controllerWatch = Gio.bus_watch_name_on_connection(Gio.DBus.session, CONTROLLER_ID,
            Gio.BusNameWatcherFlags.NONE, () => {
                hadController = true;
                button.show();
            }, () => {
                if (hadController) button.hide();
            });
    }

    _controllerAction(name) {
        const action = new GLib.Variant('(sava{sv})', [name, [], {}]);
        const cancellable = this._cancellable;
        Gio.DBus.session.call(CONTROLLER_ID, CONTROLLER_PATH, 'org.freedesktop.Application', 'ActivateAction',
            action, null, Gio.DBusCallFlags.NO_AUTO_START, 2000, cancellable, (bus, result) => {
                try {
                    bus.call_finish(result);
                } catch (error) {
                    if (cancellable?.is_cancelled()) return;
                    if (!error.matches(Gio.DBusError, Gio.DBusError.NAME_HAS_NO_OWNER) &&
                        !error.matches(Gio.DBusError, Gio.DBusError.SERVICE_UNKNOWN)) {
                        console.warn('Ripple controller action failed:', error.message);
                        return;
                    }
                    if (name === 'stop' || name === 'quit') return;
                    try {
                        const script = this._extension.dir.get_child('control.js').get_path();
                        Gio.Subprocess.new(['gjs', '-m', script, '--' + name], Gio.SubprocessFlags.NONE);
                    } catch (launchError) {
                        Main.notify('Could not open Ripple', launchError.message);
                    }
                }
            });
    }

    _discoverPeers() {
        for (const [id, schema] of PEERS) {
            if (this._peers.has(id)) continue;
            const peer = Main.extensionManager.lookup(id);
            if (!peer) continue;
            try {
                const schemas = Gio.SettingsSchemaSource.get_default();
                const local = peer.dir.get_child('schemas');
                const source = local.get_child('gschemas.compiled').query_exists(null)
                    ? Gio.SettingsSchemaSource.new_from_directory(local.get_path(), schemas, false)
                    : schemas;
                const definition = source.lookup(schema, true);
                if (!definition) continue;
                const settings = Gio.Settings.new_full(definition, null, null);
                const signal = settings.connect('changed::active', () => this.update());
                this._peers.set(id, {settings, signal, extension: peer});
            } catch (error) {
                console.warn('Could not connect Ripple to ' + id, error.message);
            }
        }
    }

    _setRipple(enabled) {
        if (enabled && this._canEnable()) {
            if (!this._settings.get_boolean('active')) this._settings.set_boolean('active', true);
        } else if (!enabled) {
            this._stop();
            this._controllerAction('stop');
        }
        this.update();
    }

    _setAll(enabled) {
        if (enabled && !this._canEnable()) {
            this.update();
            return;
        }
        this._changingAll = true;
        try {
            this._cancelPending();
            this._discoverPeers();
            this._setRipple(enabled);
            for (const [id, peer] of this._peers) {
                if (!enabled || peer.extension.state === ExtensionState.ACTIVE) {
                    if (peer.settings.get_boolean('active') !== enabled)
                        peer.settings.set_boolean('active', enabled);
                } else {
                    this._pending.add(id);
                    if (!Main.extensionManager.enableExtension(id))
                        this._peerEnableFailed(id);
                }
            }
            if (this._pending.size) {
                this._enableTimeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 5, () => {
                    this._enableTimeout = 0;
                    for (const id of [...this._pending]) this._peerEnableFailed(id);
                    this.update();
                    return GLib.SOURCE_REMOVE;
                });
            }
        } finally {
            this._changingAll = false;
        }
        this.update();
    }

    _peerStateChanged(extension) {
        this._discoverPeers();
        const {uuid, state} = extension;
        const peer = this._peers.get(uuid);
        if (peer) {
            peer.extension = extension;
            if (this._pending.has(uuid)) {
                if (state === ExtensionState.ACTIVE) {
                    this._pending.delete(uuid);
                    peer.settings.set_boolean('active', true);
                } else if ([ExtensionState.ERROR, ExtensionState.OUT_OF_DATE].includes(state)) {
                    this._peerEnableFailed(uuid);
                }
            }
        }
        if (!this._pending.size) this._cancelPending();
        this.update();
    }

    _peerEnableFailed(id) {
        this._pending.delete(id);
        Main.notify('Could not enable all effects', this._peers.get(id)?.extension.metadata.name ?? id);
    }

    _cancelPending() {
        this._pending.clear();
        if (this._enableTimeout) GLib.source_remove(this._enableTimeout);
        this._enableTimeout = 0;
    }

    _canEnable() {
        return this._leaseValid() && this._settings.is_writable('active');
    }

    updateLease() {
        if (this._ready !== this._canEnable()) this.update();
    }

    update() {
        // GSettings may notify before the other effects have received the
        // same command. Draw the switches once the batch is complete.
        if (this._changingAll) return;
        const active = this._settings.get_boolean('active');
        const ready = this._ready = this._canEnable();
        const allActive = active && [...this._peers.values()].every(peer =>
            peer.extension.state === ExtensionState.ACTIVE && peer.settings.get_boolean('active'));
        // GNOME also emits toggled when a switch changes programmatically.
        this._syncing = true;
        try {
            this._effect.setToggleState(active);
            this._all.setToggleState(allActive || this._pending.size > 0);
        } finally {
            this._syncing = false;
        }
        this._effect.setSensitive(ready || active);
        this._all.setSensitive(ready || allActive || this._pending.size > 0);
        this._stopItem.setSensitive(active || this._settings.get_boolean('recovery-pending'));
        const status = this._settings.get_string('status');
        if (this._status.label.text !== status) this._status.label.text = status;
    }

    destroy() {
        this._cancelPending();
        this._cancellable.cancel();
        Main.extensionManager.disconnect(this._managerSignal);
        for (const {settings, signal} of this._peers.values()) settings.disconnect(signal);
        this._peers.clear();
        Gio.bus_unwatch_name(this._controllerWatch);
        Gio.bus_unown_name(this._busOwner);
        this._button.destroy();
    }
}
