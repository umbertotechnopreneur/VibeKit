"""Build each Debian package in its own run and publish completed files atomically."""
from contextlib import contextmanager
import errno
import os
from pathlib import Path
import shutil
import tempfile

FAMILY = Path(__file__).resolve().parents[1]


def copy_extension_components(extension, *, panel=False, capture=False, monitors=False):
    """Ship only the modules this independent extension needs, including transitive imports."""
    modules = ["effect-stop.js", "shell-effects.js", "shell-pressure.js", "monitor-selection.js",
               "ui/metadata.js", "ui/about.js", "ui/about-content.json", "ui/close-choice.js", "ui/preferences-layout.js"]
    if panel:
        modules.extend(("panel-endpoint.js", "ui/product-panel.js"))
    if capture:
        modules.append("capture-pass.js")
    if monitors:
        modules.append("monitor-settings.js")
    for module in modules:
        target = extension / "shared" / module
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(FAMILY / "Shared/Linux" / module, target)
    for name in ("VibeWare.png", "VibeWare-LICENSE.txt"):
        shutil.copy2(FAMILY / "Shared/Assets" / name, extension / name)


@contextmanager
def package_workspace(product, architecture, output, filenames):
    """Keep incomplete builds private and support an output directory on another filesystem."""
    channel = os.environ.get("NOSTALGIA_CHANNEL", "local")
    if channel not in ("local", "public"):
        raise ValueError("NOSTALGIA_CHANNEL must be local or public")
    root = (FAMILY / Path(os.environ.get("NOSTALGIA_ARTIFACTS") or "artifacts").expanduser()).resolve()
    destination = Path(output).resolve() if output else root / "packages" / channel / "linux" / architecture / product
    stages = root / "stage"
    stages.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="build-", dir=stages) as run:
        work = Path(run) / "linux" / architecture / product
        payload, results = work / "package", work / "results"
        payload.mkdir(parents=True)
        results.mkdir()
        scratch = work / "tmp"
        scratch.mkdir()
        previous_tmp = os.environ.get("TMPDIR")
        os.environ["TMPDIR"] = str(scratch)
        try:
            yield payload, results
        finally:
            if previous_tmp is None:
                del os.environ["TMPDIR"]
            else:
                os.environ["TMPDIR"] = previous_tmp
        built_files = [results / name for name in filenames]
        for built in built_files:
            if not built.is_file() or built.stat().st_size == 0:
                raise RuntimeError(f"Build did not produce {built.name} for {product}")
        destination.mkdir(parents=True, exist_ok=True)
        for built in built_files:
            final = destination / built.name
            try:
                os.replace(built, final)
            except OSError as error:
                if error.errno != errno.EXDEV:
                    raise
                with tempfile.NamedTemporaryFile(dir=destination, delete=False) as temporary:
                    pending = Path(temporary.name)
                try:
                    shutil.copy2(built, pending)
                    os.replace(pending, final)
                finally:
                    pending.unlink(missing_ok=True)
            print(final)
