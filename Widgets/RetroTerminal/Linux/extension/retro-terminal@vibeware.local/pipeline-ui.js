// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import {readLibrary} from './pipelines.js';

// A native library view. The model owns validation, persistence and selection.
export function pipelineControls(window, model, path, showEffect) {
    const page = new Adw.PreferencesPage({title: 'Pipelines', icon_name: 'view-list-symbolic'});
    const heading = new Adw.PreferencesGroup({
        title: 'CRT pipelines',
        description: 'Choose a complete look. Duplicate a built-in to make it yours; personal pipelines save your adjustments automatically.',
    });
    page.add(heading);
    const makeButton = (label, callback) => {
        const widget = new Gtk.Button({label});
        widget.connect('clicked', callback);
        return widget;
    };
    let disposed = false;
    let renameId = null;
    const name = new Gtk.Entry({placeholder_text: 'Pipeline name', max_length: 80, hexpand: true});
    const editor = new Gtk.Box({spacing: 8, visible: false, margin_top: 8});
    const save = makeButton('Save', () => {
        if (renameId) {
            const item = model.items.find(candidate => candidate.id === renameId);
            if (!item) { editor.visible = false; return; }
            model.rename(item, name.text);
        } else model.create(name.text);
        if (!model.error) editor.visible = false;
    });
    save.add_css_class('suggested-action');
    const cancel = makeButton('Cancel', () => editor.visible = false);
    editor.append(name); editor.append(save); editor.append(cancel);
    const edit = item => {
        renameId = item?.id ?? null;
        name.text = item?.name ?? '';
        editor.visible = true;
        name.grab_focus();
    };
    const transfers = new Set();
    const transfer = exporting => {
        const chooser = new Gtk.FileChooserNative({
            title: exporting ? 'Export CRT pipelines' : 'Import CRT pipelines',
            transient_for: window, modal: true,
            action: exporting ? Gtk.FileChooserAction.SAVE : Gtk.FileChooserAction.OPEN,
            accept_label: exporting ? 'Export' : 'Import', cancel_label: 'Cancel',
        });
        const filter = new Gtk.FileFilter({name: 'CRT pipeline library (*.json)'});
        filter.add_pattern('*.json');
        chooser.add_filter(filter);
        if (exporting) chooser.set_current_name('retro-terminal-pipelines.json');
        transfers.add(chooser);
        chooser.connect('response', (_dialog, response) => {
            if (!disposed && response === Gtk.ResponseType.ACCEPT) {
                model.run(() => {
                    const file = chooser.get_file();
                    if (!file) return;
                    if (exporting && file.equal(Gio.File.new_for_path(path)))
                        throw Error('Choose a different file; the pipeline library saves itself automatically.');
                    if (exporting) file.replace_contents(model.export(), null, false, Gio.FileCreateFlags.PRIVATE, null);
                    else model.import(readLibrary(file));
                });
            }
            transfers.delete(chooser);
            chooser.destroy();
        });
        chooser.show();
    };
    const create = makeButton('New pipeline', () => edit(null));
    create.add_css_class('suggested-action');
    const importButton = makeButton('Import…', () => transfer(false));
    const exportButton = makeButton('Export…', () => transfer(true));
    const toolbar = new Gtk.Box({spacing: 8});
    toolbar.append(create); toolbar.append(importButton); toolbar.append(exportButton);
    heading.add(toolbar); heading.add(editor);
    const list = new Adw.PreferencesGroup();
    page.add(list);
    const feedback = new Adw.PreferencesGroup();
    const error = new Adw.ActionRow({title_lines: 0, use_markup: false, visible: false});
    feedback.add(error); page.add(feedback);
    const location = new Adw.PreferencesGroup({description: `Library: ${path}`});
    page.add(location);
    let rows = [];
    const actions = new Map();
    const refresh = () => {
        for (const row of rows) list.remove(row);
        rows = [];
        actions.clear();
        error.title = model.error || '';
        error.visible = Boolean(model.error);
        create.sensitive = importButton.sensitive = exportButton.sensitive = save.sensitive = model.writable !== false;
        for (const item of model.items) {
            const row = new Adw.ActionRow({title: item.name, subtitle: model.summary(item),
                use_markup: false, activatable: true});
            row.add_prefix(new Gtk.Image({icon_name: item === model.selected ? 'object-select-symbolic' :
                model.builtIn(item) ? 'starred-symbolic' : 'document-edit-symbolic'}));
            row.connect('activated', () => {
                model.select(item);
                if (!model.error) showEffect();
            });
            const menu = new Gtk.MenuButton({icon_name: 'view-more-symbolic', valign: Gtk.Align.CENTER,
                tooltip_text: `Actions for ${item.name}`, sensitive: model.writable !== false});
            const contents = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 4,
                margin_top: 6, margin_bottom: 6, margin_start: 6, margin_end: 6});
            const popover = new Gtk.Popover({child: contents});
            menu.set_popover(popover);
            const commands = {};
            const action = (key, label, callback) => {
                const button = makeButton(label, () => { popover.popdown(); callback(); });
                button.add_css_class('flat');
                contents.append(button);
                commands[key] = button;
            };
            action('duplicate', 'Duplicate', () => model.duplicate(item));
            if (!model.builtIn(item)) {
                action('rename', 'Rename', () => edit(item));
                action('delete', 'Delete', () => model.delete(item));
            }
            row.add_suffix(menu);
            row.add_suffix(new Gtk.Image({icon_name: 'go-next-symbolic'}));
            list.add(row); rows.push(row);
            actions.set(item.id, commands);
        }
    };
    const unsubscribe = model.subscribe(refresh);
    refresh();
    return {page, create, name, save, cancel, editor, error, actions, importButton, exportButton,
        dispose() {
            if (disposed) return;
            disposed = true;
            unsubscribe();
            for (const chooser of transfers) chooser.destroy();
            transfers.clear();
        }};
}
