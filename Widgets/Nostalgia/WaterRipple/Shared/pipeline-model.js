// SPDX-License-Identifier: GPL-3.0-or-later
// Platform-neutral state and portable document contract; storage/settings are injected.
export class PipelineConflictError extends Error {}

export class PipelineModel {
    constructor(catalog, adapter, storage, scheduler = null) {
        this.catalog = catalog;
        this.adapter = adapter;
        this.storage = storage;
        this.scheduler = scheduler;
        this.pending = null;
        this.saveTimer = null;
        this.contents = null;
        this.listeners = new Set();
        this.error = '';
        this.selected = null;
        this.applying = false;
        this.writable = true;
        this.conflicted = false;
        this.items = catalog.presets.map(p => ({...p, id: `builtin:${p.id}`, values: this.complete(p.values)}));
        try {
            const contents = storage.read();
            this.contents = contents;
            if (contents !== null) {
                const doc = this.parse(contents);
                this.items.push(...doc.pipelines);
                this.selected = this.items.find(p => p.id === doc.selectedId && this.matches(p)) ?? null;
            }
        } catch (e) {
            this.writable = false;
            this.error = `Pipeline library could not be loaded. The source file was preserved: ${e.message}`;
        }
    }
    get currentName() { return this.selected?.name ?? 'Custom'; }
    builtIn(p) { return p.id.startsWith('builtin:'); }
    complete(values) { return Object.fromEntries(this.catalog.parameters.map(p => [p.key, values[p.key] ?? p.default])); }
    summary(p) { return this.catalog.groups.filter(g => !g.enabled || p.values[g.enabled]).map(g => g.title).join(', '); }
    subscribe(listener) { this.listeners.add(listener); return () => this.listeners.delete(listener); }
    notify() { for (const listener of this.listeners) listener(); }
    checkpoint() {
        return {items: [...this.items], selected: this.selected, settings: this.capture(),
            entries: this.items.map(item => ({item, name: item.name, values: {...item.values}}))};
    }
    restore(checkpoint, restoreSettings = true) {
        this.applying = true;
        try {
            for (const {item, name, values} of checkpoint.entries) {
                item.name = name;
                item.values = values;
            }
            this.items = checkpoint.items;
            this.selected = checkpoint.selected;
            if (restoreSettings) {
                for (const parameter of this.catalog.parameters)
                    if (this.adapter.supports(parameter.key))
                        this.adapter.set(parameter.key, checkpoint.settings[parameter.key]);
            }
        } finally { this.applying = false; }
    }
    failed(error, checkpoint) {
        // A stale panel does not own GSettings: undoing its snapshot would also
        // undo the other panel's live values, which that panel could then save.
        const conflict = error instanceof PipelineConflictError;
        this.restore(checkpoint, !conflict);
        if (conflict) {
            this.conflicted = true;
            this.selected = null;
        }
        this.error = error.message;
    }
    run(action) {
        if (this.conflicted) return;
        if (!this.flush()) return;
        const checkpoint = this.checkpoint();
        try { this.error = ''; action(); }
        catch (error) { this.failed(error, checkpoint); }
        this.notify();
    }
    capture(previous = {}) {
        const values = this.complete(previous);
        for (const p of this.catalog.parameters)
            if (this.adapter.supports(p.key)) values[p.key] = this.adapter.get(p.key);
        return values;
    }
    matches(pipeline) {
        return this.catalog.parameters.filter(p => this.adapter.supports(p.key)).every(p => {
            const stored = pipeline.values[p.key];
            const actual = this.adapter.get(p.key);
            return typeof stored === 'boolean' ? actual === stored : Math.abs(actual - this.adapter.clamp(p.key, stored)) < .001;
        });
    }
    filterPresetIndex(group) {
        return (group.presets ?? []).findIndex(preset => Object.entries(preset.values).every(([key, value]) => Math.abs(this.adapter.get(key) - value) < .001)) + 1;
    }
    applyFilterPreset(preset) {
        this.run(() => {
            this.applying = true;
            try { for (const [key, value] of Object.entries(preset.values)) this.adapter.set(key, value); }
            finally { this.applying = false; }
            if (this.selected && !this.builtIn(this.selected)) this.selected.values = this.capture(this.selected.values);
            else if (this.selected && !this.matches(this.selected)) this.selected = null;
            this.save();
        });
    }
    select(pipeline) {
        this.run(() => this.apply(pipeline));
    }
    apply(pipeline) {
        if (!this.items.includes(pipeline)) throw Error('Pipeline is unavailable.');
        this.applying = true;
        try {
            for (const p of this.catalog.parameters)
                if (this.adapter.supports(p.key)) this.adapter.set(p.key, pipeline.values[p.key]);
            this.selected = pipeline;
            this.save();
        } finally { this.applying = false; }
    }
    detach() { this.run(() => { this.selected = null; this.save(); }); }
    changed(key) {
        if (this.conflicted || this.applying || !this.catalog.parameters.some(p => p.key === key)) return;
        if (!this.selected || (this.builtIn(this.selected) && this.matches(this.selected))) return;
        if (!this.pending) {
            this.pending = this.checkpoint();
            // Settings signals arrive after the value changes; undo to the saved pipeline on failure.
            this.pending.settings = {...this.selected.values};
        }
        if (!this.builtIn(this.selected)) this.selected.values = this.capture(this.selected.values);
        else this.selected = null;
        this.error = '';
        if (this.saveTimer !== null) this.scheduler.cancel(this.saveTimer);
        if (this.scheduler) this.saveTimer = this.scheduler.schedule(() => { this.saveTimer = null; this.flush(); });
        else this.flush();
        this.notify();
    }
    flush() {
        if (this.saveTimer !== null) this.scheduler.cancel(this.saveTimer);
        this.saveTimer = null;
        if (this.conflicted) return false;
        if (!this.pending) return true;
        const checkpoint = this.pending;
        this.pending = null;
        try { this.save(); this.error = ''; }
        catch (error) { this.failed(error, checkpoint); this.notify(); return false; }
        this.notify();
        return true;
    }
    dispose() { this.flush(); this.listeners.clear(); }
    create(name) {
        this.run(() => {
            this.validateName(name); this.ensureCapacity();
            this.selected = {id: this.storage.id(), name: name.trim(), values: this.capture(this.selected?.values)};
            this.items.push(this.selected); this.save();
        });
    }
    duplicate(pipeline) {
        this.run(() => {
            this.ensureCapacity();
            const copy = {id: this.storage.id(), name: this.uniqueName(`${pipeline.name} copy`), values: {...pipeline.values}};
            this.items.push(copy); this.apply(copy);
        });
    }
    rename(pipeline, name) {
        this.run(() => { this.editable(pipeline); this.validateName(name, pipeline.id); pipeline.name = name.trim(); this.save(); });
    }
    delete(pipeline) {
        this.run(() => {
            this.editable(pipeline);
            this.items = this.items.filter(p => p !== pipeline);
            if (this.selected === pipeline) this.selected = null;
            this.save();
        });
    }
    import(contents) {
        this.run(() => {
            const doc = this.parse(contents);
            this.ensureCapacity(doc.pipelines.length);
            for (const p of doc.pipelines)
                this.items.push({...p, id: this.storage.id(), name: this.uniqueName(p.name)});
            this.save();
        });
    }
    export() {
        if (!this.flush()) throw Error(this.error);
        return JSON.stringify({version: 1, pipelines: this.items.filter(p => !this.builtIn(p))}, null, 2);
    }
    save() {
        if (!this.writable) throw Error('Repair or move the unreadable pipeline library before saving.');
        const contents = JSON.stringify({version: 1, selectedId: this.selected?.id ?? null, pipelines: this.items.filter(p => !this.builtIn(p))}, null, 2);
        this.storage.write(contents, this.contents);
        this.contents = contents;
    }
    parse(contents) {
        if (new TextEncoder().encode(contents).length > 512 * 1024) throw Error('Pipeline library exceeds 512 KiB.');
        const doc = JSON.parse(contents);
        if (!doc || doc.version !== 1 || !Array.isArray(doc.pipelines) || doc.pipelines.length > 64 || (doc.selectedId != null && typeof doc.selectedId !== 'string')) throw Error('Unsupported pipeline library.');
        const ids = new Set(), names = new Set();
        for (const pipeline of doc.pipelines) {
            if (!pipeline || typeof pipeline.id !== 'string' || !pipeline.id.trim() || pipeline.id.length > 80 || this.builtIn(pipeline) || ids.has(pipeline.id) || typeof pipeline.name !== 'string' || !pipeline.name.trim() || pipeline.name.length > 80 || names.has(pipeline.name.trim().toLowerCase()) || !pipeline.values || Array.isArray(pipeline.values) || typeof pipeline.values !== 'object')
                throw Error('Invalid or duplicate pipeline name or identifier.');
            ids.add(pipeline.id); names.add(pipeline.name.trim().toLowerCase()); pipeline.name = pipeline.name.trim();
            for (const [key, value] of Object.entries(pipeline.values)) {
                const p = this.catalog.parameters.find(p => p.key === key);
                if (!p || (typeof p.default === 'boolean' ? typeof value !== 'boolean' : typeof value !== 'number' || !Number.isFinite(value) || value < Math.min(p.min, p.windowsMin ?? p.min) || value > Math.max(p.max, p.windowsMax ?? p.max)))
                    throw Error(`Invalid pipeline parameter: ${key}`);
            }
            pipeline.values = this.complete(pipeline.values);
        }
        return doc;
    }
    ensureCapacity(count = 1) { if (this.items.filter(p => !this.builtIn(p)).length + count > 64) throw Error('A library supports up to 64 saved pipelines.'); }
    editable(p) { if (this.builtIn(p)) throw Error('Duplicate a built-in pipeline to edit it.'); }
    validateName(name, except = null) {
        if (typeof name !== 'string' || !name.trim() || name.trim().length > 80 || this.items.some(p => p.id !== except && p.name.toLowerCase() === name.trim().toLowerCase()))
            throw Error('Choose a unique name of 1–80 characters.');
    }
    uniqueName(name) {
        name = name.slice(0, 70); let result = name, suffix = 2;
        while (this.items.some(p => p.name.toLowerCase() === result.toLowerCase())) result = `${name} ${suffix++}`;
        return result;
    }
}
