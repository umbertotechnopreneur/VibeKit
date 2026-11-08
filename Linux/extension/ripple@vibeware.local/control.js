// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gdk from 'gi://Gdk?version=4.0';
import GdkPixbuf from 'gi://GdkPixbuf';
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
    flags: Gio.ApplicationFlags.HANDLES_COMMAND_LINE |
        (smokeTest ? Gio.ApplicationFlags.NON_UNIQUE : Gio.ApplicationFlags.DEFAULT_FLAGS),
});

let window = null;
let tray = null;
let about = null;
let closeDialog = null;
let exiting = false;
let leaseProcess = null;

function showPanel() {
    window.unminimize();
    window.present();
}

const aboutStyles = `
window.widget-about, .widget-about .about-body { background-color: #fff4dc; color: #232a2f; }
.widget-about .about-body { font-family: monospace; font-size: 15px; }
.widget-about .about-body label { color: #232a2f; }
.widget-about .about-body .about-heading { font-size: 23px; font-weight: bold; }
.widget-about .about-body .about-note { color: #526573; font-size: 13px; }
.widget-about .about-body .about-link, .widget-about .about-body .about-link label { color: #3a6a96; }
.widget-about .about-body .about-link { padding: 4px; font-size: 13px; }
.widget-about .about-body .about-link:hover { background-color: alpha(#3a6a96, .10); }
`;

function aboutText(label, style) {
    const text = new Gtk.Label({
        label, wrap: true, justify: Gtk.Justification.CENTER, max_width_chars: 60, halign: Gtk.Align.CENTER,
    });
    if (style)
        text.add_css_class(style);
    return text;
}

function aboutLink(label, uri, child) {
    const link = new Gtk.LinkButton({uri, halign: Gtk.Align.CENTER, tooltip_text: label});
    link.set_child(child);
    link.add_css_class('about-link');
    link.update_property([Gtk.AccessibleProperty.LABEL], [label]);
    return link;
}

function showAbout() {
    showPanel();
    if (about) {
        about.present();
        return;
    }

    const [, metadataContents] = directory.get_child('metadata.json').load_contents(null);
    const metadata = JSON.parse(new TextDecoder().decode(metadataContents));
    const dialog = new Gtk.Window({
        application: app, title: 'About Ripple', transient_for: window, destroy_with_parent: true,
        modal: true, default_width: 640, default_height: 620, icon_name: 'local.vibeware.Ripple',
    });
    dialog.add_css_class('widget-about');
    const content = new Gtk.Box({
        orientation: Gtk.Orientation.VERTICAL, spacing: 24, halign: Gtk.Align.CENTER,
        margin_top: 24, margin_bottom: 24, margin_start: 24, margin_end: 24,
    });
    content.add_css_class('about-body');
    content.append(new Gtk.Image({icon_name: 'local.vibeware.Ripple', pixel_size: 100}));
    const identity = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 5});
    identity.append(aboutText('Ripple', 'about-heading'));
    identity.append(aboutText(`Version ${metadata['version-name']}`, 'about-note'));
    content.append(identity);
    content.append(aboutText('I built Ripple to add click-driven water ripples to my GNOME desktop.'));
    content.append(aboutText('I provide this software independently and as-is, without warranty.', 'about-note'));
    const author = new Gtk.Box({spacing: 4, halign: Gtk.Align.CENTER});
    author.append(aboutText("I'm Umberto Giacobbi ·"));
    author.append(aboutLink('GPL-3.0-or-later', 'https://www.gnu.org/licenses/gpl-3.0.html', new Gtk.Label({label: 'GPL-3.0-or-later'})));
    content.append(author);

    const website = 'https://umbertogiacobbi.biz';
    const badgeImage = GdkPixbuf.Pixbuf.new_from_file_at_scale(directory.get_child('VibeWare.png').get_path(), 64, 64, true);
    const badge = new Gtk.Image({paintable: Gdk.Texture.new_for_pixbuf(badgeImage)});
    content.append(aboutLink('VibeWare manifesto', `${website}/vibeware/manifesto`, badge));

    const links = new Gtk.FlowBox({
        selection_mode: Gtk.SelectionMode.NONE, min_children_per_line: 1, max_children_per_line: 4,
        column_spacing: 8, row_spacing: 4, halign: Gtk.Align.CENTER,
    });
    for (const [label, uri, icon] of [
        ['UmbertoGiacobbi.biz', website, 'web-browser-symbolic'],
        ['Private source', 'https://github.com/umbertotechnopreneur/Tools/tree/main/Widgets/WaterRipple/Linux', 'folder-remote-symbolic'],
        ['Terms', `${website}/terms/`, 'text-x-generic-symbolic'],
        ['Privacy', `${website}/privacy/`, 'changes-prevent-symbolic'],
    ]) {
        const pair = new Gtk.Box({spacing: 6});
        pair.append(new Gtk.Image({icon_name: icon}));
        pair.append(new Gtk.Label({label}));
        const link = aboutLink(label, uri, pair);
        if (label === 'Private source')
            link.set_tooltip_text('Private GitHub repository. Access is required.');
        links.append(link);
    }
    content.append(links);
    const scroll = new Gtk.ScrolledWindow({
        hscrollbar_policy: Gtk.PolicyType.NEVER, vscrollbar_policy: Gtk.PolicyType.AUTOMATIC,
        child: content,
    });
    dialog.set_child(scroll);

    const provider = new Gtk.CssProvider();
    provider.load_from_string(aboutStyles);
    const display = dialog.get_display();
    Gtk.StyleContext.add_provider_for_display(display, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
    dialog.connect('close-request', () => {
        Gtk.StyleContext.remove_provider_for_display(display, provider);
        about = null;
        return false;
    });
    const keys = new Gtk.EventControllerKey();
    keys.connect('key-pressed', (_controller, key) => {
        if (key !== Gdk.KEY_Escape)
            return false;
        dialog.close();
        return true;
    });
    dialog.add_controller(keys);
    about = dialog;
    dialog.present();
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
    const {page, active} = buildControls(window, settings, app, Boolean(leaseProcess), startMinimized);
    const toolbar = new Adw.ToolbarView({content: page});
    toolbar.add_top_bar(header);
    window.set_content(toolbar);
    let panelReady = false;
    if (!smokeTest) {
        tray = new TrayIndicator({
            show: showPanel,
            toggleEffect: () => {
                if (active.sensitive)
                    active.active = !active.active;
            },
            about: showAbout,
            quit: exitRipple,
        }, available => {
            minimize.set_sensitive(available);
            closeDialog?.set_response_enabled('hide', available);
            if (panelReady && !available && !window.visible)
                showPanel();
        });
        const updateTrayEffect = () => tray.setEffect(active.active, active.sensitive);
        active.connect('notify::active', updateTrayEffect);
        active.connect('notify::sensitive', updateTrayEffect);
        updateTrayEffect();
    }
    if (startMinimized) {
        window.minimize();
        window.set_visible(true);
    } else {
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
app.connect('command-line', (_app, commandLine) => {
    if (!smokeTest && commandLine.get_arguments().includes('--autostart'))
        activate(true);
    else
        app.activate();
    return 0;
});
app.connect('shutdown', () => {
    leaseProcess?.force_exit();
    tray?.destroy();
});
app.run(['ripple', ...ARGV.filter(argument => argument !== '--smoke-test')]);
