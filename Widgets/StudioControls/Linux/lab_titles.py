"""Editable title cards, rendered only on Apply; OBS displays static local images."""

import io
import json
import math
import os
import shutil
import tempfile
import wave
from concurrent.futures import CancelledError
from functools import lru_cache
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageOps
from lab_catalog import CANVAS, OUTRO_CAMERA_RECT, COMPARISON_RECTS
from studio_paths import bundled_assets, working_assets

from diagnostics import get_logger

logger = get_logger(__name__)

ROOT = bundled_assets()
CONFIG = Path.home() / ".config/umbertogiacobbi/lab-titles.json"
VIBE_URL = "umbertogiacobbi.biz/vibeware/manifesto"
DEFAULTS = {
    "name": "Software demo",
    "eyebrow": "UMBERTO’S LAB",
    "subtitle": "A closer look.",
    "description": "",
    "links": ["", "", ""],
    "logo": "",
    "vibeware": False,
    "sound_enabled": True,
    "sound_volume": 18,
    "chapter": "One detail at a time",
    "chapter_description": "",
    "compare_a": "Before",
    "compare_b": "After",
}
INK = "#e8e3dc"
MUTED = "#b9b3ad"
ACCENT = "#eba295"
BACK = "#171a1e"
LINE = "#39434a"
FIELD_LIMITS = {
    "name": 100,
    "eyebrow": 70,
    "subtitle": 180,
    "description": 600,
    "chapter": 100,
    "chapter_description": 180,
    "compare_a": 60,
    "compare_b": 60,
}


def _stage_bytes(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".lab-stage-", dir=path.parent, delete=False) as f:
            temporary = Path(f.name)
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        return temporary
    except BaseException:
        if temporary:
            temporary.unlink(missing_ok=True)
        raise


def atomic_bytes(path, data):
    temporary = _stage_bytes(path, data)
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _check_cancel(cancel_event):
    if cancel_event is not None and cancel_event.is_set():
        raise CancelledError("Project card preparation cancelled")


def _commit_files(updates, cancel_event=None):
    """Stage all writes, then restore previous files on a handled commit failure.

    Each replacement is atomic, but this is not a power-loss-safe filesystem transaction.
    If restoring a file also fails, its backup is retained and its location is logged.
    """
    staged = {}
    backups = {}
    promoted = []
    retained_backups = set()
    rollback_failed = False
    try:
        for path, data in updates.items():
            _check_cancel(cancel_event)
            staged[path] = _stage_bytes(path, data)
            if path.exists():
                backup = _stage_bytes(path, b"")
                backups[path] = backup
                shutil.copyfile(path, backup)
                with backup.open("rb") as handle:
                    os.fsync(handle.fileno())
            else:
                backups[path] = None
        _check_cancel(cancel_event)
        for path, temporary in staged.items():
            _check_cancel(cancel_event)
            os.replace(temporary, path)
            promoted.append(path)
    except BaseException:
        for path in reversed(promoted):
            backup = backups[path]
            try:
                if backup is None:
                    path.unlink(missing_ok=True)
                else:
                    os.replace(backup, path)
            except OSError:
                rollback_failed = True
                if backup is not None:
                    retained_backups.add(backup)
                logger.exception("Could not restore %s; recovery backup: %s", path, backup)
        if rollback_failed:
            raise RuntimeError(
                "Could not restore all project files; recovery backups are recorded in the log"
            ) from None
        raise
    finally:
        for temporary in (*staged.values(), *backups.values()):
            if temporary is not None and temporary not in retained_backups:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    logger.warning(
                        "Could not remove temporary project file %s", temporary, exc_info=True
                    )


