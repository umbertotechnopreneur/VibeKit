// SPDX-License-Identifier: GPL-3.0-or-later
// Replaces extension.js only in the private smoke-test installation.
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import ProductionExtension from './production-extension.js';

const now = () => GLib.get_monotonic_time() / 1e6;
const delay = ms => new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
    resolve();
    return GLib.SOURCE_REMOVE;
}));
const assert = (condition, message) => { if (!condition) throw Error(message); };

function statistics(label, samples) {
    assert(samples.length > 5, `${label}: insufficient shader updates`);
    const gaps = samples.slice(1).map((sample, i) => (sample.time - samples[i].time) * 1000);
    const sorted = gaps.slice().sort((a, b) => a - b);
    const quantile = p => sorted[Math.ceil(sorted.length * p) - 1];
    // The production shader age must use real elapsed time even across an FPS
    // change, rather than advancing by a fixed amount on each update.
    for (let i = 1; i < samples.length; ++i) {
        const elapsed = samples[i].time - samples[i - 1].time;
        const ageDelta = samples[i].age - samples[i - 1].age;
        assert(ageDelta > 0 && Math.abs(ageDelta - elapsed) < 0.005,
            `${label}: animation age is not monotonic wall-clock time`);
    }
    const metrics = {label, updates: samples.length,
        durationSeconds: samples.at(-1).time - samples[0].time,
        updatesPerSecond: (samples.length - 1) / (samples.at(-1).time - samples[0].time),
        p50GapMs: quantile(0.50), p95GapMs: quantile(0.95), maxGapMs: sorted.at(-1)};
    console.log('RIPPLES_CADENCE ' + JSON.stringify(metrics));
    return metrics;
}

export default class AnimationSmoke extends ProductionExtension {
    enable() {
        super.enable();
        GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 4, () => {
            this.exercise().catch(error => console.error('RIPPLES_SMOKE_FAILED', error));
            return GLib.SOURCE_REMOVE;
        });
    }

    async exercise() {
        Main.overview.hide();
        let source = new St.BoxLayout({vertical: true, x: 300, y: 145, width: 620, height: 510,
            style: 'background-color: #172738; padding: 24px; border: 1px solid #8fb8db;'});
        for (const text of ['RIPPLE / REAL GNOME SHADER', 'Native frame clock · 24 → 60',
            '0123456789  ABCDEFGHIJKLMNOPQRST', '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━',
            'Ripples: compact spacing and highlights', 'No live user windows in this fixture',
            '0123456789  ABCDEFGHIJKLMNOPQRST', '━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━']) {
            source.add_child(new St.Label({text,
                style: 'font: 21px monospace; color: #d7ebff; padding: 10px 0;'}));
        }
        Main.layoutManager.uiGroup.add_child(source);
        const assertCleared = label => {
            assert(this._patches.length === 0 && this._timeline === null && this._frameSignal === 0,
                `${label}: retained patch or native timeline`);
        };
        const addPatch = () => {
            const [x, y] = source.get_transformed_position();
            this._addPatch(source, x + source.width / 2, y + source.height / 2, false);
            assert(this._patches.length === 1 && this._timeline?.is_playing(),
                'Cannot start animation: ' + this._settings.get_string('status'));
            return this._patches[0];
        };
        const screenshot = async name => {
            if (GLib.getenv('RIPPLES_TEST_SCREENSHOTS') !== '1') return;
            const file = Gio.File.new_for_path(`${GLib.getenv('RIPPLES_TEST_OUTPUT')}/${name}.png`);
            const stream = file.replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
            try { await new Shell.Screenshot().screenshot(false, stream); }
            finally { stream.close(null); }
        };
        try {
            await delay(700); // Complete overview and fixture allocation before adding a clone.
            this._settings.set_boolean('auto-restart', false);
            this._settings.set_boolean('crest-enabled', true);
            this._settings.set_double('wavelength', 46);
            this._settings.set_double('rings', 3);
            this._settings.set_double('speed', 50); // Keep the measured wave inside the crop.
            this._settings.set_double('lifetime', 6);
            this._settings.set_int('patch-size', 512);
            this._settings.set_int('fps', 24);
            this._settings.set_double('lease-until', now() + 7.5);
            this._settings.set_boolean('active', true);
            await screenshot('gnome-original');
            const patch = addPatch();
            let samples = [];
            const originalSetAge = patch.effect.setAge.bind(patch.effect);
            patch.effect.setAge = age => {
                samples.push({time: now(), age});
                originalSetAge(age);
            };
            await delay(250); // Shader compilation is not part of steady-state cadence.
            samples = [];
            await delay(1500);
            const cap24 = statistics('native-cap-24', samples);
            const timeline = this._timeline;
            const ageBefore = samples.at(-1).age;
            this._settings.set_int('fps', 60);
            assert(this._patches[0] === patch && this._timeline === timeline,
                'Changing FPS destroyed an active wave or its timeline');
            await delay(100);
            assert(samples.at(-1).age > ageBefore, 'FPS change reset animation age');
            samples = [];
            await delay(1500);
            const cap60 = statistics('native-cap-60', samples);
            assert(this._settings.get_boolean('active'), this._settings.get_string('status'));
            assert(cap24.updatesPerSecond >= 19 && cap24.updatesPerSecond <= 29,
                '24 FPS cap did not produce the requested update cadence');
            assert(cap60.updatesPerSecond >= 45 && cap60.updatesPerSecond <= 70,
                '60 FPS animation failed to approach the 60 Hz virtual display');
            assert(cap60.updatesPerSecond > cap24.updatesPerSecond * 1.7,
                '60 FPS selection did not improve update cadence');
            assert(cap60.p95GapMs < 45, '60 FPS animation has excessive steady-state gaps');

            this._clear();
            assertCleared('Clear');
            await delay(100);
            assertCleared('Idle after clear');
            addPatch();
            await delay(350);
            await screenshot('gnome-ripple');
            this._settings.set_boolean('active', false);
            assertCleared('Manual stop');
            assert(!this._pointerTimer && !this._recoveryTimer && !this._recovery,
                'Manual stop retained polling or recovery');
            await delay(150);
            assert(!this._settings.get_boolean('active'), 'Manual stop restarted itself');

            this._settings.set_double('lease-until', now() + 7.5);
            this._settings.set_boolean('active', true);
            addPatch();
            await delay(100);
            source.destroy();
            source = null;
            assertCleared('Source destruction');
            this._settings.set_boolean('active', false);
            console.log('RIPPLES_SMOKE_PASS: real shader at 24/60 FPS, age continuity, FPS changes, clear, manual stop and source destruction');
        } finally {
            this._settings.set_boolean('active', false);
            this._clear();
            source?.destroy();
        }
    }
}
