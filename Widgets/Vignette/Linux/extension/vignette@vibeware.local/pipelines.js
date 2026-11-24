// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {validateAppearance} from './gradient.js';

export function loadPipelines(directory, settings) {
    const [, contents] = directory.get_child('pipelines.json').load_contents(null);
    const builtins = JSON.parse(new TextDecoder().decode(contents))
        .map(entry => ({...entry, ...validateAppearance(entry), builtin: true}));
    let personal;
    try {
        personal = JSON.parse(settings.get_string('pipeline-library'));
        if (!Array.isArray(personal) || personal.length > 60)
            throw Error('Invalid personal pipeline library.');
        personal = personal.map(entry => {
            if (typeof entry.id !== 'string' || typeof entry.name !== 'string' || !entry.name.trim() || entry.name.length > 80)
                throw Error('Invalid personal pipeline identity.');
            return {id: entry.id, name: entry.name, ...validateAppearance(entry)};
        });
    } catch (error) {
        return {builtins, personal: [], error};
    }
    return {builtins, personal, error: null};
}

export function readAppearance(settings) {
    const [color, intensity, radius, softness, aspect] = settings.get_value('appearance').deepUnpack();
    return validateAppearance({color, intensity, radius, softness, aspect});
}

export function writeAppearance(settings, values) {
    const {color, intensity, radius, softness, aspect} = validateAppearance(values);
    return settings.set_value('appearance', new GLib.Variant('(sdddd)', [color, intensity, radius, softness, aspect]));
}

export function savePipelines(settings, items) {
    if (items.length > 60) throw Error('The personal library can hold 60 pipelines.');
    if (!settings.set_string('pipeline-library', JSON.stringify(items)))
        throw Error('The pipeline library is read-only.');
}

export function newPipeline(name, appearance) {
    const title = name.trim();
    if (title.length < 1 || title.length > 80) throw Error('Enter a name between 1 and 80 characters.');
    return {id: GLib.uuid_string_random(), name: title, ...validateAppearance(appearance)};
}

export function extensionDirectory(url) {
    return Gio.File.new_for_uri(url).get_parent();
}