def normalize(values):
    if not isinstance(values, dict):
        raise ValueError("Project settings must be a JSON object")
    result = dict(DEFAULTS)
    for key, limit in FIELD_LIMITS.items():
        value = values.get(key, DEFAULTS[key])
        if not isinstance(value, str):
            raise ValueError(key.title() + " must be text")
        result[key] = value.strip()
        if len(result[key]) > limit:
            raise ValueError(key.title() + " is too long (maximum " + str(limit) + " characters)")
    if not result["name"]:
        raise ValueError("Enter a project name")
    links = values.get("links", ["", "", ""])
    if not isinstance(links, list) or len(links) > 3:
        raise ValueError("Use up to three links")
    if any(not isinstance(link, str) for link in links):
        raise ValueError("Links must be text")
    result["links"] = [link.strip() for link in links] + [""] * (3 - len(links))
    if any(len(link) > 150 for link in result["links"]):
        raise ValueError("Keep each displayed link within 150 characters")
    result["logo"] = values.get("logo", "")
    if not isinstance(result["logo"], str):
        raise ValueError("Logo must be a local file path")
    for key in ("vibeware", "sound_enabled"):
        value = values.get(key, DEFAULTS[key])
        if not isinstance(value, bool):
            raise ValueError(key + " must be true or false")
        result[key] = value
    volume = float(values.get("sound_volume", 18))
    if not math.isfinite(volume):
        raise ValueError("Sound volume must be finite")
    result["sound_volume"] = max(0, min(100, round(volume)))
    return result


def load_config(path=CONFIG):
    try:
        return normalize(json.loads(Path(path).read_text(encoding="utf-8")))
    except FileNotFoundError:
        return dict(DEFAULTS, links=["", "", ""])
    except (OSError, ValueError, TypeError, OverflowError):
        logger.warning(
            "Could not load project settings from %s; using defaults and preserving the file",
            path,
            exc_info=True,
        )
        return dict(DEFAULTS, links=["", "", ""])


def save_config(values, path=CONFIG):
    atomic_bytes(
        path, (json.dumps(normalize(values), ensure_ascii=False, indent=2) + "\n").encode()
    )


@lru_cache(maxsize=128)
def font(size, serif=False, bold=False):
    face = "DejaVuSerif" if serif else "DejaVuSans"
    if bold:
        face += "-Bold"
    return ImageFont.truetype(face + ".ttf", size)


def wrapped(draw, text, face, width):
    lines = []
    for paragraph in text.split("\n"):
        current = ""
        for word in paragraph.split():
            if draw.textlength(word, font=face) > width:
                # Long URLs are split at character boundaries, without losing characters.
                if current:
                    lines.append(current)
                    current = ""
                for char in word:
                    if current and draw.textlength(current + char, font=face) > width:
                        lines.append(current)
                        current = ""
                    current += char
            elif current and draw.textlength(current + " " + word, font=face) > width:
                lines.append(current)
                current = word
            else:
                current = (current + " " + word).strip()
        lines.append(current)
    return lines


def text_block(draw, text, box, size, minimum, color=INK, serif=False, bold=False):
    if not text:
        return
    x, y, w, h = box
    for pixels in range(size, minimum - 1, -1):
        face = font(pixels, serif, bold)
        lines = wrapped(draw, text, face, w)
        leading = round(pixels * 1.32)
        if len(lines) * leading <= h:
            for line in lines:
                draw.text((x, y), line, font=face, fill=color, anchor="lt")
                y += leading
            return
    raise ValueError(
        "The text does not fit the card. Shorten the project name, subtitle, description or links."
    )


def paste_logo(canvas, path, box):
    if not path:
        return
    image = path.copy() if isinstance(path, Image.Image) else _load_logo(path)
    bounds = image.getchannel("A").getbbox()
    if bounds:
        image = image.crop(bounds)
    image.thumbnail((box[2], box[3]), Image.Resampling.LANCZOS)
    canvas.alpha_composite(
        image,
        (round(box[0] + (box[2] - image.width) / 2), round(box[1] + (box[3] - image.height) / 2)),
    )


def _load_logo(path):
    path = Path(path)
    if not path.is_absolute():
        path = ROOT / path
    with Image.open(path) as source:
        if source.width * source.height > 16_000_000:
            raise ValueError("Use a logo smaller than 16 megapixels")
        return ImageOps.exif_transpose(source).convert("RGBA")


def _png_bytes(image):
    output = io.BytesIO()
    image.save(output, format="PNG", optimize=True)
    return output.getvalue()


def make_cards(values, root=ROOT, project_logo=None):
    values = normalize(values)
    return [_render_card(values, Path(root), outro, project_logo) for outro in (False, True)]


