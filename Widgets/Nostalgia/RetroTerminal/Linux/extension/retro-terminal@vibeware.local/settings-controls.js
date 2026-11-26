// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gio from 'gi://Gio';
import {NUMERIC} from './settings-model.js';

// Native control construction stays separate from preferences lifecycle and
// pipeline coordination. The caller owns the external settings signal IDs.
export function createControlBuilders(page, settings) {
    const controls = {};
    const expanders = {};
    const add = (parent, row) => parent instanceof Adw.ExpanderRow ? parent.add_row(row) : parent.add(row);
    const group = (title = '', description = '') => {
        const result = new Adw.PreferencesGroup({title, description});
        page.add(result);
        return result;
    };
    const section = (key, title, subtitle, icon) => {
        const row = new Adw.ExpanderRow({title, subtitle, expanded: false, use_markup: false});
        row.add_prefix(new Gtk.Image({icon_name: icon}));
        group().add(row);
        expanders[key] = row;
        return row;
    };
    const switchRow = (parent, key, title, subtitle = '') => {
        const row = new Adw.SwitchRow({title, subtitle});
        settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
        add(parent, row);
        controls[key] = row;
        return row;
    };
    const spinRow = (parent, key, enabled = null) => {
        const {title, min, max, step} = NUMERIC.find(spec => spec.key === key);
        const row = new Adw.SpinRow({title, digits: step < 0.01 ? 3 : step < 1 ? 2 : 0,
            adjustment: new Gtk.Adjustment({lower: min, upper: max, step_increment: step, page_increment: step * 5})});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        if (enabled) settings.bind(enabled, row, 'sensitive', Gio.SettingsBindFlags.GET);
        add(parent, row);
        controls[key] = row;
    };
    return {controls, expanders, group, section, switchRow, spinRow};
}

export function createPhosphorColor(parent, settings) {
    const color = new Gtk.ColorDialogButton({dialog: new Gtk.ColorDialog({title: 'Phosphor color', with_alpha: false}), valign: Gtk.Align.CENTER});
    const colorRow = new Adw.ActionRow({title: 'Phosphor color', activatable_widget: color});
    colorRow.add_suffix(color); parent.add_row(colorRow);
    settings.bind('custom-color', color, 'sensitive', Gio.SettingsBindFlags.GET);
    let syncingColor = false;
    const updateColor = () => {
        syncingColor = true;
        const rgba = new Gdk.RGBA();
        if (!rgba.parse(settings.get_string('phosphor-color'))) rgba.parse('#33ff6b');
        color.rgba = rgba;
        syncingColor = false;
    };
    color.connect('notify::rgba', () => {
        if (syncingColor) return;
        const {red, green, blue} = color.rgba;
        const toHex = channel => {
            const byte = Math.round(Math.max(0, Math.min(1, channel)) * 255);
            return byte.toString(16).padStart(2, '0');
        };
        const hex = [red, green, blue].map(toHex).join('');
        settings.set_string('phosphor-color', `#${hex}`);
    });
    const signal = settings.connect('changed::phosphor-color', updateColor);
    updateColor();
    return {color, signal};
}
