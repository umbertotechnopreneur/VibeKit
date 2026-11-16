// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {PipelineConflictError, PipelineModel} from './shared/pipeline-model.js';

export function loadPipelines(settings) {
    const directory = Gio.File.new_for_uri(import.meta.url).get_parent();
    const [, bytes] = directory.get_child('shared/pipeline-catalog.json').load_contents(null);
    const catalog = JSON.parse(new TextDecoder().decode(bytes));
    const path = GLib.build_filenamev([GLib.get_user_config_dir(), 'VibeWare', 'Ripple', 'pipelines.json']);
    const file = Gio.File.new_for_path(path);
    const parameters = new Map(catalog.parameters.map(p => [p.key, p]));
    const adapter = {
        supports: key => settings.settings_schema.has_key(key),
        get: key => settings.get_value(key).deep_unpack(),
        clamp: (key, value) => Math.max(parameters.get(key).min, Math.min(parameters.get(key).max, value)),
        set(key, value) {
            if (typeof value === 'boolean') settings.set_boolean(key, value);
            else settings.set_double(key, this.clamp(key, value));
        },
    };
    const storage = {
        id: () => GLib.uuid_string_random(),
        read() {
            return snapshot(file).contents;
        },
        write(contents, expected) {
            withLibraryLock(path, () => {
                const previous = snapshot(file);
                if (previous.contents !== expected)
                    throw new PipelineConflictError('Pipeline library changed in another window. Reopen this panel before saving.');
                GLib.mkdir_with_parents(file.get_parent().get_path(), 0o700);
                try {
                    file.replace_contents(contents, previous.etag, false, Gio.FileCreateFlags.PRIVATE, null);
                } catch (error) {
                    if (error.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.WRONG_ETAG))
                        throw new PipelineConflictError('Pipeline library changed in another window. Reopen this panel before saving.');
                    throw error;
                }
            });
        },
    };
    const scheduler = {
        schedule(callback) {
            return GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => { callback(); return GLib.SOURCE_REMOVE; });
        },
        cancel: id => GLib.source_remove(id),
    };
    return {model: new PipelineModel(catalog, adapter, storage, scheduler), path};
}

function snapshot(file) {
    try {
        if (file.query_info('standard::size', Gio.FileQueryInfoFlags.NONE, null).get_size() > 512 * 1024)
            throw Error('Pipeline library exceeds 512 KiB.');
        const [, bytes, etag] = file.load_contents(null);
        if (bytes.length > 512 * 1024) throw Error('Pipeline library exceeds 512 KiB.');
        return {contents: new TextDecoder().decode(bytes), etag};
    } catch (error) {
        if (error.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.NOT_FOUND)) return {contents: null, etag: null};
        throw error;
    }
}

export function readLibrary(file) { return snapshot(file).contents; }

// A non-queued bus name serializes the compare-and-replace, including first creation.
// The bus releases ownership if a panel crashes, so no stale disk lock needs recovery.
function withLibraryLock(path, action) {
    const name = `local.vibeware.Ripple.Library.l${GLib.compute_checksum_for_string(GLib.ChecksumType.SHA256, path, -1)}`;
    const connection = Gio.DBus.session;
    const call = (method, signature, args) => connection.call_sync(
        'org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', method,
        new GLib.Variant(signature, args), new GLib.VariantType('(u)'), Gio.DBusCallFlags.NONE, 1000, null).deep_unpack()[0];
    const result = call('RequestName', '(su)', [name, 4]); // DO_NOT_QUEUE
    if (result !== 1 && result !== 4)
        throw new PipelineConflictError('Pipeline library is busy in another window. Reopen this panel before saving.');
    try { return action(); }
    finally {
        if (result === 1) {
            try { call('ReleaseName', '(s)', [name]); }
            catch (error) { console.warn('Ripple could not release its library lock:', error.message); }
        }
    }
}