def brand_asset(root, name):
    candidate = root / name
    return candidate if candidate.is_file() else ROOT / name


def _render_card(values, root, outro=False, project_logo=None):
    """Render a layout from validated content; derived fields have their own fit checks."""
    art = Image.new("RGBA", CANVAS, BACK)
    d = ImageDraw.Draw(art)
    d.rectangle((112, 82, 120, 150), fill=ACCENT)
    d.text(
        (150, 78),
        "Thank you!" if outro else "Umberto’s Lab",
        font=font(100 if outro else 64, serif=True),
        fill=INK,
        anchor="lt",
    )
    if outro:
        # The transparent opening is filled by the existing Brio source in OBS.
        x, y, w, h = OUTRO_CAMERA_RECT
        d.rectangle((x, y, x + w - 1, y + h - 1), fill=(0, 0, 0, 0))
        d.rectangle((109, 347, 1578, 1176), outline=ACCENT, width=4)
        text_block(d, values["eyebrow"], (1660, 220, 788, 65), 30, 22, ACCENT)
        text_block(d, values["name"], (1660, 310, 788, 200), 70, 34, serif=True)
        text_block(d, values["subtitle"], (1660, 545, 788, 125), 40, 24)
        text_block(d, values["description"], (1660, 700, 788, 245), 32, 22, MUTED)
        for index, link in enumerate(values["links"]):
            text_block(
                d,
                link.removeprefix("https://").removeprefix("http://"),
                (1660, 985 + index * 82, 788, 72),
                30,
                19,
                ACCENT,
            )
        if values["logo"]:
            paste_logo(
                art,
                project_logo if project_logo is not None else root / values["logo"],
                (2198, 60, 250, 135),
            )
        if values["vibeware"]:
            paste_logo(art, brand_asset(root, "VibeWare-logo.png"), (112, 1200, 125, 112))
            d.text((270, 1220), "This is VibeWare", font=font(28, bold=True), fill=INK, anchor="lt")
            d.text((270, 1270), VIBE_URL, font=font(26), fill=ACCENT, anchor="lt")
        paste_logo(art, brand_asset(root, "OBS-logo.png"), (1920, 1310, 68, 68))
        d.text((2010, 1330), "Made with OBS Studio", font=font(29), fill=MUTED, anchor="lt")
        d.text((820, 1340), "© 2026 Umberto Giacobbi", font=font(27), fill=MUTED, anchor="lt")
    else:
        text_block(d, values["eyebrow"], (112, 260, 1800, 80), 34, 26, ACCENT, bold=True)
        text_block(d, values["name"], (112, 400, 1900, 310), 134, 64, serif=True)
        text_block(d, values["subtitle"], (112, 750, 2100, 130), 54, 32)
        text_block(d, values["description"], (112, 930, 2000, 200), 38, 24, MUTED)
        for index, link in enumerate(values["links"]):
            text_block(
                d,
                link.removeprefix("https://").removeprefix("http://"),
                (112 + index * 790, 1170, 735, 110),
                31,
                20,
                ACCENT,
            )
        if values["logo"]:
            paste_logo(
                art,
                project_logo if project_logo is not None else root / values["logo"],
                (2120, 235, 330, 300),
            )
        if values["vibeware"]:
            paste_logo(art, brand_asset(root, "VibeWare-logo.png"), (2220, 575, 220, 210))
            text_block(d, "This is VibeWare", (2080, 825, 370, 70), 30, 24, ACCENT)
    d.line((112, 1305, 2448, 1305), fill=LINE, width=2)
    d.text((112, 1340), "umbertogiacobbi.biz", font=font(32), fill=INK, anchor="lt")
    return art


