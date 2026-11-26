// SPDX-License-Identifier: MIT
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {buildPreferences} from './preferences-ui.js';

export default class VignettePreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) { buildPreferences(window, this.getSettings()); }
}
