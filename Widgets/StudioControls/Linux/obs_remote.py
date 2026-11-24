"""Authenticated, single-worker OBS access with explicit capability failures."""

import base64
import copy
import hashlib
import json
import sys
import time
from pathlib import Path
from uuid import uuid4

from diagnostics import get_logger
from lab_catalog import CARD_SOURCES
from studio_paths import working_assets
from screen_framing import (
    SCREEN_SOURCE,
    VIGNETTES,
    VIGNETTE_FILTER,
    screen_crop,
    screen_state,
    vignette_settings,
)

sys.path.insert(0, str(Path(__file__).resolve().parent / "_vendor"))
import websocket

LOGGER = get_logger(__name__)
FILTER = "Background blur"
AUDIO_INPUTS = ("Mic/Aux", "Desktop Audio")
REQUEST_TIMEOUT = 3.0


class OBSError(RuntimeError):
    """Base class for actionable OBS failures."""


class OBSRequestError(OBSError):
    """OBS received and rejected a request; the connection remains usable."""

    def __init__(self, request, code=None, comment="OBS rejected the request"):
        super().__init__(f"{request}: {comment} (code {code if code is not None else 'unknown'})")
        self.request = request
        self.code = code


class OBSConnectionError(OBSError):
    """A request could not be completed; a sent mutation must not be replayed."""

    def __init__(self, message, request_sent=False):
        super().__init__(message)
        self.request_sent = request_sent


class ProjectApplyError(OBSError):
    """Local project assets were saved but OBS may only be partly updated."""

    saved_locally = True


def _empty_status():
    return {
        "currentProgramSceneName": "Unknown scene",
        "outputActive": False,
        "outputPaused": False,
        "blurAvailable": False,
        "blurEnabled": False,
        "blurIntensity": 3,
        "audioMutes": {},
        "audioAvailable": {name: False for name in AUDIO_INPUTS},
        "screen": None,
        "vignettes": {},
        "warnings": [],
        "connectionAvailable": True,
        "statusFresh": True,
    }


