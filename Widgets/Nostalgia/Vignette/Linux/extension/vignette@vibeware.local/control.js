// SPDX-License-Identifier: MIT
import {runProductPanel} from './shared/ui/product-panel.js';
import {buildPreferences} from './preferences-ui.js';

runProductPanel(import.meta.url, 'local.vibeware.Vignette', buildPreferences);
