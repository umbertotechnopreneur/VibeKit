// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk?version=4.0';
import {buildControls} from './settings-ui.js';
import {loadSchema} from './schema.js';
import {TrayIndicator} from './tray.js';

const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
const schema = loadSchema(import.meta.url);
const smokeTest = ARGV.includes('--smoke-test');
const app = new Adw.Application({
    application_id: 'local.vibeware.Ripple',
    flags: smokeTest ? Gio.ApplicationFlags.NON_UNIQUE : Gio.ApplicationFlags.DEFAULT_FLAGS,
});

let window = null;
let tray = null;
let closeDialog = null;
let exiting = false;
let leaseProcess = null;

function showPanel() {
    window.unminimize();
    window.present();
}

function hidePanel() {
    if (tray?.available)
        window.hide();
}

function exitRipple() {
    exiting = true;
    leaseProcess?.force_exit();
    window.close();
}

app.connect('activate', () => {
    if (window) {
        showPanel();
        return;
    }

    window = new Adw.ApplicationWindow({application: app});
    const header = new Adw.HeaderBar({decoration_layout: ':close'});
    const minimize = new Gtk.Button({
        icon_name: 'window-minimize-symbolic',
        tooltip_text: 'Minimize to the top bar',
        sensitive: false,
    });
    minimize.connect('clicked', hidePanel);
    header.pack_end(minimize);

    window.connect('close-request', () => {
        if (exiting)
            return false;
        if (closeDialog)
            return true;
        closeDialog = new Adw.AlertDialog({
            heading: 'Hide the panel or quit Ripple?',
            body: tray?.available
                ? 'Hide the panel to keep the effect running in the top bar. Quitting stops the effect.'
                : 'The top bar icon is unavailable. Quitting stops the effect.',
        });
        closeDialog.add_response('cancel', 'Cancel');
        closeDialog.add_response('hide', 'Hide to tray');
        closeDialog.add_response('quit', 'Quit');
        closeDialog.set_close_response('cancel');
        closeDialog.set_default_response(tray?.available ? 'hide' : 'cancel');
        closeDialog.set_response_enabled('hide', Boolean(tray?.available));
        closeDialog.set_response_appearance('quit', Adw.ResponseAppearance.DESTRUCTIVE);
        closeDialog.connect('response', (_dialog, response) => {
            closeDialog = null;
            if (response === 'hide')
                hidePanel();
            else if (response === 'quit')
                exitRipple();
        });
        closeDialog.present(window);
        return true;
    });

    const settings = Gio.Settings.new_full(schema, null, null);
    const owner = app.get_dbus_connection()?.get_unique_name();
    if (owner && !smokeTest) {
        try {
            const helper = directory.get_child('lease.js').get_path();
            leaseProcess = Gio.Subprocess.new(['gjs', '-m', helper, owner], Gio.SubprocessFlags.NONE);
        } catch (error) {
            console.warn('Ripple lease helper failed to start', error.message);
        }
    }
    const {page} = buildControls(window, settings, app, Boolean(leaseProcess));
    const toolbar = new Adw.ToolbarView({content: page});
    toolbar.add_top_bar(header);
    window.set_content(toolbar);
    if (!smokeTest) {
        tray = new TrayIndicator(showPanel, exitRipple, available => {
            minimize.set_sensitive(available);
            closeDialog?.set_response_enabled('hide', available);
            if (!available && !window.visible)
                showPanel();
        });
    }
    showPanel();
    if (smokeTest)
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
            exitRipple();
            return GLib.SOURCE_REMOVE;
        });
});
app.connect('shutdown', () => {
    leaseProcess?.force_exit();
    tray?.destroy();
});
app.run([]);
