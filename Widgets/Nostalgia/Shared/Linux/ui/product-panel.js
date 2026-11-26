// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {productMetadata} from './metadata.js';
import {closeChoice} from './close-choice.js';

// The desktop launcher and the tray share one application; GNOME's preferences host stays independent.
export function runProductPanel(moduleUrl, applicationId, buildPreferences) {
    const {directory, metadata} = productMetadata(moduleUrl);
    const schemas = Gio.SettingsSchemaSource.new_from_directory(directory.get_child('schemas').get_path(),
        Gio.SettingsSchemaSource.get_default(), false);
    const schema = schemas.lookup(metadata['settings-schema'], false);
    if (!schema) throw new Error(`Missing compiled schema for ${metadata.uuid}`);
    const settings = Gio.Settings.new_full(schema, null, null);
    const app = new Adw.Application({application_id: applicationId, flags: Gio.ApplicationFlags.HANDLES_COMMAND_LINE});
    let window, controls, choice;
    let watch = 0;
    let quitting = false;
    let disabling = false;
    const finish = () => {
        quitting = true;
        window?.close();
        app.quit();
    };
    const quit = () => {
        if (disabling) return;
        disabling = true;
        settings.set_boolean('active', false);
        Gio.Settings.sync();
        window.set_sensitive(false);
        Gio.DBus.session.call('org.gnome.Shell.Extensions', '/org/gnome/Shell/Extensions',
            'org.gnome.Shell.Extensions', 'DisableExtension', new GLib.Variant('(s)', [metadata.uuid]),
            new GLib.VariantType('(b)'), Gio.DBusCallFlags.NONE, 5000, null, (connection, result) => {
                try {
                    const [disabled] = connection.call_finish(result).deep_unpack();
                    if (!disabled) throw new Error('GNOME Shell declined to disable the extension.');
                    finish();
                } catch (error) {
                    if (quitting) return;
                    disabling = false;
                    window.set_sensitive(true);
                    window.present();
                    const alert = new Adw.AlertDialog({heading: 'Could not disable the extension', body: error.message});
                    alert.add_response('cancel', 'Keep panel open');
                    alert.add_response('close', 'Close panel');
                    alert.set_close_response('cancel');
                    alert.connect('response', (_dialog, response) => {
                        if (response === 'close') finish();
                    });
                    alert.present(window);
                }
            });
    };
    const show = action => {
        if (!window) {
            window = new Adw.PreferencesWindow({application: app, title: metadata.name,
                icon_name: applicationId, default_width: 680, default_height: 680});
            choice = closeChoice(window, metadata.name, () => window.hide(), quit);
            // Intercept Cancel/Hide before product-specific close handlers release their settings bindings.
            window.connect('close-request', () => quitting ? false : choice.request());
            controls = buildPreferences(window, settings, finish);
            watch = Gio.bus_watch_name_on_connection(Gio.DBus.session, applicationId + '.Panel',
                Gio.BusNameWatcherFlags.NONE, () => choice.setAvailable(true), () => {
                    choice.setAvailable(false);
                    if (!quitting && !disabling && !window.visible) window.present();
                });
        }
        window.present();
        if (action === '--about') controls.about.show();
        else if (action === '--preferences') controls.showPreferences();
    };
    app.connect('activate', () => show('--open'));
    app.connect('command-line', (_app, command) => {
        show(command.get_arguments()[1]);
        return 0;
    });
    app.connect('shutdown', () => {
        if (watch) Gio.bus_unwatch_name(watch);
    });
    return app.run(['panel', ...ARGV]);
}
