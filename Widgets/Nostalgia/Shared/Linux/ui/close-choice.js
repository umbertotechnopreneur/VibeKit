// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';

// A panel can hide only while its own menu offers a way back.
export function closeChoice(window, name, hide, quit, detail = () => '') {
    let dialog = null;
    let available = false;
    const update = () => {
        if (!dialog) return;
        dialog.body = (available ? 'Hide the panel to keep the effect running in the top bar.'
            : 'The top bar icon is unavailable.') + ' Quitting stops the effect.' + detail();
        dialog.set_response_enabled('hide', available);
        dialog.set_default_response(available ? 'hide' : 'cancel');
    };
    return {
        setAvailable(value) {
            available = value;
            update();
        },
        request() {
            if (dialog) return true;
            dialog = new Adw.AlertDialog({heading: `Hide the panel or quit ${name}?`, prefer_wide_layout: true});
            dialog.add_response('cancel', 'Cancel');
            dialog.add_response('hide', 'Hide to tray');
            dialog.add_response('quit', 'Quit');
            dialog.set_close_response('cancel');
            dialog.set_response_appearance('quit', Adw.ResponseAppearance.DESTRUCTIVE);
            dialog.connect('response', (_dialog, response) => {
                dialog = null;
                if (response === 'hide' && available) hide();
                else if (response === 'quit') quit();
            });
            update();
            dialog.present(window);
            return true;
        },
    };
}
