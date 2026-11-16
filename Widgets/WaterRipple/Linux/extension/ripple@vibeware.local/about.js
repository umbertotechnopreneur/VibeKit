// SPDX-License-Identifier: GPL-3.0-or-later
import Gdk from 'gi://Gdk?version=4.0';
import GdkPixbuf from 'gi://GdkPixbuf';
import Gio from 'gi://Gio';
import Gtk from 'gi://Gtk?version=4.0';

const directory = Gio.File.new_for_uri(import.meta.url).get_parent();

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

export function createAbout(window) {
    let about = null;
    let provider = null;
    let display = null;
    let disposed = false;
    const clearProvider = () => {
        if (provider) Gtk.StyleContext.remove_provider_for_display(display, provider);
        provider = display = null;
    };
    function show() {
        if (about) {
            about.present();
            return about;
        }

        const [, metadataContents] = directory.get_child('metadata.json').load_contents(null);
        const metadata = JSON.parse(new TextDecoder().decode(metadataContents));
        const dialog = new Gtk.Window({
            application: window.get_application(), title: 'About Ripple', transient_for: window, destroy_with_parent: true,
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
        content.append(aboutText('With a family and little time for gaming, I found another use for my GPU: playful desktop effects.'));
        content.append(aboutText('VibeWare · desktop toys by Umberto Giacobbi', 'about-note'));
        content.append(aboutText('I provide this software independently and as-is, without warranty.', 'about-note'));
        const author = new Gtk.Box({spacing: 4, halign: Gtk.Align.CENTER});
        author.append(aboutText("I'm Umberto Giacobbi ·"));
        author.append(aboutLink('GPL-3.0-or-later', 'https://www.gnu.org/licenses/gpl-3.0.html', new Gtk.Label({label: 'GPL-3.0-or-later'})));
        content.append(author);

        const website = 'https://umbertogiacobbi.biz';
        const badgeImage = GdkPixbuf.Pixbuf.new_from_file_at_scale(directory.get_child('VibeWare.png').get_path(), 64, 64, true);
        const badge = new Gtk.Image({paintable: Gdk.Texture.new_for_pixbuf(badgeImage), pixel_size: 64});
        content.append(aboutLink('VibeWare manifesto', `${website}/vibeware/manifesto`, badge));

        const links = new Gtk.FlowBox({
            selection_mode: Gtk.SelectionMode.NONE, min_children_per_line: 1, max_children_per_line: 4,
            column_spacing: 8, row_spacing: 4, halign: Gtk.Align.CENTER,
        });
        for (const [label, uri, icon] of [
            ['UmbertoGiacobbi.biz', website, 'web-browser-symbolic'],
            ['Source', 'https://github.com/umbertotechnopreneur/VibeKit', 'folder-remote-symbolic'],
            ['Terms', `${website}/terms/`, 'text-x-generic-symbolic'],
            ['Privacy', `${website}/privacy/`, 'changes-prevent-symbolic'],
        ]) {
            const pair = new Gtk.Box({spacing: 6});
            pair.append(new Gtk.Image({icon_name: icon}));
            pair.append(new Gtk.Label({label}));
            const link = aboutLink(label, uri, pair);
            links.append(link);
        }
        content.append(links);
        const scroll = new Gtk.ScrolledWindow({
            hscrollbar_policy: Gtk.PolicyType.NEVER, vscrollbar_policy: Gtk.PolicyType.AUTOMATIC,
            child: content,
        });
        dialog.set_child(scroll);

        provider = new Gtk.CssProvider();
        provider.load_from_string(aboutStyles);
        display = dialog.get_display();
        Gtk.StyleContext.add_provider_for_display(display, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
        dialog.connect('close-request', () => {
            clearProvider();
            about = null;
            return false;
        });
        dialog.connect('unrealize', () => {
            clearProvider();
            about = null;
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
        return dialog;
    }
    return {
        show() { if (!disposed) return show(); },
        dispose() {
            if (disposed) return;
            disposed = true;
            clearProvider();
            about?.destroy();
            about = null;
        },
    };
}
