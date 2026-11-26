// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gdk from 'gi://Gdk?version=4.0';
import Gtk from 'gi://Gtk?version=4.0';
import {createMonitorGroup} from './shared/monitor-settings.js';
import {addThemeSetting, createAccordion} from './shared/ui/preferences-layout.js';
import {createAbout} from './shared/ui/about.js';
import {PARAMETERS} from './gradient.js';
import {extensionDirectory, loadPipelines, newPipeline, readAppearance, savePipelines, writeAppearance} from './pipelines.js';

const directory = extensionDirectory(import.meta.url);

export function buildPreferences(window, settings, quit = () => window.close()) {
    window.set_default_size(680, 680);
    const page = new Adw.PreferencesPage({title: 'Vignette'});
    window.add(page);
    const power = new Adw.PreferencesGroup({title: 'Vignette'});
    page.add(power);
    const active = new Adw.SwitchRow({title:'Vignette effect', subtitle:'Shade or brighten the outer edges of selected displays.'});
    const status = new Adw.ActionRow({title:'Status', use_markup:false});
    const stop = new Gtk.Button({label:'Stop', valign:Gtk.Align.CENTER, css_classes:['destructive-action']});
    stop.connect('clicked', () => settings.set_boolean('active', false));
    status.add_suffix(stop);
    power.add(active);
    power.add(status);
    const section = createAccordion(page);
    const effect = section('Effect');
    const library = section('Pipelines');
    const preferences = section('Settings');
    const diagnostics = section('Diagnostics');
    const about = createAbout(window, import.meta.url, 'local.vibeware.Vignette', 'MIT');
    const connections = [];
    const changed = (key, callback) => connections.push(settings.connect('changed::'+key,callback));

    let syncing = false;
    let model = loadPipelines(directory, settings);
    let entries = [];
    let editId = null;
    const lookGroup = new Adw.PreferencesGroup();
    const look = new Adw.ComboRow({title:'Pipeline'});
    const summary = new Adw.ActionRow({title:'Current look', use_markup:false});
    const name = new Adw.EntryRow({title:'Personal pipeline name', max_length:80});
    const buttons = new Gtk.Box({spacing:8, margin_top:8, margin_bottom:6});
    const button = (label, callback) => {
        const control = new Gtk.Button({label});
        control.connect('clicked', callback);
        return control;
    };
    const notice = new Gtk.Label({label:'', wrap:true, xalign:0, visible:false});
    const notify = text => { notice.label=text; notice.visible=Boolean(text); };
    const report = action => {
        try { action(); notify(''); } catch (error) { notify(error.message); }
    };
    const selected = () => entries[look.selected - 1];
    const edited = () => model.personal.find(item => item.id === editId);
    const saveAs = button('Save as', () => report(() => {
        const pipeline = newPipeline(name.text, readAppearance(settings));
        editId = pipeline.id;
        savePipelines(settings, [...model.personal,pipeline]);
        name.text = '';
    }));
    const update = button('Update', () => report(() => {
        const current = edited();
        if (!current) return;
        const values = newPipeline(name.text || current.name, readAppearance(settings));
        savePipelines(settings, model.personal.map(item => item.id === current.id ? {...values,id:item.id} : item));
    }));
    const remove = button('Remove', () => {
        const current = edited();
        if (!current) return;
        const dialog = new Adw.AlertDialog({heading:'Remove personal pipeline?',body:current.name});
        dialog.add_response('cancel','Cancel');
        dialog.add_response('remove','Remove');
        dialog.set_response_appearance('remove',Adw.ResponseAppearance.DESTRUCTIVE);
        dialog.set_close_response('cancel');
        dialog.connect('response', (_dialog, response) => {
            if (response === 'remove') report(() => savePipelines(settings, model.personal.filter(item => item.id !== current.id)));
        });
        dialog.present(window);
    });
    for (const control of [saveAs, update, remove]) buttons.append(control);
    for (const row of [look, summary, name]) lookGroup.add(row);
    library.add(lookGroup);
    library.add(buttons);
    library.add(notice);

    const adjustmentGroup = new Adw.PreferencesGroup();
    const colorHelp = 'Dark colors shade the edges. Light colors lift them.';
    const colorRow = new Adw.ActionRow({title:'Edge color', subtitle:colorHelp});
    const hexColor = new Gtk.Entry({width_chars:7, max_length:7, valign:Gtk.Align.CENTER, placeholder_text:'#RRGGBB'});
    hexColor.set_tooltip_text('Edge color in #RRGGBB format');
    const color = new Gtk.ColorDialogButton({dialog:new Gtk.ColorDialog({with_alpha:false}),valign:Gtk.Align.CENTER});
    colorRow.add_suffix(hexColor);
    colorRow.add_suffix(color);
    adjustmentGroup.add(colorRow);
    const adjustments = new Map();
    for (const [key,title,min,max,step] of PARAMETERS) {
        const adjustment = new Gtk.Adjustment({lower:min, upper:max, step_increment:step, page_increment:.1});
        const row = new Adw.SpinRow({title, adjustment, digits:2});
        adjustments.set(key,row);
        row.connect('notify::value', () => {
            if (!syncing) report(() => writeAppearance(settings,{...readAppearance(settings), [key]:row.value}));
        });
        adjustmentGroup.add(row);
    }
    color.connect('notify::rgba', () => {
        if (syncing) return;
        const rgba = color.rgba;
        const hex = [rgba.red,rgba.green,rgba.blue].map(c => Math.round(c*255).toString(16).padStart(2,'0')).join('');
        report(() => writeAppearance(settings,{...readAppearance(settings), color:'#'+hex}));
    });
    const commitColor = () => {
        try {
            if (!writeAppearance(settings, {...readAppearance(settings), color:hexColor.text.trim()}))
                throw Error('The color setting is read-only.');
            colorRow.subtitle = colorHelp;
            refresh();
        } catch (error) {
            colorRow.subtitle = error.message;
            hexColor.text = readAppearance(settings).color;
        }
    };
    hexColor.connect('activate', commitColor);
    const colorFocus = new Gtk.EventControllerFocus();
    colorFocus.connect('leave', commitColor);
    hexColor.add_controller(colorFocus);
    effect.add(adjustmentGroup);
    preferences.add(createMonitorGroup(settings));
    addThemeSetting(window, settings, preferences);
    const safety = new Adw.PreferencesGroup();
    safety.add(new Adw.ActionRow({title:'Emergency stop', subtitle:'Super + Shift + F10'}));
    safety.add(new Adw.ActionRow({title:'Rendering', subtitle:'One reusable gradient texture. No screen capture.'}));
    preferences.add(safety);
    const diagnosticGroup = new Adw.PreferencesGroup({title: 'Desktop overlay'});
    const diagnosticStatus = new Adw.ActionRow({title: 'Effect status', use_markup: false});
    const diagnosticShortcut = new Adw.ActionRow({title: 'Emergency shortcut', use_markup: false});
    diagnosticGroup.add(diagnosticStatus);
    diagnosticGroup.add(diagnosticShortcut);
    diagnostics.add(diagnosticGroup);
    const refreshDiagnostics = () => {
        diagnosticStatus.subtitle = settings.get_string('status');
        diagnosticShortcut.subtitle = settings.get_boolean('panic-available')
            ? 'Super + Shift + F10 stops the effect.'
            : 'Unavailable. The effect cannot be started until the shortcut is available.';
    };
    changed('status', refreshDiagnostics);
    changed('panic-available', refreshDiagnostics);
    refreshDiagnostics();
    const footer = new Gtk.Box({spacing:8, halign:Gtk.Align.CENTER});
    footer.append(button('About', () => about.show()));
    footer.append(new Gtk.LinkButton({label:'Mini Utilities',uri:'https://umbertogiacobbi.biz/mini-utilities/'}));
    section('About').add(footer);

    const matches = (entry, values) => entry.color === values.color && PARAMETERS.every(([key]) => entry[key] === values[key]);
    const refresh = () => {
        const values = readAppearance(settings);
        syncing = true;
        try {
            const rgba = new Gdk.RGBA();
            rgba.parse(values.color);
            color.rgba = rgba;
            hexColor.text = values.color;
            for (const [key,row] of adjustments) row.value = values[key];
            const preferred = entries.findIndex(entry => entry.id === editId && matches(entry, values));
            const index = preferred >= 0 ? preferred : entries.findIndex(entry => matches(entry, values));
            look.selected = index + 1;
            const item = selected();
            summary.subtitle = item?.description ?? (item?.builtin ? item.name : 'Your current tint and falloff settings');
            saveAs.sensitive = !model.error;
            update.sensitive = remove.sensitive = Boolean(edited() && !model.error);
        } finally { syncing=false; }
    };
    const refreshLibrary = () => {
        model=loadPipelines(directory,settings);
        entries=[...model.builtins,...model.personal];
        syncing=true;
        try { look.model=Gtk.StringList.new(['Custom',...entries.map(item=>item.name)]); }
        finally { syncing=false; }
        refresh();
        if (model.error) notify(model.error.message);
    };
    look.connect('notify::selected', () => {
        const item = selected();
        if (syncing || !item) return;
        editId = item.id;
        name.text = item.builtin ? '' : item.name;
        report(() => writeAppearance(settings, item));
        refresh();
    });
    settings.bind('active',active,'active',0);
    const refreshStatus=()=>{ status.subtitle=settings.get_string('status'); };
    changed('status',refreshStatus);
    changed('appearance',refresh);
    changed('pipeline-library',refreshLibrary);
    changed('quit-request',quit);
    settings.get_int64('quit-request');
    window.connect('unrealize',()=>{
        for (const connection of connections) settings.disconnect(connection);
        about.dispose();
    });
    refreshStatus();
    refreshLibrary();
    effect.row.expanded=true;
    return {about, showPreferences() { preferences.row.expanded = true; }, active, diagnosticStatus, diagnosticShortcut, hexColor, colorRow};
}
