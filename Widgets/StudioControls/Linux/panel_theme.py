"""Panel appearance preferences and GTK palettes, independent from OBS output."""

import json
from pathlib import Path

from diagnostics import get_logger
from lab_titles import atomic_bytes

LOGGER = get_logger(__name__)
SETTINGS_PATH = Path.home() / ".config/umbertogiacobbi/panel-settings.json"
THEMES = ("light", "dark", "system")
DEFAULT_THEME = "light"
# Symbolic icons retain a distinct colour in both palettes. Selected scenes
# use the darker variant so the icon remains visible on the salmon surface.
SCENE_COLORS = {
    "Test card": ("test-card", "#7243a1", "#c9a2f0"),
    "Intro": ("intro", "#0d6b64", "#5ed4c7"),
    "That's me": ("presenter", "#1a61a8", "#86b9f4"),
    "Software Demo": ("software", "#4c4aa9", "#aba7ff"),
    "Screen Only": ("screen", "#00677e", "#69c7e4"),
    "Detail": ("detail", "#964700", "#f9b367"),
    "Comparison A-B": ("comparison", "#8c3e83", "#e3a1d9"),
    "Chapter": ("chapter", "#3f6a25", "#a5cf7b"),
    "Hardware Demo": ("hardware", "#775900", "#e1c266"),
    "On the Table": ("table", "#994332", "#f3a18f"),
    "Thank you": ("thanks", "#9e325d", "#f59dbd"),
    "Privacy": ("privacy", "#49616d", "#a3becd"),
}
PALETTES = {
    "light": {
        "background": "#f5f2ee",
        "header": "#ece7e1",
        "surface": "#fffdf9",
        "hover": "#e9e3dc",
        "foreground": "#292622",
        "muted": "#625b53",
        "border": "#bcb3aa",
        "disabled_background": "#e8e3dd",
        "disabled_foreground": "#6c6259",
        "focus": "#935040",
    },
    "dark": {
        "background": "#171a1e",
        "header": "#20242a",
        "surface": "#292e35",
        "hover": "#343b44",
        "foreground": "#eee9e2",
        "muted": "#b9b0a6",
        "border": "#69727e",
        "disabled_background": "#262a30",
        "disabled_foreground": "#a49a90",
        "focus": "#eba295",
    },
}


def load_theme(path=SETTINGS_PATH):
    try:
        settings = json.loads(Path(path).read_text())
        if not isinstance(settings, dict) or settings.get("theme", DEFAULT_THEME) not in THEMES:
            raise ValueError("Unknown panel appearance")
        return settings.get("theme", DEFAULT_THEME)
    except FileNotFoundError:
        return DEFAULT_THEME
    except (OSError, ValueError, TypeError):
        LOGGER.warning("Could not read panel appearance; using Light", exc_info=True)
        return DEFAULT_THEME


def save_theme(theme, path=SETTINGS_PATH):
    if theme not in THEMES:
        raise ValueError("Unknown panel appearance")
    atomic_bytes(Path(path), (json.dumps({"theme": theme}, indent=2) + "\n").encode())


def resolve_theme(preference, system_scheme="default", fallback_dark=False):
    if preference in PALETTES:
        return preference
    if preference != "system":
        return DEFAULT_THEME
    if system_scheme == "prefer-dark":
        return "dark"
    if system_scheme == "prefer-light":
        return "light"
    return "dark" if fallback_dark else "light"


