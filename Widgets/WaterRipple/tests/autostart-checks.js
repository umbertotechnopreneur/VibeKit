// SPDX-License-Identifier: GPL-3.0-or-later
// Uses only an isolated temporary config tree.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
const {AutostartPreference} = await import(Gio.File.new_for_path(ARGV[0]).get_uri());
const root = GLib.dir_make_tmp('ripple-startup-checks-XXXXXX');
const user = GLib.build_filenamev([root, 'user']);
const system = GLib.build_filenamev([root, 'system']);
const file = GLib.build_filenamev([system, 'autostart', 'local.vibeware.Ripple.desktop']);
const check = (value, message) => { if (!value) throw Error(message); };
try {
    const preference = new AutostartPreference(user, [system]);
    check(!preference.read().available, 'Missing system entry must be unavailable');
    GLib.mkdir_with_parents(GLib.path_get_dirname(file), 0o700);
    GLib.file_set_contents(file, '[Desktop Entry]\nType=Application\nName=Ripple\nExec=gjs --autostart\nX-Custom=keep\n');
    check(preference.read().enabled, 'System entry should enable startup');
    preference.setEnabled(false);
    check(!preference.read().enabled, 'User override should disable startup');
    preference.setEnabled(true);
    check(preference.read().enabled, 'User override should restore startup');
    const [, bytes] = preference.userFile.load_contents(null);
    const text = new TextDecoder().decode(bytes);
    check(text.includes('Exec=gjs --autostart') && text.includes('X-Custom=keep'), 'Override must preserve existing fields');
    check(new TextDecoder().decode(GLib.file_get_contents(file)[1]).includes('X-Custom=keep'), 'System entry must remain unchanged');
    GLib.file_set_contents(preference.userFile.get_path(), '[Desktop Entry]\nHidden=true\nX-User=keep\n');
    preference.setEnabled(true);
    const [, minimalBytes] = preference.userFile.load_contents(null);
    const minimal = new TextDecoder().decode(minimalBytes);
    check(preference.read().enabled && minimal.includes('Exec=gjs --autostart') && minimal.includes('X-User=keep'),
        'Enabling a minimal override must restore the system command and preserve user fields');
    print('PASS XDG startup preference, preserved fields and isolated user override');
} finally {
    Gio.Subprocess.new(['rm', '-rf', root], Gio.SubprocessFlags.NONE).wait_check(null);
}
