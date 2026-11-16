// Run against a packaged extension with isolated XDG_CONFIG_HOME and GSETTINGS_BACKEND=memory.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const directory = Gio.File.new_for_path(ARGV[0]);
const {loadPipelines, readLibrary} = await import(directory.get_child('pipelines.js').get_uri());
const {loadSchema} = await import(directory.get_child('schema.js').get_uri());
const schema = loadSchema(directory.get_child('schema.js').get_uri());
const settings = () => Gio.Settings.new_full(schema, null, null);
function check(value, message) { if (!value) throw Error(message); }
const first = loadPipelines(settings()), stale = loadPipelines(settings());
const file = Gio.File.new_for_path(first.path);
const busName = `local.vibeware.Ripple.Library.l${GLib.compute_checksum_for_string(GLib.ChecksumType.SHA256, first.path, -1)}`;
const other = Gio.DBusConnection.new_for_address_sync(Gio.dbus_address_get_for_bus_sync(Gio.BusType.SESSION, null),
    Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION, null, null);
other.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'RequestName',
    new GLib.Variant('(su)', [busName, 4]), new GLib.VariantType('(u)'), Gio.DBusCallFlags.NONE, 1000, null);
first.model.create('Blocked');
check(first.model.error.includes('busy') && !file.query_exists(null) && !first.model.selected, 'First creation ignored a competing connection lock.');
other.close_sync(null); // Simulates a crashed writer: the bus releases the lock.
first.model.create('Saved');
check(!first.model.error, first.model.error);
const original = readLibrary(file);
stale.model.create('Stale');
check(stale.model.error.includes('changed') && readLibrary(file) === original && !stale.model.selected, 'Stale panel overwrote the library.');

const liveSettings = settings();
const live = loadPipelines(liveSettings).model;
liveSettings.connect('changed', (_settings, key) => live.changed(key));
const selected = live.selected;
check(selected, 'Saved selection did not reload.');
for (let i = 0; i < 120; i++) liveSettings.set_double('strength', 1 + i / 100);
check(readLibrary(file) === original && selected.values.strength === 2.19, 'Changes did not stay live while writes were deferred.');
const loop = new GLib.MainLoop(null, false);
GLib.timeout_add(GLib.PRIORITY_DEFAULT, 400, () => {
    check(JSON.parse(readLibrary(file)).pipelines[0].values.strength === 2.19, 'Quiet-period save did not commit.');
    liveSettings.set_double('strength', 6);
    live.dispose();
    check(JSON.parse(readLibrary(file)).pipelines[0].values.strength === 6, 'Closing did not flush the last value.');
    first.model.dispose(); stale.model.dispose();
    print('PASS GIO library locks, stale revisions, live coalesced edits, timed save and close flush');
    loop.quit();
    return GLib.SOURCE_REMOVE;
});
loop.run();
