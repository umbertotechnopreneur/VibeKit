// SPDX-License-Identifier: GPL-3.0-or-later
import GLib from 'gi://GLib';

export {processMemory} from './shared/shell-pressure.js';

// Require two low samples. Removing the source cancels monitoring when the component closes.
export function watchMemory(sample, stop) {
    let previous = false;
    return GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 3, () => {
        const event = sample();
        if (event && previous) {
            stop({atUtc: new Date().toISOString(), ...event});
            return GLib.SOURCE_REMOVE;
        }
        previous = Boolean(event);
        return GLib.SOURCE_CONTINUE;
    });
}
