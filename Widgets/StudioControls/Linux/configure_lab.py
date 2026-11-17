"""Idempotent, offline OBS collection upgrade. Caller must ensure OBS is closed."""

import copy
import math
from pathlib import Path
from uuid import uuid4
from screen_framing import VIGNETTES, VIGNETTE_FILTER, vignette_settings
from lab_catalog import CARD_SOURCES, CANVAS, FULL_FRAME, OUTRO_CAMERA_RECT, COMPARISON_RECTS
from lab_titles import normalize


def _validate_collection(collection):
    if not isinstance(collection, dict) or not isinstance(collection.get("sources"), list):
        raise ValueError("Expected an OBS scene collection with a sources list")
    sources = collection["sources"]
    for source in sources:
        if not isinstance(source, dict) or not all(
            key in source for key in ("name", "id", "uuid", "settings")
        ):
            raise ValueError("Every OBS source needs a name, type, identifier and settings")
        if not isinstance(source["settings"], dict):
            raise ValueError("Invalid settings for source " + source["name"])
        if source["id"] == "scene" and not isinstance(source["settings"].get("items"), list):
            raise ValueError("Scene " + source["name"] + " has no items list")
    named = {source["name"]: source for source in sources}
    if len(named) != len(sources):
        raise ValueError("Duplicate source names in scene collection")
    required_sources = ("Logitech Brio", "ASUS ProArt", "umbertogiacobbi.biz")
    missing = [name for name in required_sources if name not in named]
    if missing:
        raise ValueError("Required sources are missing: " + ", ".join(missing))
    for name in ("That's me", "Software Demo", "Screen Only", "Hardware Demo", "On the Table"):
        if name not in named or named[name]["id"] != "scene":
            raise ValueError("Required original scene is missing: " + name)
    resolution = collection.get("resolution", {})
    if not isinstance(resolution, dict) or any(
        not isinstance(resolution.get(axis), (int, float))
        or not math.isfinite(resolution[axis])
        or resolution[axis] <= 0
        for axis in ("x", "y")
    ):
        raise ValueError("OBS canvas resolution must contain positive width and height")
    if not any(source["id"] == "image_source" for source in sources):
        raise ValueError("An existing image source is required as an OBS format template")
    if not any(source["id"] == "scene" and source["settings"]["items"] for source in sources):
        raise ValueError("An existing populated scene is required as an OBS item template")


