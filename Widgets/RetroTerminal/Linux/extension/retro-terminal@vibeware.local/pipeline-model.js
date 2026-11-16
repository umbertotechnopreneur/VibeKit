// SPDX-License-Identifier: MIT
import {BUILTINS, DEFAULTS, readLibrary as readLegacyLooks, validateValues} from './settings-model.js';

export const LIBRARY_LIMIT = 64;
export const LIBRARY_BYTES = 512 * 1024;
export class PipelineConflictError extends Error {
    constructor(message) {
        super(message);
        this.name = 'PipelineConflictError';
        this.code = 'PIPELINE_CONFLICT';
    }
}
const KEYS = Object.keys(DEFAULTS);
const immutable = item => Object.freeze({...item, values: Object.freeze({...item.values})});
const sameValues = (left, right) => KEYS.every(key => typeof left[key] === 'number'
    ? Math.abs(left[key] - right[key]) < 0.000001 : left[key] === right[key]);
const foldedName = name => name.trim().toLocaleLowerCase('en-US');

export function parsePipelines(text) {
    if (typeof text !== 'string' || new TextEncoder().encode(text).length > LIBRARY_BYTES)
        throw Error('Pipeline library exceeds 512 KiB.');
    const document = JSON.parse(text);
    if (!document || document.app !== 'retro-terminal' || document.version !== 1 ||
        !Array.isArray(document.pipelines) || document.pipelines.length > LIBRARY_LIMIT ||
        (document.selectedId != null && typeof document.selectedId !== 'string'))
        throw Error('Choose a Retro Terminal pipeline library (version 1).');
    const ids = new Set();
    const names = new Set();
    const pipelines = document.pipelines.map(entry => {
        if (!entry || typeof entry.id !== 'string' || !entry.id.trim() || entry.id.length > 80 ||
            entry.id.startsWith('builtin:') || ids.has(entry.id) || typeof entry.name !== 'string' ||
            !entry.name.trim() || entry.name.trim().length > 80 || names.has(foldedName(entry.name)))
            throw Error('Pipeline names and identifiers must be unique and valid.');
        ids.add(entry.id);
        names.add(foldedName(entry.name));
        return immutable({id: entry.id, name: entry.name.trim(), values: validateValues(entry.values)});
    });
    return {pipelines, selectedId: document.selectedId ?? null};
}

// Configuration, storage and the quiet-period scheduler are injected so all
// mutation and recovery paths can be verified without touching a desktop.
export class PipelineModel {
    constructor(adapter, storage, scheduler, legacy = '[]') {
        this.adapter = adapter;
        this.storage = storage;
        this.scheduler = scheduler;
        this.error = '';
        this.writable = true;
        this._listeners = new Set();
        this._applying = false;
        this._disposed = false;
        this._pending = null;
        this._source = null;
        this._selectedId = null;
        this._contents = null;
        this._builtins = BUILTINS.map((entry, index) => immutable({...entry, id: `builtin:${index}`}));
        this.items = [...this._builtins];
        try {
            this._contents = storage.read();
            if (this._contents !== null) {
                const document = parsePipelines(this._contents);
                this.items.push(...document.pipelines);
                const selected = this.items.find(item => item.id === document.selectedId);
                if (selected && sameValues(selected.values, this._capture())) this._selectedId = selected.id;
            } else {
                // Import old saved looks only once. The legacy key is deliberately
                // untouched, and opening preferences never applies a saved look.
                const looks = readLegacyLooks(legacy);
                if (looks.length) {
                    for (const look of looks) this.items.push(immutable({
                        id: storage.id(), name: this._uniqueName(look.name), values: look.values,
                    }));
                    this._save();
                }
            }
        } catch (error) {
            this.items = [...this._builtins];
            this._selectedId = null;
            this.writable = false;
            this.error = `Could not load pipelines. Existing data was preserved: ${error.message}`;
        }
    }

