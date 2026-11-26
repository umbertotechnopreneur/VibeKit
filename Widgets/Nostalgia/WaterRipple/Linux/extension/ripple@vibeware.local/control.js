// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk?version=4.0';
import {closeChoice} from './shared/ui/close-choice.js';
import {buildControls} from './settings-ui.js';
import {loadSchema} from './schema.js';
import {CONTROLLER_ID, PANEL_BUS_NAME} from './panel-ipc.js';
import {processMemory, watchMemory} from './ram-guard.js';

const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
const schema = loadSchema(import.meta.url);
const smokeTest = ARGV.includes('--smoke-test');
const app = new Adw.Application({
    application_id: CONTROLLER_ID,
    flags: Gio.ApplicationFlags.HANDLES_COMMAND_LINE |
        (smokeTest ? Gio.ApplicationFlags.NON_UNIQUE : Gio.ApplicationFlags.DEFAULT_FLAGS),
});

let window = null;
let panelAvailable = false;
let panelWatch = 0;
let controls = null;
let choice = null;
let exiting = false;
let leaseProcess = null;
let startupHidden = false;
let startupCancelled = false;
let memoryTimer = 0;

function showPanel() {
    startupHidden = false;
    window.unminimize();
    window.present();
}


function hidePanel() {
    if (panelAvailable)
        window.hide();
}

function exitRipple() {
    exiting = true;
    leaseProcess?.force_exit();
    window?.close();
    app.quit();
}

function activate(startMinimized, enableOnStartup = true) {
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

    const settings = Gio.Settings.new_full(schema, null, null);
    choice = closeChoice(window, 'Ripple', hidePanel, exitRipple, () =>
        settings.get_boolean('panic-enabled') && settings.get_boolean('panic-available')
            ? '\n\nIn an emergency, press Super + Shift + Esc to stop the effect.'
            : '\n\nThe emergency shortcut is disabled or unavailable. Check its status in the panel.');
    window.connect('close-request', () => exiting ? false : choice.request());

    const owner = app.get_dbus_connection()?.get_unique_name();
    if (owner && !smokeTest) {
        try {
            const helper = directory.get_child('lease.js').get_path();
            leaseProcess = Gio.Subprocess.new(['gjs', '-m', helper, owner], Gio.SubprocessFlags.NONE);
        } catch (error) {
            console.warn('Ripple lease helper failed to start', error.message);
        }
    }
    controls = buildControls(window, settings, app, Boolean(leaseProcess), startMinimized, Gio.DBus.session,
        enableOnStartup && !startupCancelled);
    const {page, footer} = controls;
    const toolbar = new Adw.ToolbarView({content: page});
    toolbar.add_top_bar(header);
    toolbar.add_bottom_bar(footer);
    window.set_content(toolbar);
    if (!smokeTest) {
        const updatePanel = available => {
            const wasAvailable = panelAvailable;
            panelAvailable = available;
            minimize.set_sensitive(available);
            choice.setAvailable(available);
            if (wasAvailable && !available && !exiting && !startupHidden && !window.visible)
                showPanel();
        };
        panelWatch = Gio.bus_watch_name_on_connection(Gio.DBus.session, PANEL_BUS_NAME, Gio.BusNameWatcherFlags.NONE,
            () => updatePanel(true), () => updatePanel(false));
    }
    if (!startMinimized) {
        showPanel();
    }
    if (smokeTest)
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
            exitRipple();
            return GLib.SOURCE_REMOVE;
        });
}

function openAction(action) {
    activate(false, false);
    if (action === 'preferences') controls.settingsSection.expanded = true;
    else if (action === 'about') controls.about.show();
}

app.connect('activate', () => activate(false));
app.connect('startup', () => {
    for (const name of ['open', 'preferences', 'about', 'stop', 'quit']) {
        const action = new Gio.SimpleAction({name});
        action.connect('activate', () => {
            if (name === 'stop') {
                startupCancelled = true;
                controls?.cancelStartup();
            }
            else if (name === 'quit') exitRipple();
            else openAction(name);
        });
        app.add_action(action);
    }
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
            exitRipple();
        }
    });
});
app.connect('command-line', (_app, commandLine) => {
    const args = commandLine.get_arguments();
    if (!smokeTest && args.includes('--autostart')) activate(true);
    else if (args.includes('--open')) openAction('open');
    else if (args.includes('--preferences')) openAction('preferences');
    else if (args.includes('--about')) openAction('about');
    else app.activate();
    return 0;
});
app.connect('shutdown', () => {
    if (memoryTimer) GLib.source_remove(memoryTimer);
    leaseProcess?.force_exit();
    if (panelWatch) Gio.bus_unwatch_name(panelWatch);
});
app.run(['ripple', ...ARGV.filter(argument => argument !== '--smoke-test')]);