def prepare_cards(values, root=None, config_path=CONFIG, cancel_event=None):
    """Save a complete validated local project; applying it to OBS is a separate step."""
    values = normalize(values)
    _check_cancel(cancel_event)
    root = Path(root) if root is not None else working_assets()
    logo = values["logo"]
    project_logo = None
    updates = {}
    if logo:
        source = Path(logo)
        if not source.is_absolute():
            source = root / source
        project_logo = _load_logo(source)
        project_logo.thumbnail((1600, 1600), Image.Resampling.LANCZOS)
        updates[root / "Project-logo.png"] = _png_bytes(project_logo)
        values["logo"] = "Project-logo.png"
    cards = make_cards(values, root, project_logo)
    extra = extra_cards(values, root)
    for name, image in list(
        zip(("Intro-card.png", "Thank-you-card.png"), cards, strict=False)
    ) + list(extra.items()):
        _check_cancel(cancel_event)
        updates[root / name] = _png_bytes(image)
    updates[Path(config_path)] = (json.dumps(values, ensure_ascii=False, indent=2) + "\n").encode(
        "utf-8"
    )
    _commit_files(updates, cancel_event)
    return values


def extra_cards(values, root=ROOT):
    values = normalize(values)
    chapter = dict(
        values,
        name=values["chapter"] or "Next chapter",
        eyebrow=values["name"],
        subtitle=values["chapter_description"],
        description="",
        links=["", "", ""],
        logo="",
        vibeware=False,
    )
    privacy = dict(
        values,
        name="A short break.",
        eyebrow="STANDBY",
        subtitle="Setting up the next part.",
        description=values["name"],
        links=["", "", ""],
        logo="",
        vibeware=False,
    )
    chapter_art = _render_card(chapter, Path(root))
    privacy_art = _render_card(privacy, Path(root))
    compare = Image.new("RGBA", CANVAS, BACK)
    d = ImageDraw.Draw(compare)
    d.text((80, 70), "Side by side", font=font(88, serif=True), fill=INK, anchor="lt")
    for key, (x, y, w, h) in zip(("compare_a", "compare_b"), COMPARISON_RECTS, strict=False):
        d.rectangle((x, y, x + w - 1, y + h - 1), fill=(0, 0, 0, 0))
        d.rectangle((x - 3, y - 3, x + w + 2, y + h + 2), outline=ACCENT, width=4)
        text_block(d, values[key], (x, 235, w, 70), 44, 25)
    d.text((80, 1320), "umbertogiacobbi.biz", font=font(32), fill=INK, anchor="lt")
    crt = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
    d = ImageDraw.Draw(crt)
    d.rounded_rectangle((112, 1010, 2448, 1325), radius=18, fill=(15, 18, 21, 235))
    text_block(d, values["name"], (155, 1045, 2220, 100), 70, 35, serif=True)
    description = (
        values["description"] or values["subtitle"] or "Please stand by. A demo is tuning in."
    )
    text_block(d, description, (155, 1165, 2220, 125), 36, 23, MUTED)
    d.rectangle((0, 1376, 2560, 1440), fill=(15, 18, 21, 245))
    d.text(
        (112, 1386),
        "CRT footage: Ivan Bridgewater / Internet Archive · CC BY 3.0 · excerpt, audio removed",
        font=font(18),
        fill=MUTED,
        anchor="lt",
    )
    d.text(
        (112, 1412),
        "archive.org/details/IMB_SF_R30_C8 · creativecommons.org/licenses/by/3.0/",
        font=font(16),
        fill=MUTED,
        anchor="lt",
    )
    return {
        "Chapter-card.png": chapter_art,
        "Privacy-card.png": privacy_art,
        "Compare-frame.png": compare,
        "CRT-overlay.png": crt,
    }


def make_chime(path):
    # Original, short synthesized cue; no downloaded music or third-party recording.
    rate = 48000
    duration = 1.35
    data = bytearray()
    for n in range(round(rate * duration)):
        t = n / rate
        signal = 0
        for start, hz in ((0, 523.251), (0.16, 659.255), (0.32, 783.991)):
            elapsed = t - start
            if elapsed >= 0:
                envelope = min(1, elapsed / 0.015) * math.exp(-elapsed * 5.8)
                signal += (
                    0.16
                    * envelope
                    * (
                        math.sin(2 * math.pi * hz * elapsed)
                        + 0.13 * math.sin(4 * math.pi * hz * elapsed)
                    )
                )
        signal *= min(1, (duration - t) / 0.12)
        value = max(-32767, min(32767, round(signal * 32767)))
        data.extend(value.to_bytes(2, "little", signed=True))
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(rate)
        f.writeframes(data)
