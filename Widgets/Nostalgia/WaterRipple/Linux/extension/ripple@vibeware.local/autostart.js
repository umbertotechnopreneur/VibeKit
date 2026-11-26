// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const ENTRY = 'local.vibeware.Ripple.desktop';

// XDG autostart gives a user entry precedence over a system entry with the same name.
export class AutostartPreference {
    constructor(userConfig = GLib.get_user_config_dir(), systemConfigs = GLib.get_system_config_dirs()) {
        this.userFile = Gio.File.new_for_path(GLib.build_filenamev([userConfig, 'autostart', ENTRY]));
        this.systemFiles = systemConfigs.map(path => Gio.File.new_for_path(GLib.build_filenamev([path, 'autostart', ENTRY])));
    }

    _load(file) {
        if (!file.query_exists(null)) return null;
        const [, bytes] = file.load_contents(null);
        const key = new GLib.KeyFile();
        key.load_from_data(new TextDecoder().decode(bytes), bytes.length,
            GLib.KeyFileFlags.KEEP_COMMENTS | GLib.KeyFileFlags.KEEP_TRANSLATIONS);
        if (!key.has_group('Desktop Entry')) throw Error('The Ripple startup entry is invalid.');
        return key;
    }

    _system() {
        for (const file of this.systemFiles) {
            const key = this._load(file);
            if (key) return key;
        }
        return null;
    }

    read() {
        const key = this._load(this.userFile) ?? this._system();
        if (!key) return {available: false, enabled: false};
        const [keys] = key.get_keys('Desktop Entry');
        const disabled = (keys.includes('Hidden') && key.get_boolean('Desktop Entry', 'Hidden')) ||
            (keys.includes('X-GNOME-Autostart-enabled') && !key.get_boolean('Desktop Entry', 'X-GNOME-Autostart-enabled'));
        return {available: true, enabled: !disabled};
    }

    setEnabled(enabled) {
        const key = this._load(this.userFile) ?? this._system();
        if (!key) throw Error('Ripple’s startup entry is not installed.');
        // A common XDG override contains only Hidden=true. Fill its missing
        // launch fields from the system entry before making it visible.
        if (enabled && !key.get_keys('Desktop Entry')[0].includes('Exec')) {
            const system = this._system();
            if (!system) throw Error('Ripple’s system startup entry is missing.');
            const existing = new Set(key.get_keys('Desktop Entry')[0]);
            for (const name of system.get_keys('Desktop Entry')[0]) {
                if (!existing.has(name)) key.set_value('Desktop Entry', name, system.get_value('Desktop Entry', name));
            }
            if (!key.get_keys('Desktop Entry')[0].includes('Exec'))
                throw Error('Ripple’s system startup entry has no launch command.');
        }
        key.set_boolean('Desktop Entry', 'Hidden', !enabled);
        if (key.get_keys('Desktop Entry')[0].includes('X-GNOME-Autostart-enabled'))
            key.set_boolean('Desktop Entry', 'X-GNOME-Autostart-enabled', enabled);
        const parent = this.userFile.get_parent();
        if (GLib.mkdir_with_parents(parent.get_path(), 0o700) !== 0)
            throw Error('Could not create the startup preferences folder.');
        const [contents] = key.to_data();
        this.userFile.replace_contents(new TextEncoder().encode(contents), null, false,
            Gio.FileCreateFlags.REPLACE_DESTINATION | Gio.FileCreateFlags.PRIVATE, null);
    }
}
