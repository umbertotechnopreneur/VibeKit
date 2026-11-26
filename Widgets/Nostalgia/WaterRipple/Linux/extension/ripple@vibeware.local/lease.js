// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {loadSchema} from './schema.js';

const [controller] = ARGV;
if (!controller?.startsWith(':'))
    throw new Error('Ripple requires the controller’s unique D-Bus name');

const settings = Gio.Settings.new_full(loadSchema(import.meta.url), null, null);
const loop = new GLib.MainLoop(null, false);
let heartbeat = 0;
function renewLease() {
    // Stay below the extension's eight-second upper bound.
    settings.set_double('lease-until', GLib.get_monotonic_time() / 1e6 + 7.5);
    return GLib.SOURCE_CONTINUE;
}

const watch = Gio.bus_watch_name_on_connection(Gio.DBus.session, controller,
    Gio.BusNameWatcherFlags.NONE, () => {
        renewLease();
        heartbeat = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, renewLease);
    }, () => loop.quit());
loop.run();
if (heartbeat)
    GLib.source_remove(heartbeat);
Gio.bus_unwatch_name(watch);
