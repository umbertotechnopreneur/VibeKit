import copy
import unittest
from screen_framing import screen_crop, screen_state, vignette_settings
from obs_remote import OBS

BASE = {
    "sourceWidth": 3840.0,
    "sourceHeight": 2160.0,
    "boundsType": "OBS_BOUNDS_SCALE_OUTER",
    "boundsWidth": 5120.0,
    "boundsHeight": 2880.0,
    "positionX": 0.0,
    "positionY": 0.0,
    "cropLeft": 0,
    "cropRight": 0,
    "cropTop": 0,
    "cropBottom": 0,
}


class FakeOBS(OBS):
    def __init__(self):
        self.scene_name = "Software Demo"
        self.calls = []
        self.item = {
            "sceneItemId": 1,
            "sourceName": "ASUS ProArt",
            "sceneItemTransform": copy.deepcopy(BASE),
        }

    def call(self, kind, data=None):
        self.calls.append((kind, data))
        if kind == "GetCurrentProgramScene":
            return {"currentProgramSceneName": self.scene_name}
        if kind == "GetSceneItemList":
            return {"sceneItems": [self.item]}
        if kind == "SetSceneItemTransform":
            self.item["sceneItemTransform"].update(data["sceneItemTransform"])
            return {}
        if kind == "SetSourceFilterSettings":
            return {}
        raise AssertionError(kind)


class ScreenTests(unittest.TestCase):
    def test_crop_stays_inside_screen_at_all_zoom_and_pan_extremes(self):
        for zoom in [1, 1.01, 1.5, 2, 3.99, 4]:
            for x in [-100, -33, 0, 66, 100]:
                for y in [-100, 0, 100]:
                    crop = screen_crop(BASE, zoom, x, y)
                    self.assertTrue(all(v >= 0 for v in crop.values()))
                    self.assertGreater(3840 - crop["cropLeft"] - crop["cropRight"], 0)
                    self.assertGreater(2160 - crop["cropTop"] - crop["cropBottom"], 0)
                    state = screen_state(dict(BASE, **crop))
                    self.assertAlmostEqual(state["zoom"], zoom, delta=0.004)

    def test_two_x_detail_reaches_all_four_corners(self):
        self.assertEqual(
            screen_crop(BASE, 2, -100, -100),
            {"cropLeft": 0, "cropRight": 1920, "cropTop": 0, "cropBottom": 1080},
        )
        self.assertEqual(
            screen_crop(BASE, 2, 100, 100),
            {"cropLeft": 1920, "cropRight": 0, "cropTop": 1080, "cropBottom": 0},
        )

    def test_reset_clears_crop_from_zoomed_state(self):
        zoomed = dict(BASE, **screen_crop(BASE, 3, 70, -40))
        self.assertEqual(
            screen_crop(zoomed, 1, 0, 0),
            {k: 0 for k in ["cropLeft", "cropRight", "cropTop", "cropBottom"]},
        )

    def test_invalid_capture_does_not_generate_transform(self):
        self.assertIsNone(screen_state(dict(BASE, sourceWidth=0)))
        with self.assertRaises(ValueError):
            screen_crop(BASE, float("nan"), 0, 0)

    def test_layout_and_unrelated_source_fields_are_never_written(self):
        o = FakeOBS()
        o.frame_screen("Software Demo", 1, 2, 35, -45)
        writes = [data for kind, data in o.calls if kind == "SetSceneItemTransform"]
        self.assertEqual(len(writes), 1)
        self.assertEqual(
            set(writes[0]["sceneItemTransform"]), {"cropLeft", "cropRight", "cropTop", "cropBottom"}
        )
        self.assertEqual(o.item["sceneItemTransform"]["boundsWidth"], 5120)

    def test_delayed_drag_after_scene_switch_is_ignored(self):
        o = FakeOBS()
        o.scene_name = "Hardware Demo"
        o.frame_screen("Software Demo", 1, 2, 0, 0)
        self.assertFalse(any(kind == "SetSceneItemTransform" for kind, _ in o.calls))

    def test_scene_change_during_item_read_prevents_delayed_crop(self):
        class ChangingScene(FakeOBS):
            def call(self, kind, data=None):
                if kind == "GetSceneItemList":
                    self.scene_name = "Hardware Demo"
                return super().call(kind, data)

        obs = ChangingScene()
        obs.frame_screen("Software Demo", 1, 2, 0, 0)
        self.assertFalse(any(kind.startswith("Set") for kind, _ in obs.calls))

    def test_invalid_comparison_does_not_send_partial_mutations(self):
        class Comparison(FakeOBS):
            def call(self, kind, data=None):
                if kind == "GetSceneItemList":
                    self.calls.append((kind, data))
                    return {"sceneItems": self.items}
                return super().call(kind, data)

        for count, width in [(1, 1920), (3, 1920), (2, 0)]:
            with self.subTest(count=count, width=width):
                obs = Comparison()
                obs.items = [
                    dict(obs.item, sceneItemTransform=dict(BASE, sourceWidth=width))
                    for _ in range(count)
                ]
                with self.assertRaises(RuntimeError):
                    obs.scene("Comparison A-B")
                self.assertFalse(any(kind.startswith("Set") for kind, _ in obs.calls))

    def test_replaced_item_cannot_zoom_another_source(self):
        o = FakeOBS()
        o.item["sourceName"] = "Logitech Brio"
        with self.assertRaises(RuntimeError):
            o.frame_screen("Software Demo", 1, 2, 0, 0)

    def test_off_preserves_strength_and_on_restores_it(self):
        off = vignette_settings(False, 43)
        self.assertEqual(off, {"opacity": 0.0, "lab_enabled": False, "lab_strength": 43})
        self.assertEqual(vignette_settings(True, off["lab_strength"])["opacity"], 0.43)

    def test_each_effect_uses_a_different_source(self):
        o = FakeOBS()
        for target in ["screen", "camera", "hardware"]:
            o.vignette(target, True, 25)
        self.assertEqual(len({d["sourceName"] for k, d in o.calls}), 3)


if __name__ == "__main__":
    unittest.main()
