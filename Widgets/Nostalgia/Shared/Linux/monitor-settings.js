// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

import {MONITOR_SELECTION_KEY, parseDisplayState, selectMonitors, updateMonitorSelection} from './monitor-selection.js';

const DISPLAY_CONFIG = 'org.gnome.Mutter.DisplayConfig';
const DISPLAY_CONFIG_PATH = '/org/gnome/Mutter/DisplayConfig';
const DISPLAY_STATE_TYPE = new GLib.VariantType('(ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv})');

export function createMonitorGroup(settings) {
    const group = new Adw.PreferencesGroup({title: 'Monitors'});
    let monitors = [];
    let rows = [];
    let syncing = false;
    let bus = null;
    let monitorSignal = 0;
    let selectionSignal = 0;
    let writableSignal = 0;
    let query = null;

    function syncRows() {
        const selected = new Set(selectMonitors(monitors, settings.get_strv(MONITOR_SELECTION_KEY)));
        const writable = settings.is_writable(MONITOR_SELECTION_KEY);
        syncing = true;
        try {
            for (const {monitor, row} of rows) {
                row.active = selected.has(monitor);
                row.sensitive = writable;
            }
        } finally {
            syncing = false;
        }
    }

    function updateRows(displays, message = '') {
        for (const {row} of rows)
            group.remove(row);
        monitors = displays;
        group.description = message;
        rows = monitors.map(monitor => {
            const duplicateName = monitors.some(other => other !== monitor && other.name === monitor.name);
            const title = duplicateName ? `${monitor.name} (${monitor.ids.join(', ')})` : monitor.name;
            const row = new Adw.SwitchRow({title, use_markup: false});
            row.connect('notify::active', () => {
                if (syncing)
                    return;
                const ids = updateMonitorSelection(monitors, settings.get_strv(MONITOR_SELECTION_KEY), monitor, row.active);
                if (!settings.set_strv(MONITOR_SELECTION_KEY, ids))
                    syncRows();
            });
            group.add(row);
            return {monitor, row};
        });
        syncRows();
    }

    function refresh() {
        if (!bus)
            return;
        query?.cancel();
        const request = new Gio.Cancellable();
        query = request;
        bus.call(DISPLAY_CONFIG, DISPLAY_CONFIG_PATH, DISPLAY_CONFIG, 'GetCurrentState', null,
            DISPLAY_STATE_TYPE, Gio.DBusCallFlags.NO_AUTO_START, 1500, request, (connection, result) => {
                try {
                    const state = connection.call_finish(result).recursiveUnpack();
                    if (query !== request)
                        return;
                    const displays = parseDisplayState(state);
                    updateRows(displays, displays.length === 0 ? 'No active monitors found.' : '');
                } catch {
                    if (query === request)
                        updateRows([], 'Monitor list unavailable.');
                } finally {
                    if (query === request)
                        query = null;
                }
            });
    }

    group.connect('map', () => {
        try {
            bus = Gio.DBus.session;
        } catch {
            updateRows([], 'Monitor list unavailable.');
            return;
        }
        monitorSignal = bus.signal_subscribe(DISPLAY_CONFIG, DISPLAY_CONFIG, 'MonitorsChanged',
            DISPLAY_CONFIG_PATH, null, Gio.DBusSignalFlags.NONE, refresh);
        selectionSignal = settings.connect(`changed::${MONITOR_SELECTION_KEY}`, syncRows);
        writableSignal = settings.connect(`writable-changed::${MONITOR_SELECTION_KEY}`, syncRows);
        syncRows();
        refresh();
    });
    group.connect('unmap', () => {
        query?.cancel();
        query = null;
        if (monitorSignal)
            bus.signal_unsubscribe(monitorSignal);
        if (selectionSignal)
            settings.disconnect(selectionSignal);
        if (writableSignal)
            settings.disconnect(writableSignal);
        monitorSignal = selectionSignal = writableSignal = 0;
        bus = null;
    });
    return group;
}
