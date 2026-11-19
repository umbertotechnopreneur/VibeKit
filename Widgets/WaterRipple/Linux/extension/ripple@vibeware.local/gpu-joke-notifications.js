// SPDX-License-Identifier: GPL-3.0-or-later
import GLib from 'gi://GLib';

export const GPU_JOKES = [
    'Family time won. Gaming time lost. The GPU makes ripples now.',
    'Imagine a $1,000 GPU. Now give it ripples.',
    'Built for gaming. Promoted to desktop decoration.',
    'No time for one more level. Always time for one more ripple.',
    'A very serious use of a gaming GPU.',
];

export function scheduleGpuJokeNotifications(settings, show) {
    let source = 0;
    let index = 0;
    const cancel = () => {
        if (source) GLib.source_remove(source);
        source = 0;
    };
    const schedule = seconds => {
        source = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, seconds, () => {
            source = 0;
            if (!settings.get_boolean('show-gpu-jokes')) return GLib.SOURCE_REMOVE;
            const joke = GPU_JOKES[index];
            index = (index + GLib.random_int_range(1, GPU_JOKES.length)) % GPU_JOKES.length;
            schedule(GLib.random_int_range(60, 121));
            show(joke);
            return GLib.SOURCE_REMOVE;
        });
    };
    const sync = () => {
        cancel();
        if (settings.get_boolean('show-gpu-jokes')) schedule(10);
    };
    const signal = settings.connect('changed::show-gpu-jokes', sync);
    sync();
    return () => {
        settings.disconnect(signal);
        cancel();
    };
}
