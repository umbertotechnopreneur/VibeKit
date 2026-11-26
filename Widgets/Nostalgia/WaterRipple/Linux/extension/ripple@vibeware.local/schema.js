// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';

export const SCHEMA = 'org.gnome.shell.extensions.vibeware-ripple';

export function loadSchema(moduleUrl) {
    const directory = Gio.File.new_for_uri(moduleUrl).get_parent();
    const source = Gio.SettingsSchemaSource.new_from_directory(
        directory.get_child('schemas').get_path(), Gio.SettingsSchemaSource.get_default(), false);
    return source.lookup(SCHEMA, true);
}
