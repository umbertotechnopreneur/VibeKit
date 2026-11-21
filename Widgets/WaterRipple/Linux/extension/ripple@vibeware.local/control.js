// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk?version=4.0';
import {buildControls} from './settings-ui.js';
import {loadSchema} from './schema.js';
import {TrayIndicator} from './tray.js';
import {processMemory, watchMemory} from './ram-guard.js';

const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
const schema = loadSchema(import.meta.url);
const smokeTest = ARGV.includes('--smoke-test');
const app = new Adw.Application({
    application_id: 'local.vibeware.Ripple',
    flags: Gio.ApplicationFlags.HANDLES_COMMAND_LINE |
        (smokeTest ? Gio.ApplicationFlags.NON_UNIQUE : Gio.ApplicationFlags.DEFAULT_FLAGS),
});

let window = null;
let tray = null;
let closeDialog = null;
let exiting = false;
let leaseProcess = null;
let startupHidden = false;
let memoryTimer = 0;

function showPanel() {
    startupHidden = false;
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

function activate(startMinimized) {
    if (window) {
        if (!startMinimized)
            showPanel();
        return;
    }

    startupHidden = startMinimized;
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
            prefer_wide_layout: true,
            body: (tray?.available
                ? 'Hide the panel to keep the effect running in the top bar. Quitting stops the effect.'
                : 'The top bar icon is unavailable. Quitting stops the effect.')
                + (settings.get_boolean('panic-enabled') && settings.get_boolean('panic-available')
                    ? '\n\nIn an emergency, press Super + Shift + Esc to stop the effect.'
                    : '\n\nThe emergency shortcut is disabled or unavailable. Check its status in the panel.'),
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
    const {page, active, about, settingsSection} = buildControls(window, settings, app, Boolean(leaseProcess), startMinimized);
    const toolbar = new Adw.ToolbarView({content: page});
    toolbar.add_top_bar(header);
    window.set_content(toolbar);
    let panelReady = false;
    if (!smokeTest) {
        tray = new TrayIndicator({
            show: showPanel,
            preferences: () => { showPanel(); settingsSection.expanded = true; },
            toggleEffect: () => {
                if (active.sensitive)
                    active.active = !active.active;
            },
            about: () => { showPanel(); about.show(); },
            quit: exitRipple,
        }, available => {
            minimize.set_sensitive(available);
            closeDialog?.set_response_enabled('hide', available);
            if (panelReady && !startupHidden && !available && !window.visible)
                showPanel();
        });
        const updateTrayEffect = () => tray.setEffect(active.active, active.sensitive);
        active.connect('notify::active', updateTrayEffect);
        active.connect('notify::sensitive', updateTrayEffect);
        updateTrayEffect();
    }
    if (!startMinimized) {
        showPanel();
    }
    panelReady = true;
    if (smokeTest)
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
            exitRipple();
            return GLib.SOURCE_REMOVE;
        });
}

app.connect('activate', () => activate(false));
app.connect('startup', () => {
    const limitBytes = 500_000_000;
    memoryTimer = watchMemory(() => {
        const root = processMemory();
        if (!root) return null;
        const childId = leaseProcess?.get_identifier();
        const child = childId && processMemory(childId);
        const processes = child?.parentPid === root.pid ? [root, child] : [root];
        const bytes = processes.reduce((sum, process) => sum + process.rssBytes, 0);
        return bytes > limitBytes ? {limitBytes, bytes, processes} : null;
    }, event => {
        memoryTimer = 0;
        console.warn(JSON.stringify({kind: 'guard.memory-limit', app: 'WaterRipple', ...event}));
        try {
            const notice = new Gio.Notification();
            notice.set_title('Water Ripple closed');
            notice.set_body('The controller exceeded its memory limit. See the system journal for details.');
            app.send_notification('memory-limit', notice);
        } finally {
            if (window) exitRipple();
            app.quit();
        }
    });
});
app.connect('command-line', (_app, commandLine) => {
    if (!smokeTest && commandLine.get_arguments().includes('--autostart'))
        activate(true);
    else
        app.activate();
    return 0;
});
app.connect('shutdown', () => {
    if (memoryTimer) GLib.source_remove(memoryTimer);
    leaseProcess?.force_exit();
    tray?.destroy();
});
app.run(['ripple', ...ARGV.filter(argument => argument !== '--smoke-test')]);
