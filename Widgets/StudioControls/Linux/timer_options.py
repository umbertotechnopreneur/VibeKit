"""Local presenter timer preferences, separate from the workflow document."""

from dataclasses import asdict, dataclass
import json
from pathlib import Path

from diagnostics import get_logger
from lab_titles import atomic_bytes
from localization import preference_path

LOGGER = get_logger(__name__)


@dataclass(frozen=True)
class TimerOptions:
    size: str = "compact"
    show_next: bool = True
    show_total: bool = True
    show_note: bool = False
    show_obs: bool = True
    lead_in_enabled: bool = True
    end_warning: bool = True
    warning_seconds: int = 5
    free_seconds: int = 180

    def __post_init__(self):
        if self.size not in ("compact", "large"):
            raise ValueError("Invalid timer size")
        for flag in (self.show_next, self.show_total, self.show_note, self.show_obs,
                     self.lead_in_enabled, self.end_warning):
            if type(flag) is not bool:
                raise ValueError("Invalid timer switch")
        for value, limit in ((self.warning_seconds, 60), (self.free_seconds, 86400)):
            if type(value) is not int or not 1 <= value <= limit:
                raise ValueError("Invalid timer duration")

    @classmethod
    def load(cls, path=None):
        source = Path(path or preference_path().with_name("studio-timer.json"))
        try:
            with source.open("rb") as stream:
                raw = stream.read(4097)
            if len(raw) > 4096:
                raise ValueError("Timer preferences too large")
            return cls(**json.loads(raw))
        except FileNotFoundError:
            pass
        except (ValueError, TypeError, OSError):
            LOGGER.warning("Could not load timer preferences; using defaults")
        return cls()

    def save(self, path=None):
        target = Path(path or preference_path().with_name("studio-timer.json"))
        atomic_bytes(target, (json.dumps(asdict(self), indent=2) + "\n").encode())