class OBS:
    def __init__(self, config_path=None, assets_root=None, title_config=None):
        self.ws = None
        self.config_path = (
            Path(config_path)
            if config_path
            else Path.home() / ".config/obs-studio/plugin_config/obs-websocket/config.json"
        )
        self._assets_root = Path(assets_root) if assets_root is not None else None
        self.title_config = (
            Path(title_config)
            if title_config
            else Path.home() / ".config/umbertogiacobbi/lab-titles.json"
        )
        self._last_status = None

    @property
    def assets_root(self):
        # Read-only OBS status construction must not create or copy user files.
        if self._assets_root is None:
            self._assets_root = working_assets()
        return self._assets_root

    def select_card_project(self, root, settings):
        self._assets_root = root
        self.title_config = settings

    def _workflow_recording(self):
        status = self.call("GetRecordStatus")
        if (
            type(status.get("outputActive")) is not bool
            or type(status.get("outputPaused")) is not bool
        ):
            raise ValueError("OBS recording status is unconfirmed.")
        if status["outputPaused"]:
            raise ValueError("Recording was paused or stopped.")
        return status["outputActive"]

    def workflow_preflight(self, required):
        collection = self.call("GetSceneCollectionList")["currentSceneCollectionName"]
        listing = self.call("GetSceneList")
        available = {scene["sceneName"] for scene in listing["scenes"]}
        required = set(required)
        if not required <= available:
            raise ValueError("A workflow scene is missing from the OBS collection.")
        if "Comparison A-B" in required:
            self._verify_comparison()
        current = listing["currentProgramSceneName"]
        if (
            not isinstance(collection, str)
            or not collection
            or not isinstance(current, str)
            or not current
        ):
            raise ValueError("OBS status is unconfirmed.")
        recording = self._workflow_recording()
        return self.ws, collection, current, recording

    def workflow_switch(self, session, expected, scene, cancel, recording):
        """Run on the serial OBS worker. Never reconnect or retry a live mutation."""
        socket, collection = session
        if cancel.is_set():
            return None
        if self.ws is None or self.ws is not socket:
            raise ValueError("OBS connection changed.")
        if self.call("GetSceneCollectionList")["currentSceneCollectionName"] != collection:
            raise ValueError("The OBS scene collection changed.")
        if scene == "Comparison A-B":
            self._verify_comparison()
        active = self._workflow_recording()
        if recording and not active:
            raise ValueError("Recording was paused or stopped.")
        current = self.call("GetCurrentProgramScene")["currentProgramSceneName"]
        if current != expected:
            raise ValueError("The OBS program scene changed manually.")
        if cancel.is_set():
            return None
        if current != scene:
            self.call("SetCurrentProgramScene", {"sceneName": scene})
        confirmed = self.call("GetCurrentProgramScene")["currentProgramSceneName"]
        if confirmed != scene:
            raise ValueError("OBS did not confirm the workflow scene.")
        return confirmed, active

    @staticmethod
    def _remaining(deadline):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("OBS request deadline exceeded")
        return remaining

    def _send(self, message, deadline):
        self.ws.settimeout(self._remaining(deadline))
        self.ws.send(json.dumps(message))

    def _receive(self, deadline):
        self.ws.settimeout(self._remaining(deadline))
        return json.loads(self.ws.recv())

    def close(self):
        connection, self.ws = self.ws, None
        if connection is not None:
            try:
                connection.settimeout(0.1)
                connection.close(timeout=0)
            except Exception:
                LOGGER.debug("Could not close OBS websocket cleanly", exc_info=True)

    def connect(self, deadline=None):
        deadline = deadline if deadline is not None else time.monotonic() + REQUEST_TIMEOUT
        try:
            config = json.loads(self.config_path.read_text())
            port = config.get("server_port")
            if type(port) is not int or not 1 <= port <= 65535:
                raise ValueError("OBS WebSocket port must be an integer between 1 and 65535")
            self.ws = websocket.create_connection(
                f"ws://127.0.0.1:{port}",
                timeout=self._remaining(deadline),
                http_no_proxy=["127.0.0.1"],
            )
            hello = self._receive(deadline)
            if hello["op"] != 0:
                raise ValueError("OBS did not send a protocol greeting")
            identify = {"rpcVersion": 1, "eventSubscriptions": 0}
            if "authentication" in hello["d"]:
                authentication = hello["d"]["authentication"]
                salted_password = config["server_password"] + authentication["salt"]
                secret = base64.b64encode(
                    hashlib.sha256(salted_password.encode()).digest()
                ).decode()
                challenge = secret + authentication["challenge"]
                identify["authentication"] = base64.b64encode(
                    hashlib.sha256(challenge.encode()).digest()
                ).decode()
            self._send({"op": 1, "d": identify}, deadline)
            if self._receive(deadline)["op"] != 2:
                raise ValueError("OBS authentication failed")
        except Exception as exc:
            self.close()
            raise OBSConnectionError(f"Could not connect to OBS: {exc}") from exc

    def call(self, kind, data=None):
        deadline = time.monotonic() + REQUEST_TIMEOUT
        request_sent = False
        try:
            if self.ws is None:
                self.connect(deadline)
            request_id = str(uuid4())
            # A partial send may already reach OBS, so its outcome is uncertain.
            request_sent = True
            self._send(
                {
                    "op": 6,
                    "d": {"requestType": kind, "requestId": request_id, "requestData": data or {}},
                },
                deadline,
            )
            while True:
                message = self._receive(deadline)
                if message["op"] != 7 or message["d"]["requestId"] != request_id:
                    continue
                result = message["d"]
                request_status = result["requestStatus"]
                if type(request_status.get("result")) is not bool:
                    raise ValueError("Invalid OBS request status")
                if (
                    request_status.get("code") is not None
                    and type(request_status["code"]) is not int
                ):
                    raise ValueError("Invalid OBS request status code")
                if not request_status["result"]:
                    raise OBSRequestError(
                        kind,
                        request_status.get("code"),
                    )
                response = result.get("responseData", {})
                if not isinstance(response, dict):
                    raise ValueError("Invalid OBS response data")
                return response
        except OBSRequestError:
            raise
        except OBSConnectionError:
            self.close()
            raise
        except Exception as exc:
            self.close()
            raise OBSConnectionError(
                f"OBS connection failed during {kind}: {exc}", request_sent=request_sent
            ) from exc

    def status(self):
        result = _empty_status()
        result.update(self.call("GetRecordStatus"))
        result.update(self.call("GetCurrentProgramScene"))

        def optional(label, operation):
            # Once transport fails, avoid a reconnect attempt for every feature.
            if not result["connectionAvailable"]:
                return None
            try:
                return operation()
            except OBSConnectionError as exc:
                result["connectionAvailable"] = False
                result["warnings"].append(f"{label} unavailable: {exc}")
            except (OBSRequestError, KeyError, TypeError, ValueError, OverflowError) as exc:
                result["warnings"].append(f"{label} unavailable: {exc}")
            return None

        def read_blur():
            filters = self.call("GetSourceFilterList", {"sourceName": "Logitech Brio"})["filters"]
            effect = next((item for item in filters if item["filterName"] == FILTER), None)
            if effect is None:
                raise ValueError("Background blur filter not found")
            return {
                "blurAvailable": True,
                "blurEnabled": bool(effect["filterEnabled"]),
                "blurIntensity": int(effect.get("filterSettings", {}).get("blur_background", 3)),
            }

        blur = optional("Background blur", read_blur)
        if blur is not None:
            result.update(blur)
        for name in AUDIO_INPUTS:
            muted = optional(
                name, lambda name=name: self.call("GetInputMute", {"inputName": name})["inputMuted"]
            )
            if muted is not None:
                result["audioMutes"][name] = bool(muted)
                result["audioAvailable"][name] = True

        def read_screen():
            items = self.call("GetSceneItemList", {"sceneName": result["currentProgramSceneName"]})[
                "sceneItems"
            ]
            screens = [item for item in items if item.get("sourceName") == SCREEN_SOURCE]
            if len(screens) != 1:
                return None
            screen = screens[0]
            framing = screen_state(screen["sceneItemTransform"])
            return dict(framing, itemId=screen["sceneItemId"]) if framing else None

        result["screen"] = optional("Screen framing", read_screen)

        def read_vignette(source):
            filters = self.call("GetSourceFilterList", {"sourceName": source})["filters"]
            effect = next((item for item in filters if item["filterName"] == VIGNETTE_FILTER), None)
            if effect is None:
                raise ValueError("Vignette filter not found")
            settings = effect.get("filterSettings", {})
            return {
                "enabled": bool(settings.get("lab_enabled", True)),
                "strength": settings.get(
                    "lab_strength", round(settings.get("opacity", 0.25) * 100)
                ),
            }

        for key, (source, _, label) in VIGNETTES.items():
            effect = optional(f"{label} vignette", lambda source=source: read_vignette(source))
            if effect is not None:
                result["vignettes"][key] = effect
        self._last_status = copy.deepcopy(result)
        return result

    def _after_action(self, name, confirmed=None, response=None, warnings=None):
        """Do not turn a confirmed mutation into a failure of a later read."""
        try:
            result = self.status()
        except OBSError as exc:
            LOGGER.warning("%s succeeded, but OBS status refresh failed", name, exc_info=True)
            result = copy.deepcopy(getattr(self, "_last_status", None) or _empty_status())
            if (
                confirmed
                and confirmed.get("currentProgramSceneName", result["currentProgramSceneName"])
                != result["currentProgramSceneName"]
            ):
                result["screen"] = None
            result.update(confirmed or {})
            result["warnings"] = []
            result["statusFresh"] = False
            result["connectionAvailable"] = not isinstance(exc, OBSConnectionError)
            result["warnings"].append(
                f"{name} succeeded; current status could not be refreshed: {exc}"
            )
        # Keep persistent state separate from one-action metadata such as the
        # saved filename. A later failed refresh must not report an old save.
        self._last_status = copy.deepcopy(result)
        result.update(response or {})
        result["warnings"].extend(warnings or [])
        result["actionSucceeded"] = True
        return result

    def frame_screen(self, scene, item_id, zoom, x, y):
        # Ignore a delayed drag if the operator has already selected another scene.
        if self.call("GetCurrentProgramScene")["currentProgramSceneName"] != scene:
            return
        items = self.call("GetSceneItemList", {"sceneName": scene})["sceneItems"]
        item = next(
            (
                item
                for item in items
                if item["sceneItemId"] == item_id and item.get("sourceName") == SCREEN_SOURCE
            ),
            None,
        )
        if item is None:
            raise OBSRequestError(
                "Screen framing", comment="The screen source changed; choose the scene again"
            )
        crop = screen_crop(item["sceneItemTransform"], zoom, x, y)
        if self.call("GetCurrentProgramScene")["currentProgramSceneName"] != scene:
            return
        self.call(
            "SetSceneItemTransform",
            {
                "sceneName": scene,
                "sceneItemId": item_id,
                "sceneItemTransform": crop,
            },
        )

    def vignette(self, key, enabled, strength):
        source = VIGNETTES[key][0]
        # OFF keeps the intensity, so ON restores it without another saved file.
        self.call(
            "SetSourceFilterSettings",
            {
                "sourceName": source,
                "filterName": VIGNETTE_FILTER,
                "filterSettings": vignette_settings(enabled, strength),
                "overlay": True,
            },
        )

    def apply_titles(self, values, prepared=False):
        if not prepared:
            from lab_titles import prepare_cards

            values = prepare_cards(values, root=self.assets_root, config_path=self.title_config)
        try:
            for source, filename in CARD_SOURCES:
                self.call(
                    "SetInputSettings",
                    {
                        "inputName": source,
                        "inputSettings": {"file": str(self.assets_root / filename)},
                        "overlay": True,
                    },
                )
            for scene in self.call("GetSceneList")["scenes"]:
                items = self.call("GetSceneItemList", {"sceneName": scene["sceneName"]})[
                    "sceneItems"
                ]
                for item in items:
                    if item.get("sourceName") == "VibeWare branding":
                        self.call(
                            "SetSceneItemEnabled",
                            {
                                "sceneName": scene["sceneName"],
                                "sceneItemId": item["sceneItemId"],
                                "sceneItemEnabled": values["vibeware"],
                            },
                        )
            self.call(
                "SetInputMute",
                {"inputName": "Lab intro sound", "inputMuted": not values["sound_enabled"]},
            )
            self.call(
                "SetInputVolume",
                {"inputName": "Lab intro sound", "inputVolumeMul": values["sound_volume"] / 100},
            )
        except OBSError as exc:
            raise ProjectApplyError(
                f"Project saved locally; OBS update incomplete. Retry Apply. {exc}"
            ) from exc
        return self._after_action("Apply project")

    def _comparison_halves(self):
        items = self.call("GetSceneItemList", {"sceneName": "Comparison A-B"})["sceneItems"]
        screens = sorted(
            (item for item in items if item.get("sourceName") == SCREEN_SOURCE),
            key=lambda item: item["sceneItemTransform"]["positionX"],
        )
        if len(screens) != 2:
            raise OBSRequestError("Comparison", comment="The scene needs exactly two screen items")
        halves = []
        for index, item in enumerate(screens):
            width = round(item["sceneItemTransform"].get("sourceWidth", 0))
            if width <= 1:
                raise OBSRequestError("Comparison", comment="The screen capture is not ready")
            split = width // 2
            crop = {
                "cropLeft": 0 if index == 0 else split,
                "cropRight": width - split if index == 0 else 0,
                "cropTop": 0,
                "cropBottom": 0,
            }
            halves.append((item, crop))
        return halves

    def _verify_comparison(self):
        for item, crop in self._comparison_halves():
            transform = item["sceneItemTransform"]
            if any(transform.get(key) != value for key, value in crop.items()):
                raise ValueError(
                    "Select Comparison A/B in Camera & recording to calibrate the split."
                )

    def scene(self, name):
        if name == "Comparison A-B":
            # Validate both halves before sending any mutation to OBS.
            for item, crop in self._comparison_halves():
                self.call(
                    "SetSceneItemTransform",
                    {
                        "sceneName": name,
                        "sceneItemId": item["sceneItemId"],
                        "sceneItemTransform": crop,
                    },
                )
        self.call("SetStudioModeEnabled", {"studioModeEnabled": False})
        self.call("SetCurrentProgramScene", {"sceneName": name})
        return self._after_action("Change scene", {"currentProgramSceneName": name})

    def blur(self, enabled):
        self.call(
            "SetSourceFilterEnabled",
            {"sourceName": "Logitech Brio", "filterName": FILTER, "filterEnabled": enabled},
        )
        return self._after_action("Change background blur", {"blurEnabled": enabled})

    def blur_intensity(self, value):
        # Zero removes the background in this plugin; OFF uses the separate switch.
        value = max(1, min(20, int(value)))
        self.call(
            "SetSourceFilterSettings",
            {
                "sourceName": "Logitech Brio",
                "filterName": FILTER,
                "filterSettings": {"blur_background": value},
                "overlay": True,
            },
        )
        return self._after_action("Change blur intensity", {"blurIntensity": value})

    def record(self, start):
        action = "Start recording" if start else "Stop recording"
        response = self.call("StartRecord" if start else "StopRecord")
        warnings = []
        if start:
            try:
                if self.call("GetCurrentProgramScene")["currentProgramSceneName"] == "Intro":
                    self.call(
                        "TriggerMediaInputAction",
                        {
                            "inputName": "Lab intro sound",
                            "mediaAction": "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART",
                        },
                    )
            except OBSError as exc:
                LOGGER.warning(
                    "Recording started, but Intro sound could not be restarted", exc_info=True
                )
                warnings.append(f"Recording started; Intro sound unavailable: {exc}")
        return self._after_action(
            action, {"outputActive": bool(start), "outputPaused": False}, response, warnings
        )

    def pause(self, paused):
        self.call("PauseRecord" if paused else "ResumeRecord")
        return self._after_action(
            "Pause recording" if paused else "Resume recording",
            {"outputActive": True, "outputPaused": bool(paused)},
        )

    def mute_audio(self, name, muted):
        self.call("SetInputMute", {"inputName": name, "inputMuted": bool(muted)})
        audio_mutes = dict((getattr(self, "_last_status", None) or {}).get("audioMutes", {}))
        audio_mutes[name] = bool(muted)
        return self._after_action("Change audio mute", {"audioMutes": audio_mutes})