def configure(collection, root, values, capture_width=3840):
    """Upgrade this lab's collection; capture_width is the selected display's pixel width."""
    _validate_collection(collection)
    values = normalize(values)
    if not isinstance(capture_width, int) or capture_width < 2:
        raise ValueError("Capture width must be an integer of at least two pixels")
    result = copy.deepcopy(collection)
    root = Path(root)
    sources = {s["name"]: s for s in result["sources"]}
    source_template = copy.deepcopy(next(s for s in result["sources"] if s["id"] == "image_source"))
    scene_template = copy.deepcopy(
        next(s for s in result["sources"] if s["id"] == "scene" and s["settings"]["items"])
    )
    item_template = copy.deepcopy(scene_template["settings"]["items"][0])
    width, height = result["resolution"]["x"], result["resolution"]["y"]
    sx, sy = width / CANVAS[0], height / CANVAS[1]

    def source(name, kind, settings):
        if name in sources:
            existing = sources[name]
            if existing.get("versioned_id", existing["id"]) != kind:
                raise ValueError("Source name conflict: " + name)
            existing["settings"].update(settings)
            return existing
        obj = copy.deepcopy(source_template)
        obj.update(
            name=name,
            uuid=str(uuid4()),
            id=kind.removesuffix("_v2").removesuffix("_v3"),
            versioned_id=kind,
            settings=settings,
            filters=[],
            hotkeys={},
        )
        result["sources"].append(obj)
        sources[name] = obj
        return obj

    def image(name, file):
        return source(name, "image_source", {"file": str(root / file), "unload": False})

    def scene(name):
        if name in sources:
            if sources[name]["id"] != "scene":
                raise ValueError("Scene name conflict: " + name)
            return sources[name]
        obj = copy.deepcopy(scene_template)
        obj.update(
            name=name,
            uuid=str(uuid4()),
            settings={"id_counter": 0, "items": []},
            hotkeys={"OBSBasic.SelectScene": []},
        )
        result["sources"].append(obj)
        sources[name] = obj
        return obj

    def place(owner, src, rect, visible=True, bounds_type=3, crop=(0, 0, 0, 0)):
        x, y, w, h = rect
        x *= sx
        y *= sy
        w *= sx
        h *= sy
        settings = owner["settings"]
        items = settings["items"]
        item = copy.deepcopy(item_template)
        item_id = max([settings.get("id_counter", 0)] + [i["id"] for i in items]) + 1
        item.update(
            name=src["name"],
            source_uuid=src["uuid"],
            id=item_id,
            visible=visible,
            locked=True,
            pos={"x": x, "y": y},
            pos_rel={"x": (2 * x - width) / height, "y": (2 * y - height) / height},
            bounds={"x": w, "y": h},
            bounds_rel={"x": 2 * w / height, "y": 2 * h / height},
            scale={"x": 1.0, "y": 1.0},
            scale_rel={"x": 1.0, "y": 1.0},
            scale_ref={"x": width, "y": height},
            bounds_type=bounds_type,
            bounds_crop=True,
            crop_left=crop[0],
            crop_top=crop[1],
            crop_right=crop[2],
            crop_bottom=crop[3],
        )
        items.append(item)
        settings["id_counter"] = item_id
        return item

    cards = {name: image(name, file) for name, file in CARD_SOURCES}
    intro = scene("Intro")
    outro = scene("Thank you")
    chapter = scene("Chapter")
    privacy = scene("Privacy")
    comparison = scene("Comparison A-B")
    crt = scene("Test card")
    detail = scene("Detail")
    # Newly created scenes are populated once; existing operator layout changes are preserved.
    if not intro["settings"]["items"]:
        place(intro, cards["Intro artwork"], FULL_FRAME)
    if not outro["settings"]["items"]:
        place(outro, sources["Logitech Brio"], OUTRO_CAMERA_RECT)
        place(outro, cards["Thank you artwork"], FULL_FRAME)
    if not chapter["settings"]["items"]:
        place(chapter, cards["Chapter artwork"], FULL_FRAME)
    if not privacy["settings"]["items"]:
        place(privacy, cards["Privacy artwork"], FULL_FRAME)
    if not detail["settings"]["items"]:
        place(detail, sources["ASUS ProArt"], FULL_FRAME)
        place(detail, sources["umbertogiacobbi.biz"], (64, 1234.75, 552, 136))
    if not comparison["settings"]["items"]:
        # One captured display, split into two complete halves. No second portal capture.
        split = capture_width // 2
        place(
            comparison,
            sources["ASUS ProArt"],
            COMPARISON_RECTS[0],
            bounds_type=2,
            crop=(0, 0, capture_width - split, 0),
        )
        place(
            comparison,
            sources["ASUS ProArt"],
            COMPARISON_RECTS[1],
            bounds_type=2,
            crop=(split, 0, 0, 0),
        )
        place(comparison, cards["Comparison frame"], FULL_FRAME)
    if not crt["settings"]["items"]:
        movie = source(
            "CRT test video",
            "ffmpeg_source",
            {
                "local_file": str(root / "CRT-test.mp4"),
                "is_local_file": True,
                "looping": True,
                "restart_on_activate": True,
                "close_when_inactive": True,
                "hw_decode": False,
            },
        )
        movie.update(mixers=0, muted=True)
        place(crt, movie, FULL_FRAME, bounds_type=2)
        place(crt, cards["CRT title overlay"], FULL_FRAME)
    audio = source(
        "Lab intro sound",
        "ffmpeg_source",
        {
            "local_file": str(root / "Lab-intro.wav"),
            "is_local_file": True,
            "looping": False,
            "restart_on_activate": True,
            "close_when_inactive": True,
        },
    )
    audio.update(mixers=255, volume=values["sound_volume"] / 100, muted=not values["sound_enabled"])
    if not any(i["name"] == audio["name"] for i in intro["settings"]["items"]):
        place(intro, audio, (0, 0, 1, 1))

    for key, (overlay_name, target, _) in VIGNETTES.items():
        overlay = image(overlay_name, "Vignette.png")
        if not any(f.get("name") == VIGNETTE_FILTER for f in overlay.get("filters", [])):
            filt = copy.deepcopy(source_template)
            filt.update(
                name=VIGNETTE_FILTER,
                uuid=str(uuid4()),
                id="color_filter",
                versioned_id="color_filter_v2",
                settings=vignette_settings(True, 25 if key == "camera" else 20),
                enabled=True,
                filters=[],
                hotkeys={},
            )
            overlay.setdefault("filters", []).append(filt)
        for owner in [s for s in result["sources"] if s["id"] == "scene"]:
            items = owner["settings"]["items"]
            targets = [i for i in list(items) if i["name"] == target]
            existing = [i for i in items if i["name"] == overlay_name]
            if existing:
                continue
            for target_item in targets:
                effect = copy.deepcopy(target_item)
                effect.update(
                    name=overlay_name,
                    source_uuid=overlay["uuid"],
                    id=max([owner["settings"].get("id_counter", 0)] + [i["id"] for i in items]) + 1,
                    crop_left=0,
                    crop_right=0,
                    crop_top=0,
                    crop_bottom=0,
                    bounds_type=1,
                )
                items.insert(items.index(target_item) + 1, effect)
                owner["settings"]["id_counter"] = effect["id"]

    badge = image("VibeWare branding", "VibeWare-logo.png")
    for owner in [
        s
        for s in result["sources"]
        if s["id"] == "scene" and s["name"] not in ("Intro", "Thank you")
    ]:
        existing = next((i for i in owner["settings"]["items"] if i["name"] == badge["name"]), None)
        if existing:
            existing["visible"] = values["vibeware"]
        else:
            place(owner, badge, (2340, 1240, 160, 160), visible=values["vibeware"], bounds_type=2)
    desired = [
        "Test card",
        "Intro",
        "That's me",
        "Software Demo",
        "Screen Only",
        "Detail",
        "Comparison A-B",
        "Chapter",
        "Hardware Demo",
        "On the Table",
        "Thank you",
        "Privacy",
    ]
    extra = [s["name"] for s in result.get("scene_order", []) if s["name"] not in desired]
    result["scene_order"] = [{"name": name} for name in desired + extra]
    uuids = {s["uuid"] for s in result["sources"]}
    if len(uuids) != len(result["sources"]):
        raise ValueError("Duplicate source identifiers")
    for owner in [s for s in result["sources"] if s["id"] == "scene"]:
        items = owner["settings"]["items"]
        if len({i["id"] for i in items}) != len(items):
            raise ValueError("Duplicate scene item identifiers")
        if any(i["source_uuid"] not in uuids for i in items):
            raise ValueError("Broken source reference")
    return result
