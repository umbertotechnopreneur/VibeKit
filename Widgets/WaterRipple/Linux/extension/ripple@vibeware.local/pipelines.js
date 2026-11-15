// SPDX-License-Identifier: GPL-3.0-or-later
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {PipelineModel} from './shared/pipeline-model.js';

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
            if (!file.query_exists(null)) return null;
            return readLibrary(file);
        },
        write(contents) {
            GLib.mkdir_with_parents(file.get_parent().get_path(), 0o700);
            file.replace_contents(contents, null, false, Gio.FileCreateFlags.PRIVATE, null);
        },
    };
    return {model: new PipelineModel(catalog, adapter, storage), path};
}

export function readLibrary(file) {
    if (file.query_info('standard::size', Gio.FileQueryInfoFlags.NONE, null).get_size() > 512 * 1024)
        throw Error('Pipeline library exceeds 512 KiB.');
    const [, bytes] = file.load_contents(null);
    return new TextDecoder().decode(bytes);
}
