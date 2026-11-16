// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import {readLibrary} from './pipelines.js';

export function pipelineControls(window, model, path, selectEffect) {
    const page = new Adw.PreferencesPage({title: 'Pipelines', icon_name: 'view-list-symbolic'});
    const group = new Adw.PreferencesGroup({description: 'Pipelines are saved sets of ripple and filter settings.'});
    page.add(group);
    const toolbar = new Gtk.Box({spacing: 8, margin_bottom: 12});
    const button = (label, action) => {
        const b = new Gtk.Button({label}); b.connect('clicked', action); return b;
    };
    let renaming = null;
    const name = new Gtk.Entry({placeholder_text: 'Pipeline name', max_length: 80, hexpand: true});
    const editor = new Gtk.Box({spacing: 8, visible: false, margin_bottom: 12});
    editor.append(name);
    editor.append(button('Save', () => {
        if (renaming) model.rename(renaming, name.text); else model.create(name.text);
        if (!model.error) editor.visible = false;
    }));
    editor.append(button('Cancel', () => { editor.visible = false; }));
    toolbar.append(button('+ New pipeline', () => { renaming = null; name.text = ''; editor.visible = true; name.grab_focus(); }));
    function transfer(save) {
        const dialog = new Gtk.FileChooserNative({
            title: save ? 'Export pipelines' : 'Import pipelines', transient_for: window,
            action: save ? Gtk.FileChooserAction.SAVE : Gtk.FileChooserAction.OPEN,
            accept_label: save ? 'Export' : 'Import', cancel_label: 'Cancel', modal: true,
        });
        const filter = new Gtk.FileFilter({name: 'Pipeline library (*.json)'}); filter.add_pattern('*.json'); dialog.add_filter(filter);
        if (save) dialog.set_current_name('ripple-pipelines.json');
        const onClose = window.connect('close-request', () => { dialog.destroy(); return false; });
        dialog.connect('response', (_dialog, response) => {
            if (response === Gtk.ResponseType.ACCEPT) {
                model.run(() => {
                    const file = dialog.get_file();
                    if (save) file.replace_contents(model.export(), null, false, Gio.FileCreateFlags.PRIVATE, null);
                    else model.import(readLibrary(file));
                });
            }
            window.disconnect(onClose); dialog.destroy();
        });
        dialog.show();
    }
    toolbar.append(button('Import…', () => transfer(false)));
    toolbar.append(button('Export…', () => transfer(true)));
    group.add(toolbar); group.add(editor);
    const list = new Adw.PreferencesGroup(); page.add(list);
    const error = new Adw.PreferencesGroup(); page.add(error);
    const footer = new Adw.PreferencesGroup({description: `Library: ${path}`}); page.add(footer);
    const rows = new Map();
    let order = [];
    const unsubscribe = model.subscribe(() => refresh());
    function refresh() {
        error.description = model.error;
        const nextOrder = model.items.map(pipeline => pipeline.id);
        const structureChanged = order.length !== nextOrder.length || order.some((id, index) => id !== nextOrder[index]);
        if (structureChanged) {
            for (const id of order) list.remove(rows.get(id).row);
            for (const id of order) if (!nextOrder.includes(id)) rows.delete(id);
        }
        for (const pipeline of model.items) {
            let record = rows.get(pipeline.id);
            if (!record) {
                record = createRow(pipeline);
                rows.set(pipeline.id, record);
            }
            const {row, menu} = record;
            row.title = `${pipeline === model.selected ? '✓ ' : ''}${pipeline.name}`;
            row.subtitle = model.summary(pipeline);
            row.activatable = !model.conflicted;
            menu.tooltip_text = `Actions for ${pipeline.name}`;
            menu.sensitive = !model.conflicted;
            if (structureChanged) list.add(row);
        }
        order = nextOrder;
    }
    function createRow(pipeline) {
        const row = new Adw.ActionRow({activatable: true, use_markup: false});
        row.connect('activated', () => { model.select(pipeline); selectEffect(); });
        const menu = new Gtk.MenuButton({icon_name: 'view-more-symbolic', valign: Gtk.Align.CENTER, tooltip_text: `Actions for ${pipeline.name}`});
        const actions = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 4, margin_top: 6, margin_bottom: 6, margin_start: 6, margin_end: 6});
        const popover = new Gtk.Popover({child: actions}); menu.set_popover(popover);
        actions.append(button('Duplicate', () => { popover.popdown(); model.duplicate(pipeline); }));
        if (!model.builtIn(pipeline)) {
            actions.append(button('Rename', () => { popover.popdown(); renaming = pipeline; name.text = pipeline.name; editor.visible = true; name.grab_focus(); }));
            actions.append(button('Delete', () => { popover.popdown(); model.delete(pipeline); }));
        }
        row.add_suffix(menu);
        row.add_suffix(new Gtk.Image({icon_name: 'go-next-symbolic'}));
        return {row, menu};
    }
    refresh();
    // Stable row identity preserves focus and open action menus during live edits.
    return {page, rowFor: id => rows.get(id)?.row, dispose: unsubscribe};
}
