// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gdk from 'gi://Gdk?version=4.0';
import GdkPixbuf from 'gi://GdkPixbuf';
import Gio from 'gi://Gio';
import Gtk from 'gi://Gtk?version=4.0';

const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
const website = 'https://umbertogiacobbi.biz';

// Kept local so the extension archive also contains its complete About UI.
export function createAbout(parent) {
    let dialog = null;
    let provider = null;
    let display = null;
    let disposed = false;
    const releaseStyle = () => {
        if (provider) Gtk.StyleContext.remove_provider_for_display(display, provider);
        provider = display = null;
    };
    const show = () => {
        if (disposed) return null;
        if (dialog) { dialog.present(); return dialog; }
        const [, contents] = directory.get_child('metadata.json').load_contents(null);
        const metadata = JSON.parse(new TextDecoder().decode(contents));
        dialog = new Adw.Window({title: `About ${metadata.name}`, transient_for: parent,
            modal: true, destroy_with_parent: true, default_width: 640, default_height: 680,
            icon_name: 'weather-clear-night-symbolic'});
        dialog.add_css_class('vignette-about');
        const body = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 20,
            margin_top: 24, margin_bottom: 24, margin_start: 24, margin_end: 24});
        body.add_css_class('vignette-about-body');
        const label = (text, css = null) => {
            const child = new Gtk.Label({label: text, wrap: true, justify: Gtk.Justification.CENTER,
                halign: Gtk.Align.CENTER, max_width_chars: 58});
            if (css) child.add_css_class(css);
            body.append(child);
        };
        const badge = new Gtk.LinkButton({uri: `${website}/vibeware/manifesto`,
            halign: Gtk.Align.CENTER, tooltip_text: 'VibeWare manifesto'});
        badge.update_property([Gtk.AccessibleProperty.LABEL], ['VibeWare manifesto']);
        const pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(directory.get_child('VibeWare.png').get_path(), 80, 80, true);
        badge.set_child(new Gtk.Image({paintable: Gdk.Texture.new_for_pixbuf(pixbuf), pixel_size: 80}));
        body.append(badge);
        label(metadata.name, 'about-heading');
        label(`VibeWare · Version ${metadata['version-name']}`, 'about-note');
        label('A film and CRT-inspired vignette for your desktop.');
        label('I have a family and little time for gaming. So I make playful desktop effects to give my GPU something to do.');
        label("I'm Umberto Giacobbi.");
        label('Independent software, provided as-is without warranty.', 'about-note');
        const links = new Gtk.FlowBox({selection_mode: Gtk.SelectionMode.NONE, halign: Gtk.Align.CENTER,
            min_children_per_line: 1, max_children_per_line: 3, column_spacing: 8, row_spacing: 4});
        for (const [name, uri] of [
            ['UmbertoGiacobbi.biz', website],
            ['Source', 'https://github.com/umbertotechnopreneur/VibeKit'],
            ['MIT license', 'https://opensource.org/license/mit'],
            ['Terms', `${website}/terms/`], ['Privacy', `${website}/privacy/`],
        ]) links.append(new Gtk.LinkButton({label: name, uri}));
        body.append(links);
        const close = new Gtk.Button({label: 'Close', halign: Gtk.Align.CENTER});
        close.connect('clicked', () => dialog?.close());
        body.append(close);
        dialog.set_content(new Gtk.ScrolledWindow({hscrollbar_policy: Gtk.PolicyType.NEVER,
            vscrollbar_policy: Gtk.PolicyType.AUTOMATIC, child: body}));
        provider = new Gtk.CssProvider();
        provider.load_from_string(`
            .vignette-about-body label { font-family: monospace; font-size: 15px; }
            .vignette-about-body .about-heading { font-size: 23px; font-weight: bold; }
            .vignette-about-body .about-note { opacity: .75; font-size: 13px; }
            .vignette-about-body button.link label { font-size: 13px; }
        `);
        display = dialog.get_display();
        Gtk.StyleContext.add_provider_for_display(display, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
        dialog.connect('close-request', () => { releaseStyle(); dialog = null; return false; });
        const keys = new Gtk.EventControllerKey();
        keys.connect('key-pressed', (_controller, key) => {
            if (key !== Gdk.KEY_Escape) return false;
            dialog?.close();
            return true;
        });
        dialog.add_controller(keys);
        dialog.present();
        return dialog;
    };
    return {show, dispose() {
        if (disposed) return;
        disposed = true;
        releaseStyle();
        dialog?.destroy();
        dialog = null;
    }};
}
