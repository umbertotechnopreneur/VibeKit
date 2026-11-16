// SPDX-License-Identifier: MIT
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {buildControls} from './settings-ui.js';
export default class RetroPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) { buildControls(window, this.getSettings()); }
}
