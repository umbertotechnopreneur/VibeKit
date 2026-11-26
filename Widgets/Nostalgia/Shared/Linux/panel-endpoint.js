// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';

// The shell owns this name only while the product has a working panel menu.
export function registerPanel(directory, applicationId) {
    const name = Gio.bus_own_name_on_connection(Gio.DBus.session, applicationId + '.Panel',
        Gio.BusNameOwnerFlags.NONE, null, null);
    const controller = directory.get_child('control.js').get_path();
    return {
        open(action) {
            Gio.Subprocess.new(['gjs', '-m', controller, '--' + action], Gio.SubprocessFlags.NONE);
        },
        dispose() { Gio.bus_unown_name(name); },
    };
}
