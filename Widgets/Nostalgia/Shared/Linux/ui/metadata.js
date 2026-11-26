// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';

export function readJson(file) {
    const [, bytes] = file.load_contents(null);
    return JSON.parse(new TextDecoder().decode(bytes));
}

// Metadata comes from the installed extension or its standalone archive.
export function productMetadata(moduleUrl) {
    const directory = Gio.File.new_for_uri(moduleUrl).get_parent();
    const metadata = readJson(directory.get_child('metadata.json'));
    for (const key of ['uuid', 'name', 'version-name', 'description', 'settings-schema'])
        if (typeof metadata[key] !== 'string' || !metadata[key].trim())
            throw new Error(`Missing ${key} in ${directory.get_path()}/metadata.json`);
    return {directory, metadata};
}
