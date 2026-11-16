// SPDX-License-Identifier: MIT
// Test driver copied over extension.js ONLY inside the isolated smoke-test tree.
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import ProductionExtension from './production-extension.js';

const delay = ms => new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
    resolve(); return GLib.SOURCE_REMOVE;
}));

export default class SmokeTest extends ProductionExtension {
    enable() {
        super.enable();
        GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 4, () => {
            this.exercise().catch(error => console.error('RETRO_SMOKE_FAILED', error));
            return GLib.SOURCE_REMOVE;
        });
    }
    async exercise() {
        Main.overview.hide();
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
        try {
            const shot = async (name, settle = 700) => {
                await delay(settle);
                const file = Gio.File.new_for_path(`${GLib.getenv('RETRO_TEST_OUTPUT')}/${name}.png`);
                const stream = file.replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
                try { await new Shell.Screenshot().screenshot(false, stream); }
                finally { stream.close(null); }
            };
            await shot('gnome-original');
            this._settings.set_boolean('active', true);
            if (!this._effect) throw Error(this._settings.get_string('status'));
            for (const [index, name] of ['green', 'amber', 'color'].entries()) {
                this._settings.set_int('preset', index);
                await shot(`gnome-${name}`);
            }
            this._settings.set_boolean('custom-color', true);
            this._settings.set_string('phosphor-color', '#5bd8ff');
            this._settings.set_int('mask', 2);
            this._settings.set_double('scanline-spacing', 5);
            this._settings.set_double('scanline-width', 1.5);
            this._settings.set_double('glow-radius', 5);
            await shot('gnome-custom-cyan');
            this._settings.set_boolean('glitches-enabled', false);
            this._settings.set_double('glitch-duration-min', 400);
            this._settings.set_double('glitch-duration-max', 400);
            this._settings.set_double('glitch-intensity', 0.7);
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
                if (this._glitchSequence.event || this._glitchTimer || lastGlitch !== 0)
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
            if (this._glitchWake || this._glitchTimer) throw Error('Turning off random glitches retained a timer');
            this._settings.set_boolean('glitches-enabled', true);
            this._settings.set_boolean('persistence-enabled', true);
            // The same public settings path used by the native Stop button.
            this._settings.set_boolean('active', false);
            if (this._effect || this._timer || this._previewTimer || this._glitchTimer || this._glitchWake || this._persistence)
                throw Error('Stop retained resources');
            await delay(200);
            if (this._settings.get_boolean('active')) throw Error('Stop must not restart');
            console.log('RETRO_SMOKE_PASS: palettes, custom optics, all glitches/reset, real history, random cancellation and complete stop');
        } finally {
            this._stop('Smoke test finished');
            demo.destroy();
            marker.destroy();
        }
    }
}