def theme_css(theme):
    palette = PALETTES[theme]
    definitions = "\n".join(f"@define-color lab_{name} {value};" for name, value in palette.items())
    icon_rules = []
    for name, light, dark in SCENE_COLORS.values():
        color = light if theme == "light" else dark
        icon_rules.append(
            f"window.umberto-lab .workflow-scene-icon.scene-{name}, "
            f"window.umberto-lab button.scene-button image.scene-{name} "
            f"{{ color: {color}; }}\n"
            f"window.umberto-lab button.scene-button.suggested-action:not(:disabled) "
            f"image.scene-{name} {{ color: {light}; }}\n"
            f"window.umberto-lab button.workflow-step.step-{name} "
            f"{{ border-top-color: {color}; }}"
        )
    return (
        definitions
        + """
@define-color lab_accent #eba295;
@define-color lab_accent_foreground #29201c;

window.umberto-lab, window.umberto-lab:backdrop {
  background-color: @lab_background;
  color: @lab_foreground;
}
window.umberto-lab headerbar, window.umberto-lab headerbar:backdrop {
  background-image: none;
  background-color: @lab_header;
  color: @lab_foreground;
  border-bottom: 1px solid @lab_border;
  box-shadow: none;
}
window.umberto-lab button {
  background-image: none;
  background-color: @lab_surface;
  color: @lab_foreground;
  border: 1px solid @lab_border;
  border-radius: 8px;
  box-shadow: none;
  text-shadow: none;
}
window.umberto-lab button:hover { background-color: @lab_hover; }
window.umberto-lab button.suggested-action,
window.umberto-lab button:checked {
  background-color: @lab_accent;
  color: @lab_accent_foreground;
  border-color: @lab_focus;
}
window.umberto-lab .dim-label { color: @lab_muted; opacity: 1; }
window.umberto-lab button.suggested-action .dim-label,
window.umberto-lab button:checked .dim-label { color: @lab_accent_foreground; }
window.umberto-lab button:disabled {
  background-color: @lab_disabled_background;
  color: @lab_disabled_foreground;
  border-color: @lab_border;
  opacity: 1;
}
window.umberto-lab button:disabled label,
window.umberto-lab button:disabled image,
window.umberto-lab button:disabled .dim-label,
window.umberto-lab scale:disabled { color: @lab_disabled_foreground; opacity: 1; }
window.umberto-lab button.scene-button { padding: 12px; }
window.umberto-lab button.scene-button:disabled image {
  -gtk-icon-filter: none;
  opacity: 0.85;
}
window.umberto-lab .scene-title { font-weight: 700; }
window.umberto-lab headerbar windowcontrols button {
  background-image: none;
  background-color: @lab_hover;
  border: none;
  border-radius: 999px;
  min-width: 30px;
  min-height: 30px;
  padding: 0;
  margin: 8px 3px;
  box-shadow: none;
}
window.umberto-lab headerbar windowcontrols button image {
  background: none;
  border: none;
  padding: 0;
  box-shadow: none;
}
window.umberto-lab headerbar windowcontrols button:hover { background-color: @lab_border; }
window.umberto-lab headerbar windowcontrols button.close {
  background-color: #c43e42;
  color: #ffffff;
}
window.umberto-lab headerbar windowcontrols button.close:hover { background-color: #a62d32; }
window.umberto-lab headerbar windowcontrols button:focus-visible {
  outline: 2px solid @lab_focus;
  outline-offset: 2px;
}
window.umberto-lab entry,
window.umberto-lab textview, window.umberto-lab textview text {
  background-color: @lab_surface;
  color: @lab_foreground;
  caret-color: @lab_foreground;
}
window.umberto-lab entry { border-color: @lab_border; }
window.umberto-lab entry:disabled,
window.umberto-lab textview:disabled, window.umberto-lab textview:disabled text {
  background-color: @lab_disabled_background;
  color: @lab_disabled_foreground;
}
window.umberto-lab notebook > header {
  background-color: @lab_header;
  border-color: @lab_border;
}
window.umberto-lab notebook > header tab { color: @lab_muted; }
window.umberto-lab notebook > header tab:checked {
  background-color: @lab_surface;
  color: @lab_foreground;
  border-bottom: 3px solid @lab_focus;
  box-shadow: none;
}
window.umberto-lab notebook > stack { background-color: @lab_background; }
window.umberto-lab scale trough {
  background-color: @lab_hover;
  border: 1px solid @lab_border;
}
window.umberto-lab scale highlight {
  background-color: @lab_accent;
  border-color: @lab_focus;
}
window.umberto-lab scale slider {
  background-image: none;
  background-color: @lab_foreground;
  border-color: @lab_surface;
  box-shadow: none;
}
window.umberto-lab scale:disabled slider { background-color: @lab_disabled_foreground; }
window.umberto-lab scale value { color: @lab_muted; }
window.umberto-lab scale:disabled value { color: @lab_disabled_foreground; }
window.umberto-lab checkbutton check {
  background-color: @lab_surface;
  border-color: @lab_border;
  color: @lab_foreground;
}
window.umberto-lab checkbutton check:checked {
  background-color: @lab_accent;
  color: @lab_accent_foreground;
}
window.umberto-lab popover > contents,
window.umberto-lab dropdown listview,
window.umberto-lab dropdown row {
  background-color: @lab_surface;
  color: @lab_foreground;
}
window.umberto-lab dropdown row:hover { background-color: @lab_hover; }
window.umberto-lab dropdown row:selected {
  background-color: @lab_accent;
  color: @lab_accent_foreground;
}
window.umberto-lab scrollbar slider { background-color: @lab_muted; }
window.umberto-lab :focus-visible { outline-color: @lab_focus; }
window.umberto-lab .workflow-title { font-size: 22px; font-weight: 700; }
window.umberto-lab .workflow-board {
  background-color: @lab_header;
  border: 1px solid alpha(@lab_border, 0.85);
  border-radius: 12px;
}
window.umberto-lab .workflow-board-header { padding: 10px 14px; border-bottom: 1px solid alpha(@lab_border, 0.55); }
window.umberto-lab .workflow-board-footer { padding: 10px 14px; border-top: 1px solid alpha(@lab_border, 0.55); }
window.umberto-lab .workflow-metric { font-weight: 700; font-variant-numeric: tabular-nums; }
window.umberto-lab .workflow-over { color: @lab_focus; font-weight: 700; }
window.umberto-lab button.workflow-small { padding: 3px; min-width: 24px; min-height: 24px; }
window.umberto-lab button.workflow-step {
  padding: 10px 12px;
  margin: 10px 2px;
  border-radius: 10px;
  border: 1px solid @lab_border;
  border-top-width: 3px;
  background-color: @lab_surface;
}
window.umberto-lab button.workflow-step:hover { background-color: mix(@lab_surface, @lab_hover, 0.45); }
window.umberto-lab .workflow-step-edit { opacity: 0; }
window.umberto-lab button.workflow-step:hover .workflow-step-edit,
window.umberto-lab button.workflow-step:focus-visible .workflow-step-edit { opacity: 0.8; }
window.umberto-lab button.workflow-step.workflow-selected { outline: 2px solid @lab_focus; outline-offset: 0; }
window.umberto-lab .workflow-playing button.workflow-step.workflow-selected { outline-width: 0; }
window.umberto-lab .workflow-playing button.workflow-step.workflow-now {
  outline: 2px solid @lab_focus;
  outline-offset: 0;
  background-color: mix(@lab_surface, @lab_accent, 0.14);
}
window.umberto-lab .workflow-playing button.workflow-step .workflow-step-edit { opacity: 0; }
window.umberto-lab .workflow-position { font-variant-numeric: tabular-nums; font-weight: 700; color: @lab_muted; font-size: 11px; }
window.umberto-lab .workflow-step-name { font-size: 15px; font-weight: 700; }
window.umberto-lab .workflow-step-time { font-variant-numeric: tabular-nums; font-size: 19px; font-weight: 600; }
window.umberto-lab .workflow-secondary { color: @lab_muted; font-size: 12px; }
window.umberto-lab .workflow-palette { padding: 8px 2px; }
window.umberto-lab button.workflow-source { padding: 7px 10px; }
window.umberto-lab .workflow-library-section {
  background-color: @lab_surface;
  padding: 12px;
  border: 1px solid alpha(@lab_border, 0.65);
  border-radius: 10px;
}
window.umberto-lab .workflow-library-list { background-color: @lab_surface; }
window.umberto-lab .workflow-library-list row:selected { background-color: @lab_hover; color: @lab_foreground; }
/* Keep the lit time face legible in both desktop themes. */
window.umberto-lab .presenter-stage {
  background: #1e262b;
  color: #f4f6f4;
  border: 1px solid #394348;
  border-radius: 12px;
}
window.umberto-lab .presenter-clock-row { padding: 8px 14px; }
window.umberto-lab.presenter-large .presenter-clock-row { padding: 18px 24px; }
window.umberto-lab .presenter-caption { color: #b0c2c7; font-size: 12px; font-weight: 500; }
window.umberto-lab .presenter-clock, window.umberto-lab .presenter-total {
  font-family: monospace;
  font-variant-numeric: tabular-nums;
  color: #f7faf7;
  font-weight: 700;
}
window.umberto-lab .presenter-clock { font-size: 54px; }
window.umberto-lab.presenter-large .presenter-clock { font-size: 84px; }
window.umberto-lab .presenter-total { font-size: 19px; }
window.umberto-lab.presenter-large .presenter-total { font-size: 28px; }
window.umberto-lab .presenter-route { padding: 8px 15px 10px; border-top: 1px solid #445155; }
window.umberto-lab.presenter-large .presenter-route { padding: 13px 24px 17px; }
window.umberto-lab .presenter-route .dim-label { color: #a5b4b8; }
window.umberto-lab .presenter-current, window.umberto-lab .presenter-next { font-size: 19px; font-weight: 600; }
window.umberto-lab.presenter-large .presenter-current, window.umberto-lab.presenter-large .presenter-next { font-size: 25px; }
window.umberto-lab .presenter-note { padding: 0 16px 10px; color: #e1e8e7; }
window.umberto-lab .presenter-obs { font-size: 12px; color: @lab_muted; }
window.umberto-lab button.presenter-command { min-width: 24px; min-height: 26px; padding: 3px 5px; }
window.umberto-lab.presenter-compact .presenter-clock.long-time { font-size: 37px; }
window.umberto-lab.presenter-large .presenter-clock.long-time { font-size: 64px; }
window.umberto-lab .presenter-glow-red {
  background-image: radial-gradient(circle closest-side at 50% 20%, rgba(255, 67, 47, 0.38), rgba(255, 67, 47, 0.16) 62%, transparent);
}
window.umberto-lab .presenter-glow-amber {
  background-image: radial-gradient(circle closest-side at 50% 49%, rgba(255, 193, 48, 0.38), rgba(255, 193, 48, 0.16) 62%, transparent);
}
window.umberto-lab .presenter-glow-green {
  background-image: radial-gradient(circle closest-side at 50% 78%, rgba(68, 231, 96, 0.38), rgba(68, 231, 96, 0.16) 62%, transparent);
}
window.umberto-lab.timer-settings switch:checked { background-color: @lab_focus; border-color: @lab_focus; }
window.umberto-lab .timer-settings-row { padding: 6px 2px; }
window.umberto-lab .title-1 { font-size: 28px; }
"""
        + "\n".join(icon_rules)
    )
