"""Bounded, deadline-aware transport for the short-lived shared account reader."""
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_BACKEND = ROOT / "backend/OpenAIUsage.Backend.dll"
if not DEFAULT_BACKEND.is_file():
    DEFAULT_BACKEND = ROOT / "Backend/bin/Debug/net10.0/OpenAIUsage.Backend.dll"
BACKEND = Path(os.environ.get("OPENAI_USAGE_BACKEND", str(DEFAULT_BACKEND)))
MAXIMUM_REQUEST_BYTES = 64 * 1024
MAXIMUM_OUTPUT_BYTES = 8 * 1024 * 1024
MAXIMUM_DIAGNOSTIC_BYTES = 1024 * 1024


def exchange(command, request, timeout=45, output_limit=MAXIMUM_OUTPUT_BYTES):
    """Read stdout/stderr incrementally; neither a blocked stdin nor output can evade the deadline."""
    if len(request) > MAXIMUM_REQUEST_BYTES:
        raise RuntimeError("Settings request exceeds the supported size.")
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, start_new_session=True)
    output = bytearray()
    diagnostics = 0
    sent = 0
    deadline = time.monotonic() + timeout
    completed = False
    try:
        with selectors.DefaultSelector() as streams:
            for pipe in (process.stdin, process.stdout, process.stderr):
                os.set_blocking(pipe.fileno(), False)
            streams.register(process.stdout, selectors.EVENT_READ, "output")
            streams.register(process.stderr, selectors.EVENT_READ, "diagnostics")
            if request:
                streams.register(process.stdin, selectors.EVENT_WRITE, "input")
            else:
                process.stdin.close()
            while streams.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise RuntimeError("Account reader timed out. Check Codex and your connection.")
                for ready, _ in streams.select(remaining):
                    pipe, kind = ready.fileobj, ready.data
                    if kind == "input":
                        try:
                            sent += os.write(pipe.fileno(), request[sent:sent + 4096])
                        except BrokenPipeError:
                            sent = len(request)
                        if sent == len(request):
                            streams.unregister(pipe)
                            pipe.close()
                        continue
                    chunk = os.read(pipe.fileno(), 4096)
                    if not chunk:
                        streams.unregister(pipe)
                        pipe.close()
                    elif kind == "output":
                        if len(output) + len(chunk) > output_limit:
                            raise RuntimeError("Account reader response exceeds the supported size.")
                        output.extend(chunk)
                    else:
                        # Do not retain raw diagnostics, which may include private account details.
                        diagnostics += len(chunk)
                        if diagnostics > MAXIMUM_DIAGNOSTIC_BYTES:
                            raise RuntimeError("Account reader diagnostics exceed the supported size.")
            try:
                process.wait(timeout=max(0.001, deadline - time.monotonic()))
                completed = True
            except subprocess.TimeoutExpired:
                raise RuntimeError("Account reader timed out. Check Codex and your connection.") from None
        return output
    finally:
        # The backend is a private process group, including its account-reader children.
        # Always reap it, including malformed payloads and a child retaining a pipe.
        if not completed:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        process.wait()
        for pipe in (process.stdin, process.stdout, process.stderr):
            pipe.close()


def invoke(command="snapshot", value=None):
    if not BACKEND.is_file():
        raise RuntimeError("Shared backend is missing. Build Backend/OpenAIUsage.Backend.csproj or set OPENAI_USAGE_BACKEND.")
    request = json.dumps(value).encode("utf-8") if value is not None else b""
    if len(request) > MAXIMUM_REQUEST_BYTES:
        raise RuntimeError("Settings request exceeds the supported size.")
    native_backend = BACKEND.with_suffix("")
    executable = [str(native_backend)] if native_backend.is_file() else ["dotnet", str(BACKEND)]
    output = exchange([*executable, command], request)
    try:
        result = json.loads(output)
    except (ValueError, UnicodeDecodeError):
        raise RuntimeError("Account reader failed. Verify the .NET 10 runtime is installed.") from None
    if not isinstance(result, dict):
        raise RuntimeError("Account reader returned an invalid response.")
    if "error" in result:
        raise RuntimeError(str(result["error"]))
    return result
