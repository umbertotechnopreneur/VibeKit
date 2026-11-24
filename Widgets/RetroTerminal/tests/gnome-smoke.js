// SPDX-License-Identifier: MIT
// Test driver copied over extension.js ONLY inside the isolated smoke-test tree.
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import ProductionExtension from './production-extension.js';
import {applyValues, BUILTINS} from './settings-model.js';

const delay = ms => new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
    resolve(); return GLib.SOURCE_REMOVE;
}));

// The private test compositor owns this bus. Never touch the user's display.
async function setFractionalTestScale() {
    const call = (method, parameters = null) => new Promise((resolve, reject) => {
        Gio.DBus.session.call('org.gnome.Mutter.DisplayConfig', '/org/gnome/Mutter/DisplayConfig',
            'org.gnome.Mutter.DisplayConfig', method, parameters, null, Gio.DBusCallFlags.NONE, 5000, null,
            (connection, result) => {
                try { resolve(connection.call_finish(result).deep_unpack()); }
                catch (error) { reject(error); }
            });
    });
    const [serial, monitors] = await call('GetCurrentState');
    if (monitors.length !== 1) throw Error('Oversized test requires exactly one private virtual monitor');
    const [spec, modes] = monitors[0];
    const mode = modes.find(item => item[1] === 6720 && item[2] === 2160 && item[5].includes(1.5));
    if (!mode) throw Error('Private virtual monitor does not support the required fractional test mode');
    await call('ApplyMonitorsConfig', new GLib.Variant('(uua(iiduba(ssa{sv}))a{sv})',
        [serial, 1, [[0, 0, 1.5, 0, true, [[spec[0], mode[0], {}]]]], {'layout-mode': new GLib.Variant('u', 1)}]));
    await delay(1000);
    if (global.stage.width !== 4480 || global.stage.height !== 1440)
        throw Error(`Unexpected private stage size: ${global.stage.width}×${global.stage.height}`);
}

