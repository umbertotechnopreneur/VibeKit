// SPDX-License-Identifier: GPL-3.0-or-later
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {buildControls} from './settings-ui.js';

export default class RipplePreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        buildControls(window, this.getSettings());
    }
}
