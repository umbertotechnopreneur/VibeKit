// SPDX-License-Identifier: MIT
import GLib from 'gi://GLib';

export const GPU_JOKES = [
    'Family time won. Gaming time lost. The GPU makes scanlines now.',
    'Imagine a $1,000 GPU. Now ask it to look like 1985.',
    'Built for gaming. Promoted to desktop decoration.',
    'All this graphics power, just to bring back scanlines.',
    'No time to game. Plenty of time to adjust the phosphor.',
];

export function scheduleStartupGpuJoke(settings, show) {
    if (!settings.get_boolean('show-gpu-jokes')) return () => {};

    let signal = 0;
    const delay = GLib.random_int_range(120, 241);
    let source = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, delay, () => {
        source = 0;
        cancel();
        if (settings.get_boolean('show-gpu-jokes')) show(GPU_JOKES[0]);
        return GLib.SOURCE_REMOVE;
    });
    const cancel = () => {
        if (source) GLib.source_remove(source);
        if (signal) settings.disconnect(signal);
        source = signal = 0;
    };
    signal = settings.connect('changed::show-gpu-jokes', () => {
        if (!settings.get_boolean('show-gpu-jokes')) cancel();
    });
    return cancel;
}