    get selected() { return this.items.find(item => item.id === this._selectedId) ?? null; }
    get currentName() { return this.selected?.name ?? 'Custom'; }
    builtIn(item) { return this._builtins.includes(item); }
    summary(item) {
        const values = item.values;
        const color = values['custom-color'] ? values['phosphor-color'] : ['Green phosphor', 'Amber terminal', 'Arcade color'][values.preset];
        return [color, values.scanlines > 0 ? 'Scanlines' : null, values.glow > 0 ? 'Glow' : null,
            values['persistence-enabled'] ? 'Afterglow' : null,
            values['glitches-enabled'] ? 'Random glitches' : null].filter(Boolean).join(' · ');
    }
    subscribe(listener) {
        this._listeners.add(listener);
        return () => this._listeners.delete(listener);
    }
    _emit() { for (const listener of this._listeners) listener(); }
    _capture() { return validateValues(this.adapter.read()); }
    _apply(values) {
        this._applying = true;
        try { this.adapter.apply(values); }
        finally { this._applying = false; }
    }
    _snapshot() { return {items: this.items, selectedId: this._selectedId, values: this._capture()}; }
    _restore(snapshot) {
        this.items = snapshot.items;
        this._selectedId = snapshot.selectedId;
        this._apply(snapshot.values);
    }
    _rollback(snapshot, error) {
        this.error = error.message;
        if (error.code === 'PIPELINE_CONFLICT') {
            // Settings are shared across preference windows. Another writer may
            // already have saved these exact values; reverting them here would
            // notify that writer and undo its successful save.
            if (snapshot) this.items = snapshot.items;
            this._selectedId = null;
            this.writable = false;
            return;
        }
        if (!snapshot) return;
        try { this._restore(snapshot); }
        catch (restoreError) {
            this._selectedId = null;
            this.error += ` Some previous settings could not be restored: ${restoreError.message}`;
        }
    }
    _document(selection = true) {
        const document = {app: 'retro-terminal', version: 1,
            pipelines: this.items.filter(item => !this.builtIn(item))};
        if (selection) document.selectedId = this._selectedId;
        return JSON.stringify(document, null, 2);
    }
    _save() {
        if (!this.writable) throw Error('Repair or move the unreadable pipeline library before saving.');
        const contents = this._document();
        if (new TextEncoder().encode(contents).length > LIBRARY_BYTES) throw Error('Pipeline library exceeds 512 KiB.');
        this.storage.write(contents, this._contents);
        this._contents = contents;
    }
    _transaction(action) {
        if (this._disposed || !this.flush()) return false;
        if (!this.writable) {
            this._emit();
            return false;
        }
        let previous = null;
        try {
            previous = this._snapshot();
            action();
            this._save();
            this.error = '';
        } catch (error) {
            this._rollback(previous, error);
            this._emit();
            return false;
        }
        this._emit();
        return true;
    }
    // File chooser actions use this wrapper to report IO/parse failures in the UI.
    run(action) {
        if (this._disposed || !this.flush() || !this.writable) return false;
        try { this.error = ''; action(); }
        catch (error) { this.error = error.message; }
        this._emit();
        return !this.error;
    }
    _requireItem(item, editable = false) {
        if (!this.items.includes(item)) throw Error('This pipeline is no longer available.');
        if (editable && this.builtIn(item)) throw Error('Duplicate a built-in pipeline before editing it.');
    }
    _capacity(count = 1) {
        if (this.items.length - this._builtins.length + count > LIBRARY_LIMIT)
            throw Error(`A library can contain up to ${LIBRARY_LIMIT} personal pipelines.`);
    }
    _name(name, exceptId = null) {
        if (typeof name !== 'string' || !name.trim() || name.trim().length > 80 ||
            this.items.some(item => item.id !== exceptId && foldedName(item.name) === foldedName(name)))
            throw Error('Choose a unique pipeline name of 1–80 characters.');
        return name.trim();
    }
    _uniqueName(name) {
        const stem = name.trim().slice(0, 70);
        let result = stem;
        let suffix = 2;
        while (this.items.some(item => foldedName(item.name) === foldedName(result))) result = `${stem} ${suffix++}`;
        return result;
    }
    create(name) {
        return this._transaction(() => {
            this._capacity();
            const item = immutable({id: this.storage.id(), name: this._name(name), values: this._capture()});
            this.items = [...this.items, item];
            this._selectedId = item.id;
        });
    }
    duplicate(item) {
        return this._transaction(() => {
            this._requireItem(item);
            this._capacity();
            const copy = immutable({id: this.storage.id(), name: this._uniqueName(`${item.name} copy`), values: item.values});
            this.items = [...this.items, copy];
            this._apply(copy.values);
            this._selectedId = copy.id;
        });
    }
    rename(item, name) {
        return this._transaction(() => {
            this._requireItem(item, true);
            const renamed = immutable({...item, name: this._name(name, item.id)});
            this.items = this.items.map(entry => entry === item ? renamed : entry);
        });
    }
    delete(item) {
        return this._transaction(() => {
            this._requireItem(item, true);
            this.items = this.items.filter(entry => entry !== item);
            if (this._selectedId === item.id) this._selectedId = null;
        });
    }
    select(item) {
        return this._transaction(() => {
            this._requireItem(item);
            this._apply(item.values);
            this._selectedId = item.id;
        });
    }
    detach() { return this._transaction(() => { this._selectedId = null; }); }

    changed(key) {
        if (this._disposed || !this.writable || this._applying || !Object.hasOwn(DEFAULTS, key) || !this.selected) return;
        const selected = this.selected;
        let values;
        try { values = this._capture(); }
        catch (error) { this.error = error.message; this._emit(); return; }
        if (sameValues(selected.values, values)) return;
        if (!this._pending) this._pending = {items: this.items, selectedId: this._selectedId, values: {...selected.values}};
        if (this.builtIn(selected)) this._selectedId = null;
        else this.items = this.items.map(item => item === selected ? immutable({...item, values}) : item);
        this.error = '';
        this._cancelSave();
        if (this.scheduler) this._source = this.scheduler.schedule(() => { this._source = null; this.flush(); });
        else this.flush();
        this._emit();
    }
    _cancelSave() {
        if (this._source !== null) this.scheduler.cancel(this._source);
        this._source = null;
    }
    flush() {
        this._cancelSave();
        if (!this._pending) return true;
        const previous = this._pending;
        this._pending = null;
        try { this._save(); this.error = ''; }
        catch (error) {
            this._rollback(previous, error);
            this._emit();
            return false;
        }
        this._emit();
        return true;
    }
    export() {
        if (!this.flush()) throw Error(this.error);
        if (!this.writable) throw Error(this.error || 'Reopen the pipeline library before exporting.');
        return this._document(false);
    }
    import(text) {
        return this._transaction(() => {
            const document = parsePipelines(text);
            this._capacity(document.pipelines.length);
            for (const entry of document.pipelines) this.items = [...this.items, immutable({
                id: this.storage.id(), name: this._uniqueName(entry.name), values: entry.values,
            })];
        });
    }
    dispose() {
        if (this._disposed) return;
        this.flush();
        this._disposed = true;
        this._listeners.clear();
    }
}
