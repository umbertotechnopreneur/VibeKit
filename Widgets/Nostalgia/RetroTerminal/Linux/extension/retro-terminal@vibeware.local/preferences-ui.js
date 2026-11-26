// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {buildControls} from './settings-ui.js';

// Advance this when preferences require changes to the Shell renderer.
const MINIMUM_RENDERER_VERSION = 4;

export function buildPreferences(window, settings, quit = () => window.close()) {
    const ui = buildControls(window, settings, quit);
    const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
    const [, contents] = directory.get_child('metadata.json').load_contents(null);
    const installed = JSON.parse(new TextDecoder().decode(contents));
    const cancellable = new Gio.Cancellable();
    window.connect('close-request', () => { cancellable.cancel(); return false; });
    window.connect('unrealize', () => cancellable.cancel());
    // Preferences run in their own process, but Shell keeps imported renderer
    // modules for the session. Do not offer the old, failing afterglow path
    // as if reopening this updated panel had also updated the renderer.
    Gio.DBus.session.call('org.gnome.Shell.Extensions', '/org/gnome/Shell/Extensions',
        'org.gnome.Shell.Extensions', 'GetExtensionInfo', new GLib.Variant('(s)', [installed.uuid]),
        new GLib.VariantType('(a{sv})'), Gio.DBusCallFlags.NONE, 3000, cancellable,
        (connection, result) => {
            if (cancellable.is_cancelled()) return;
            try {
                const [info] = connection.call_finish(result).deep_unpack();
                const running = info.version?.deep_unpack();
                if (Number.isFinite(running) && running < MINIMUM_RENDERER_VERSION) {
                    ui.status.title = 'New CRT renderer installed';
                    ui.status.subtitle = 'Update installed. Sign out and back in to load the new CRT renderer. Your session is still using an earlier version.';
                    ui.active.sensitive = false;
                }
            } catch (error) {
                // A temporary bus error does not change effect state or
                // prevent an already-current installation from being used.
                console.debug('Retro Terminal runtime version check:', error.message);
            }
        });
    return ui;
}