export default class SmokeTest extends ProductionExtension {
    enable() {
        super.enable();
        GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 4, () => {
            this.exercise().catch(error => console.error('RETRO_SMOKE_FAILED', error));
            return GLib.SOURCE_REMOVE;
        });
    }
    async exercise() {
        const oversized = GLib.getenv('RETRO_TEST_OVERSIZED') === '1';
        if (oversized) await setFractionalTestScale();
        Main.overview.hide();
        const trayItems = this._button.menu._getMenuItems();
        if (trayItems[0].label.text !== 'Preferences' || trayItems.at(-1).label.text !== 'About' ||
            trayItems.at(-2).label.text !== 'Mini Utilities' ||
            trayItems.filter(item => item instanceof PopupMenu.PopupSeparatorMenuItem).length !== 3)
            throw Error('The tray menu must group controls between Preferences and About');
        const demo = new St.BoxLayout({vertical: true, x: 160, y: 170, width: 960, height: 420,
            style: 'background-color: #101827; padding: 40px; border-radius: 14px; border: 1px solid #6d7b8f;'});
        demo.add_child(new St.Label({text: 'RETRO TERMINAL / GNOME GPU TEST',
            style: 'font-size: 30px; font-family: monospace; color: #ffffff;'}));
        demo.add_child(new St.Label({text: '\n$ desktop --style phosphor\n\n  Native scene rendering\n  Input coordinates unchanged\n  Super + Shift + F12 stops CRT\n\n  Synthetic content for visual verification',
            style: 'font-size: 23px; font-family: monospace; color: #b0c9e8;'}));
        Main.layoutManager.uiGroup.add_child(demo);
        const marker = new St.Widget({x: 240, y: 650, width: 120, height: 36,
            style: 'background-color: #ffffff;'});
        Main.layoutManager.uiGroup.add_child(marker);
        const farMarker = oversized ? new St.Widget({x: global.stage.width - 240, y: global.stage.height - 180,
            width: 120, height: 36, style: 'background-color: #ffffff;'}) : null;
        if (farMarker) Main.layoutManager.uiGroup.add_child(farMarker);
        try {
            const shot = async (name, settle = 700, crop = null) => {
                await delay(settle);
                const file = Gio.File.new_for_path(`${GLib.getenv('RETRO_TEST_OUTPUT')}/${name}.png`);
                const stream = file.replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
                try {
                    await new Promise((resolve, reject) => {
                        let timer;
                        const paintId = global.stage.connect('after-paint', (_stage, view) => {
                            global.stage.disconnect(paintId);
                            GLib.source_remove(timer);
                            try {
                                // Read the presented frame. An offscreen stage screenshot
                                // can reuse a partially updated effect texture.
                                const texture = view.get_framebuffer().get_texture();
                                const area = crop ?? [0, 0, texture.get_width(), texture.get_height()];
                                Shell.Screenshot.composite_to_stream(texture, ...area, 1, null, 0, 0, 1, stream)
                                    .then(resolve, reject);
                            } catch (error) {
                                reject(error);
                            }
                        });
                        timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 2000, () => {
                            global.stage.disconnect(paintId);
                            reject(Error('No compositor frame was painted'));
                            return GLib.SOURCE_REMOVE;
                        });
                        global.stage.queue_redraw();
                    });
                } finally { stream.close(null); }
            };
            await shot('gnome-original');
            this._settings.set_boolean('active', true);
            const startupDeadline = GLib.get_monotonic_time() + 5_000_000;
            while (this._startupTimer && GLib.get_monotonic_time() < startupDeadline)
                await delay(50);
            if (!this._effect) throw Error(this._settings.get_string('status'));
            if (GLib.getenv('RETRO_TEST_PRESETS') === '1') {
                // Leave a margin around the sampled flat patch for spatial glow.
                const patch = new St.Widget({x: 560, y: 610, width: 160, height: 144,
                    style: 'background-color: #808080;'});
                Main.layoutManager.uiGroup.add_child(patch);
                const crop = [576, 626, 128, 112];
                const frames = [];
                try {
                    for (const {id, values} of BUILTINS) {
                        const filename = id.slice('builtin:'.length);
                        applyValues(this._settings, values);
                        await shot(filename, 35, crop);
                        if (id === 'builtin:tv-composite-pal' || id === 'builtin:trinitron-aperture-grille')
                            await shot(`${filename}-desktop`, 0);
                        this._settings.set_double('grain', 0);
                        await shot(`${filename}-no-grain`, 35, crop);
                        this._settings.set_double('scanlines', 0);
                        await shot(`${filename}-no-raster`, 35, crop);
                        frames.push(filename);
                    }
                    console.log(`RETRO_PRESET_FRAMES ${JSON.stringify(frames)}`);
                    console.log('RETRO_SMOKE_PASS: built-in preset frames');
                } finally { patch.destroy(); }
                return;
            }
            if (oversized) {
                this._settings.set_double('persistence-duration', 0.5);
                this._settings.set_double('persistence-strength', 0.6);
                this._settings.set_boolean('persistence-enabled', true);
                await delay(350);
                const info = this._persistence?.captureInfo[0];
                if (!info?.reduced || info.nativeWidth * info.nativeHeight <= 9000000 ||
                    info.width * info.height > 9000000 || info.width > 8192 || info.height > 8192 ||
                    !this._persistence.hasHistory || this._persistence.captureCount < 2 || this._persistence._slots[0].buffers.length !== 2)
                    throw Error(`Oversized native capture was not bounded: ${JSON.stringify(info)}`);
                if (!this._settings.get_string('status').includes('(scaled)'))
                    throw Error('Reduced history resolution must be visible in status');
                console.log(`RETRO_CAPTURE_INFO ${JSON.stringify({...info, logicalWidth: global.stage.width, logicalHeight: global.stage.height})}`);
                marker.x = 800;
                farMarker.x -= 400;
                await shot('gnome-persistence', 70);
                this._settings.set_boolean('persistence-enabled', false);
                if (this._persistence.running || this._persistence.hasHistory || this._persistence.captureInfo.length)
                    throw Error('Disabling scaled persistence retained history or dimensions');
                await shot('gnome-persistence-off', 100);
                this._settings.set_boolean('active', false);
                if (this._effect || this._persistence || this._timer)
                    throw Error('Scaled history Stop retained resources');
                await delay(200);
                if (this._settings.get_boolean('active')) throw Error('Stop must not restart');
                console.log('RETRO_SMOKE_PASS: oversized native capture, bounded scaled history, status, complete stop');
                return;
            }
            for (const [index, name] of ['green', 'amber', 'color'].entries()) {
                this._settings.set_int('preset', index);
                await shot(`gnome-${name}`);
            }
            let configurations = 0;
            const configure = this._effect.configure.bind(this._effect);
            this._effect.configure = (...args) => {
                configurations++;
                configure(...args);
            };
            this._settings.set_boolean('custom-color', true);
            this._settings.set_string('phosphor-color', '#5bd8ff');
            this._settings.set_int('mask', 2);
            this._settings.set_double('scanline-spacing', 5);
            this._settings.set_double('scanline-width', 1.5);
            this._settings.set_double('glow-radius', 5);
            await shot('gnome-custom-cyan');
            if (configurations !== 1) throw Error(`Expected one shader update for six settings, got ${configurations}`);
            const paletteMenu = trayItems.find(item => item.label?.text === 'Palette').menu;
            paletteMenu._getMenuItems().find(item => item.label.text === 'Amber terminal').activate(null);
            await shot('gnome-tray-amber');
            if (this._settings.get_boolean('custom-color') || configurations !== 2)
                throw Error('Tray palette must override custom color in one shader update');
            this._settings.set_boolean('custom-color', true);
            await delay(60);
            configurations = 0;
            this._settings.set_boolean('glitches-enabled', false);
            this._settings.set_double('glitch-duration-min', 400);
            this._settings.set_double('glitch-duration-max', 400);
            this._settings.set_double('glitch-intensity', 0.7);
            await delay(60);
            if (configurations) throw Error('Glitch timing must not reconfigure the base shader');
            let lastGlitch = 0;
            const setGlitch = this._effect.setGlitch.bind(this._effect);
            this._effect.setGlitch = (type, ...args) => { lastGlitch = type; setGlitch(type, ...args); };
            const types = ['glitch-rgb', 'glitch-tear', 'glitch-noise', 'glitch-band'];
            for (const [index, key] of types.entries()) {
                for (const flag of types) this._settings.set_boolean(flag, flag === key);
                // Honor the production rest period instead of bypassing it.
                const rest = this._glitchSequence.restUntil - GLib.get_monotonic_time() / 1e6;
                if (rest > 0) await delay(Math.ceil(rest * 1000) + 100);
                this._settings.set_uint('glitch-request', this._settings.get_uint('glitch-request') + 1);
                if (this._glitchSequence.event?.type !== index + 1) throw Error(`Cannot start ${key}`);
                await delay(140);
                if (lastGlitch !== index + 1) throw Error(`${key} did not reach the shader`);
                await shot(`gnome-${key}`, 0);
                await delay(450);
                if (this._glitchSequence.event || this._glitchFrames || lastGlitch !== 0)
                    throw Error(`${key} did not finish and reset`);
            }
            this._settings.set_double('persistence-duration', 0.5);
            this._settings.set_double('persistence-strength', 0.6);
            this._settings.set_boolean('persistence-enabled', true);
            await delay(350);
            if (!this._persistence?.running || !this._persistence.hasHistory || this._persistence.captureCount < 2)
                throw Error('Persistence did not retain actual frames');
            marker.x = 800;
            await shot('gnome-persistence', 70);
            this._settings.set_boolean('persistence-enabled', false);
            if (this._persistence.running || this._persistence.hasHistory)
                throw Error('Disabling persistence retained history or its timer');
            await shot('gnome-persistence-off', 100);
            this._settings.set_boolean('glitches-enabled', true);
            if (!this._glitchWake) throw Error('Random scheduling did not arm a wakeup');
            this._settings.set_boolean('glitches-enabled', false);
            if (this._glitchWake || this._glitchFrames) throw Error('Turning off random glitches retained a timer');
            this._settings.set_boolean('glitches-enabled', true);
            this._settings.set_boolean('persistence-enabled', true);
            this._settings.set_strv('target-monitors', []);
            if (this._effect || !this._settings.get_boolean('active'))
                throw Error('Deselecting all monitors must release resources and leave CRT armed');
            this._settings.set_strv('target-monitors', ['*']);
            if (!this._effect || !this._persistence?.running) throw Error('Reselecting a monitor should resume CRT');
            const intensity = this._settings.get_double('intensity');
            this._settings.set_double('intensity', 0);
            await delay(100);
            if (this._effect.get_enabled() || this._unredirectInhibited || this._persistence.running)
                throw Error('Zero intensity should skip the shader, capture and fullscreen composition override');
            this._settings.set_double('intensity', intensity);
            await delay(100);
            if (!this._effect.get_enabled() || !this._unredirectInhibited)
                throw Error('Increasing intensity did not restore the shader');
            this._settings.set_boolean('panic-enabled', false);
            if (!this._effect || this._settings.get_boolean('panic-available'))
                throw Error('Disabling the optional shortcut must keep the effect active');
            // The same public settings path used by the native Stop button.
            this._settings.set_boolean('active', false);
            if (this._effect || this._timer || this._settingsUpdate || this._previewTimer || this._glitchFrames || this._glitchWake || this._persistence)
                throw Error('Stop retained resources');
            await delay(200);
            if (this._settings.get_boolean('active') || this._settings.get_boolean('recovery-pending'))
                throw Error('Stop must not restart');
            console.log('RETRO_SMOKE_PASS: palettes, batched shader updates, custom optics, all glitches/reset, real history, random cancellation and complete stop');
        } finally {
            this._stop('Smoke test finished');
            demo.destroy();
            marker.destroy();
            farMarker?.destroy();
        }
    }
}
