// SPDX-License-Identifier: GPL-3.0-or-later
import Dbusmenu from 'gi://Dbusmenu?version=0.4';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const WATCHER = 'org.kde.StatusNotifierWatcher';
const ITEM_PATH = '/StatusNotifierItem';
const MENU_PATH = '/StatusNotifierItem/Menu';
const ITEM_INTERFACE = `
<node>
  <interface name="org.kde.StatusNotifierItem">
    <property name="Category" type="s" access="read"/>
    <property name="Id" type="s" access="read"/>
    <property name="Title" type="s" access="read"/>
    <property name="Status" type="s" access="read"/>
    <property name="IconName" type="s" access="read"/>
    <property name="ItemIsMenu" type="b" access="read"/>
    <property name="Menu" type="o" access="read"/>
    <method name="Activate">
      <arg type="i" direction="in"/>
      <arg type="i" direction="in"/>
    </method>
  </interface>
</node>`;

export class TrayIndicator {
    constructor({show, toggleEffect, about, quit}, availabilityChanged) {
        this.available = false;
        this._availabilityChanged = availabilityChanged;
        this._cancellable = new Gio.Cancellable();

        const menu = new Dbusmenu.Menuitem();
        const addItem = (label, action) => {
            const item = new Dbusmenu.Menuitem();
            item.property_set('label', label);
            item.connect('item-activated', action);
            menu.child_append(item);
            return item;
        };
        addItem('Open', show);
        this._effect = addItem('Effect', toggleEffect);
        this._effect.property_set('toggle-type', 'checkmark');
        addItem('About', about);
        addItem('Quit', quit);
        this._server = Dbusmenu.Server.new(MENU_PATH);
        this._server.set_root(menu);

        const bus = Gio.DBus.session;
        this._item = Gio.DBusExportedObject.wrapJSObject(ITEM_INTERFACE, {
            Category: 'ApplicationStatus',
            Id: 'local.vibeware.Ripple',
            Title: 'Ripple',
            Status: 'Active',
            IconName: 'local.vibeware.Ripple',
            ItemIsMenu: true,
            Menu: MENU_PATH,
            Activate: show,
        });
        this._item.export(bus, ITEM_PATH);
        this._watch = Gio.bus_watch_name_on_connection(bus, WATCHER,
            Gio.BusNameWatcherFlags.NONE,
            (connection, _name, owner) => {
                this._owner = owner;
                connection.call(owner, '/StatusNotifierWatcher', WATCHER,
                    'RegisterStatusNotifierItem', new GLib.Variant('(s)', [ITEM_PATH]),
                    null, Gio.DBusCallFlags.NONE, 3000, this._cancellable, (conn, result) => {
                        try {
                            conn.call_finish(result);
                            if (this._owner === owner)
                                this._setAvailable(true);
                        } catch (error) {
                            if (this._owner === owner)
                                console.warn('Ripple indicator registration failed', error.message);
                        }
                    });
            },
            () => {
                this._owner = null;
                this._setAvailable(false);
            });
    }

    setEffect(active, available) {
        this._effect.property_set_int('toggle-state', active ? 1 : 0);
        this._effect.property_set_bool('enabled', available);
    }

    _setAvailable(available) {
        if (this.available === available)
            return;
        this.available = available;
        this._availabilityChanged(available);
    }

    destroy() {
        this._owner = null;
        this._cancellable.cancel();
        Gio.bus_unwatch_name(this._watch);
        this._item.unexport();
        this._server = null;
    }
}
