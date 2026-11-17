import copy
import unittest

from configure_lab import configure
from lab_titles import DEFAULTS


def collection_fixture():
    sources = []
    for name, kind in [
        ("Logitech Brio", "v4l2_input"),
        ("ASUS ProArt", "pipewire-desktop-capture-source"),
        ("umbertogiacobbi.biz", "image_source"),
    ]:
        sources.append(
            {"name": name, "id": kind, "versioned_id": kind, "uuid": "uuid-" + name, "settings": {}}
        )
    for name in ("That's me", "Software Demo", "Screen Only", "Hardware Demo", "On the Table"):
        sources.append(
            {
                "name": name,
                "id": "scene",
                "uuid": "uuid-" + name,
                "settings": {
                    "id_counter": 1,
                    "items": [
                        {
                            "id": 1,
                            "name": "ASUS ProArt",
                            "source_uuid": "uuid-ASUS ProArt",
                            "visible": True,
                            "pos": {"x": 0, "y": 0},
                            "bounds": {"x": 2560, "y": 1440},
                        }
                    ],
                },
            }
        )
    return {
        "sources": sources,
        "resolution": {"x": 2560, "y": 1440},
        "scene_order": [{"name": source["name"]} for source in sources if source["id"] == "scene"],
    }


class ConfigureTests(unittest.TestCase):
    def test_upgrade_is_idempotent_and_does_not_mutate_input(self):
        original = collection_fixture()
        untouched = copy.deepcopy(original)
        upgraded = configure(original, "/tmp/test-lab-assets", DEFAULTS)
        self.assertEqual(original, untouched)
        self.assertEqual(configure(upgraded, "/tmp/test-lab-assets", DEFAULTS), upgraded)
        scenes = [source for source in upgraded["sources"] if source["id"] == "scene"]
        self.assertEqual(len(scenes), 12)

    def test_explicit_capture_width_splits_display_without_assuming_4k(self):
        upgraded = configure(
            collection_fixture(), "/tmp/test-lab-assets", DEFAULTS, capture_width=1921
        )
        comparison = next(
            source for source in upgraded["sources"] if source["name"] == "Comparison A-B"
        )
        screens = [
            item for item in comparison["settings"]["items"] if item["name"] == "ASUS ProArt"
        ]
        self.assertEqual(
            [(item["crop_left"], item["crop_right"]) for item in screens], [(0, 961), (960, 0)]
        )

    def test_missing_source_and_bad_resolution_have_actionable_errors(self):
        collection = collection_fixture()
        collection["sources"] = [
            source for source in collection["sources"] if source["name"] != "Logitech Brio"
        ]
        with self.assertRaisesRegex(ValueError, "Required sources are missing: Logitech Brio"):
            configure(collection, "/tmp/test-lab-assets", DEFAULTS)
        collection = collection_fixture()
        collection["resolution"]["y"] = 0
        with self.assertRaisesRegex(ValueError, "canvas resolution"):
            configure(collection, "/tmp/test-lab-assets", DEFAULTS)

    def test_an_empty_first_scene_does_not_prevent_using_another_template(self):
        collection = collection_fixture()
        first = next(source for source in collection["sources"] if source["id"] == "scene")
        first["settings"]["items"] = []
        result = configure(collection, "/tmp/test-lab-assets", DEFAULTS)
        self.assertTrue(any(source["name"] == "Intro" for source in result["sources"]))


if __name__ == "__main__":
    unittest.main()
