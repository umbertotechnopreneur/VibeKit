// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {PipelineModel, PipelineConflictError, LIBRARY_BYTES} from './pipeline-model.js';
import {readValues, applyValues} from './settings-model.js';

function readSnapshot(file) {
    try {
        const info = file.query_info('standard::size', Gio.FileQueryInfoFlags.NONE, null);
        if (info.get_size() > LIBRARY_BYTES) throw Error('Pipeline library exceeds 512 KiB.');
        const [, bytes, etag] = file.load_contents(null);
        if (bytes.length > LIBRARY_BYTES) throw Error('Pipeline library exceeds 512 KiB.');
        return {text: new TextDecoder().decode(bytes), etag};
    } catch (error) {
        if (error.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.NOT_FOUND)) return {text: null, etag: null};
        throw error;
    }
}

export function readLibrary(file) {
    const text = readSnapshot(file).text;
    if (text === null) throw Error('The selected pipeline file no longer exists.');
    return text;
}

export function libraryLockName(path) {
    return `local.vibeware.RetroTerminal.Pipelines.p${GLib.compute_checksum_for_string(GLib.ChecksumType.SHA256, path, -1)}`;
}

function exclusiveUpdate(path, update) {
    const connection = Gio.DBus.session;
    const name = libraryLockName(path);
    const invoke = (method, type, args) => connection.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus',
        'org.freedesktop.DBus', method, new GLib.Variant(type, args), new GLib.VariantType('(u)'),
        Gio.DBusCallFlags.NONE, 1000, null).deep_unpack()[0];
    const acquired = invoke('RequestName', '(su)', [name, 4]);
    if (acquired !== 1 && acquired !== 4)
        throw new PipelineConflictError('Pipeline library is busy in another window. Reopen this panel before saving.');
    try { update(); }
    finally {
        if (acquired === 1) {
            try { invoke('ReleaseName', '(s)', [name]); }
            catch (error) { console.warn('Retro Terminal pipeline lock release:', error.message); }
        }
    }
}

export function loadPipelines(settings) {
    const path = GLib.build_filenamev([GLib.get_user_config_dir(), 'VibeWare', 'RetroTerminal', 'pipelines.json']);
    const file = Gio.File.new_for_path(path);
    const storage = {
        id: () => GLib.uuid_string_random(),
        read: () => readSnapshot(file).text,
        write(text, previous) {
            exclusiveUpdate(path, () => {
                const current = readSnapshot(file);
                if (current.text !== previous)
                    throw new PipelineConflictError('Pipelines changed in another window. Reopen this panel before saving.');
                if (GLib.mkdir_with_parents(file.get_parent().get_path(), 0o700) !== 0)
                    throw Error('Could not create the pipeline library directory.');
                try { file.replace_contents(text, current.etag, false, Gio.FileCreateFlags.PRIVATE, null); }
                catch (error) {
                    if (error.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.WRONG_ETAG))
                        throw new PipelineConflictError('Pipelines changed during the save. Reopen this panel before saving.');
                    throw error;
                }
            });
        },
    };
    const adapter = {
        read: () => readValues(settings),
        apply(values) {
            applyValues(settings, values);
            const applied = readValues(settings);
            if (Object.entries(values).some(([key, value]) => applied[key] !== value))
                throw Error('Some effect settings are read-only. The pipeline was not applied.');
        },
    };
    const scheduler = {
        schedule: callback => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => { callback(); return GLib.SOURCE_REMOVE; }),
        cancel: id => GLib.source_remove(id),
    };
    const model = new PipelineModel(adapter, storage, scheduler, settings.get_string('saved-presets'));
    return {model, path};
}
