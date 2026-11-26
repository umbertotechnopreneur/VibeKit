// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';

export function createAccordion(page) {
    const group = new Adw.PreferencesGroup();
    const rows = [];
    page.add(group);
    return title => {
        const row = new Adw.ExpanderRow({title});
        const body = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL, spacing: 16,
            margin_top: 12, margin_bottom: 12, margin_start: 12, margin_end: 12,
        });
        row.add_row(new Gtk.ListBoxRow({child: body, activatable: false, selectable: false}));
        group.add(row);
        rows.push(row);
        row.connect('notify::expanded', () => {
            if (row.expanded) {
                for (const other of rows)
                    if (other !== row) other.expanded = false;
            }
        });
        return {row, add: widget => body.append(widget)};
    };
}

export function addThemeSetting(window, settings, section, application = null) {
    const manager = application ? application.get_style_manager() : Adw.StyleManager.get_for_display(window.get_display());
    const originalScheme = manager.color_scheme;
    const values = ['system', 'light', 'dark'];
    const schemes = [Adw.ColorScheme.DEFAULT, Adw.ColorScheme.FORCE_LIGHT, Adw.ColorScheme.FORCE_DARK];
    const theme = new Adw.ComboRow({title: 'Theme', model: Gtk.StringList.new(['System', 'Light', 'Dark'])});
    const apply = () => {
        const index = Math.max(0, values.indexOf(settings.get_string('window-theme')));
        theme.selected = index;
        manager.color_scheme = schemes[index];
    };
    apply();
    theme.connect('notify::selected', () => {
        const value = values[theme.selected];
        if (value && value !== settings.get_string('window-theme'))
            settings.set_string('window-theme', value);
    });
    const changed = settings.connect('changed::window-theme', apply);
    const unrealize = window.connect('unrealize', () => {
        window.disconnect(unrealize);
        settings.disconnect(changed);
        manager.color_scheme = originalScheme;
    });
    const group = new Adw.PreferencesGroup();
    group.add(theme);
    section.add(group);
}
