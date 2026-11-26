// SPDX-License-Identifier: MIT
// Run only with GSETTINGS_BACKEND=memory and a fresh temporary XDG_CONFIG_HOME.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
if (GLib.getenv('GSETTINGS_BACKEND') !== 'memory' || !GLib.getenv('XDG_CONFIG_HOME'))
    throw Error('This check requires isolated memory settings and a temporary XDG_CONFIG_HOME.');
const directory = Gio.File.new_for_path(ARGV[0]);
const {loadPipelines, readLibrary, libraryLockName} = await import(directory.get_child('pipelines.js').get_uri());
const schemaSource = Gio.SettingsSchemaSource.new_from_directory(directory.get_child('schemas').get_path(),
    Gio.SettingsSchemaSource.get_default(), false);
const schema = schemaSource.lookup('org.gnome.shell.extensions.vibeware-retro-terminal', true);
const settings = () => Gio.Settings.new_full(schema, null, null);
const assert = (condition, message) => { if (!condition) throw Error(message); };
// Check the installed schema contract, not a second hard-coded copy of it.
const {NUMERIC, DEFAULTS, Palette, PhosphorMask} = await import(directory.get_child('settings-model.js').get_uri());
for (const [key, defaultValue] of Object.entries(DEFAULTS)) {
    const schemaKey = schema.get_key(key);
    assert(schemaKey.get_default_value().deep_unpack() === defaultValue, `${key}: schema default differs from the model`);
}
for (const spec of NUMERIC) {
    const schemaKey = schema.get_key(spec.key);
    assert(schemaKey.get_value_type().dup_string() === 'd', `${spec.key}: expected a double`);
    const [kind, payload] = schemaKey.get_range().deep_unpack();
    const bounds = payload instanceof GLib.Variant ? payload.deep_unpack() : payload;
    assert(kind === 'range' && bounds[0] === spec.min && bounds[1] === spec.max,
        `${spec.key}: schema limits differ from the descriptor`);
    assert(spec.step > 0 && spec.defaultValue >= spec.min && spec.defaultValue <= spec.max,
        `${spec.key}: invalid descriptor`);
}
for (const [key, values] of [['preset', Palette], ['mask', PhosphorMask]]) {
    const [kind, payload] = schema.get_key(key).get_range().deep_unpack();
    const bounds = payload instanceof GLib.Variant ? payload.deep_unpack() : payload;
    assert(kind === 'range' && bounds[0] === Math.min(...Object.values(values)) &&
        bounds[1] === Math.max(...Object.values(values)), `${key}: enum and schema limits differ`);
}
print('PASS compiled schema defaults, numeric types/ranges and enum contract');
let first = loadPipelines(settings());
const stale = loadPipelines(settings());
const file = Gio.File.new_for_path(first.path);
assert(!file.query_exists(null), 'Use an empty temporary configuration directory.');
const competitor = Gio.DBusConnection.new_for_address_sync(Gio.dbus_address_get_for_bus_sync(Gio.BusType.SESSION, null),
    Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION, null, null);
competitor.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'RequestName',
    new GLib.Variant('(su)', [libraryLockName(first.path), 4]), new GLib.VariantType('(u)'),
    Gio.DBusCallFlags.NONE, 1000, null);
first.model.create('Blocked writer');
assert(first.model.error.includes('busy') && !file.query_exists(null), 'A competing writer must block initial file creation.');
competitor.close_sync(null);
first.model.dispose();
first = loadPipelines(settings());
first.model.create('My terminal');
assert(!first.model.error, first.model.error);
assert((file.query_info('unix::mode', Gio.FileQueryInfoFlags.NONE, null).get_attribute_uint32('unix::mode') & 0o777) === 0o600,
    'The library must be private to its owner.');
const original = readLibrary(file);
stale.model.create('Do not overwrite');
assert(stale.model.error.includes('changed') && readLibrary(file) === original, 'A stale writer replaced the library.');
const liveSettings = settings();
const live = loadPipelines(liveSettings).model;
const handler = liveSettings.connect('changed', (_object, key) => live.changed(key));
const observerSettings = settings();
const observer = loadPipelines(observerSettings).model;
const observerHandler = observerSettings.connect('changed', (_object, key) => observer.changed(key));
assert(live.selected?.name === 'My terminal', 'Selected pipeline did not restore.');
for (let i = 0; i < 100; i++) liveSettings.set_double('scanlines', i / 200);
assert(readLibrary(file) === original, 'Continuous control changes wrote before the quiet period.');
assert(live.selected.values.scanlines === 0.495, 'The selected pipeline did not update live.');
const loop = new GLib.MainLoop(null, false);
GLib.timeout_add(GLib.PRIORITY_DEFAULT, 400, () => {
    assert(JSON.parse(readLibrary(file)).pipelines[0].values.scanlines === 0.495, 'Quiet-period persistence did not run.');
    assert(!observer.writable && !observer.selected && observer.error.includes('changed'),
        'A competing observer must detach after the first panel saves.');
    assert(liveSettings.get_double('scanlines') === 0.495 && live.selected.values.scanlines === 0.495,
        'A stale observer reverted shared GSettings and the winning pipeline.');
    liveSettings.set_string('phosphor-color', '#11cc88');
    live.dispose();
    assert(JSON.parse(readLibrary(file)).pipelines[0].values['phosphor-color'] === '#11cc88', 'Closing lost the pending color edit.');
    liveSettings.disconnect(handler);
    observerSettings.disconnect(observerHandler);
    observer.dispose();
    first.model.dispose(); stale.model.dispose();
    print('PASS CRT native storage: private file, competing lock, shared-GSettings observer conflicts, 250ms autosave and close flush');
    loop.quit();
    return GLib.SOURCE_REMOVE;
});
loop.run();
