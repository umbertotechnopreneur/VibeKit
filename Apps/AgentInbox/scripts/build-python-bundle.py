# VBWR B
#
# Project: AgentInbox
# Repository: https://github.com/umbertotechnopreneur/VibeKit
# Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
#
# VibeWare: Human intent, AI execution, and plenty of tokens
# Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
#
# File: Package tracked Python source and agent setup instructions in a portable ZIP.
# Created with AI: OpenAI Codex, 2026-11-16.
# Human guidance: Umberto Giacobbi; hand off setup to an agent and restore credentials separately.
#
# Copyright (c) 2026 Umberto Giacobbi
# License: MIT - see LICENSE
# SPDX-License-Identifier: MIT
#
# VBWR E

"""Create a Python source ZIP that an agent can install with a separate account backup."""

import argparse
import re
import subprocess
import sys
import zipfile
from pathlib import Path


def bundle_sources(repository):
    """Select versioned engine modules and explicit setup resources without reading a profile.

    Args:
        repository: Absolute AgentInbox application directory, including in a monorepo.
    """
    resources = {
        "python/run.py", "scripts/export-plugin.py", "LICENSE", "THIRD_PARTY_NOTICES.md",
        "docs/CLI_REFERENCE.md", "docs/PRIVACY.md", "docs/PYTHON_AGENT_SETUP.md",
        "integrations/codex/plugins/agentinbox/plugin.json",
        "integrations/codex/plugins/agentinbox/mcp.json",
        "integrations/codex/.agents/plugins/marketplace.json",
    }
    result = subprocess.run(
        ["git", "-C", str(repository), "ls-files", "--cached", "-z", "--",
         "python/agentinbox", *sorted(resources)],
        check=True, stdout=subprocess.PIPE,
    )
    tracked = set(result.stdout.decode("utf-8").rstrip("\0").split("\0"))
    required = resources | {"python/agentinbox/__init__.py", "python/agentinbox/__main__.py",
                            "python/agentinbox/backup.py", "python/agentinbox/cli.py"}
    if missing := required - tracked:
        raise ValueError("Required source files must be tracked in Git: " + ", ".join(sorted(missing)))
    modules = {name for name in tracked
               if Path(name).parent == Path("python/agentinbox") and Path(name).suffix == ".py"}
    selected = {}
    for name in sorted(resources | modules):
        path = repository / name
        if not path.is_file() or path.is_symlink() or not path.resolve().is_relative_to(repository):
            raise ValueError(f"Source must be a regular file inside AgentInbox: {name}")
        archive_name = "AGENTS.md" if name == "docs/PYTHON_AGENT_SETUP.md" else name
        selected[archive_name] = path.read_bytes()
    return selected


def main():
    """Write a new portable ZIP under AgentInbox artifacts using the current source version."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", help="New .zip filename under AgentInbox artifacts, relative to the application directory.")
    options = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    sources = bundle_sources(repository)
    match = re.search(r'^__version__ = "([0-9]+\.[0-9]+\.[0-9]+)"\r?$',
                      sources["python/agentinbox/__init__.py"].decode("utf-8"), re.MULTILINE)
    if not match:
        parser.error("The engine must declare a three-part source version.")
    version = match.group(1)
    output = repository / (options.output or f"artifacts/python-bundle/agentinbox-python-{version}.zip")
    artifacts = repository / "artifacts"
    if (output.suffix.lower() != ".zip" or not output.resolve().is_relative_to(artifacts)
            or artifacts.is_symlink() or output.exists() or output.is_symlink()):
        parser.error("Choose a new .zip filename inside this application's regular artifacts directory.")
    for parent in output.parents:
        if parent == repository:
            break
        if parent.is_symlink():
            parser.error("Output directories must not be symbolic links.")

    sources["README.md"] = (
        "# AgentInbox Python handoff\n\n"
        "I needed to connect my existing mailboxes to an AI agent on another computer. "
        "I packaged the Python engine and setup instructions so you can ask your agent to install it.\n\n"
        "First, open Accounts in the full AgentInbox app and choose Export accounts. "
        "Save that encrypted backup separately and keep its passphrase private.\n\n"
        "Extract this ZIP into a permanent local directory outside a Git checkout. "
        "Ask Codex to read AGENTS.md and complete setup. Give it the local path to your backup when requested. "
        "Enter the passphrase yourself in the terminal's hidden prompt.\n\n"
        "Connect Gmail, Microsoft, iCloud, GoDaddy and other compatible IMAP/POP3 mailboxes "
        "through one MCP connection. You can share dozens of IMAP folders across accounts, "
        "with up to twenty selected folders per account. POP3 exposes one INBOX.\n\n"
        f"Python engine version {version}. Requires Python 3.11 or newer and native protected credential storage. "
        "Microsoft accounts require a new sign-in after restore.\n\n"
        "I'm Umberto Giacobbi. I built and maintain AgentInbox, with help from contributors.\n"
    ).encode("utf-8")
    sources[".gitignore"] = (
        "artifacts/\n.local/\n.venv/\n__pycache__/\n*.py[cod]\n*.backup\n"
        "accounts.json\nprotected/\n*.db\n*.sqlite*\n.env\n.env.*\ndesktop.ini\n"
    ).encode("utf-8")
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with output.open("xb") as stream:
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for name, content in sorted(sources.items()):
                    archive.writestr(name, content)
    except Exception:
        sys.stderr.write("Bundle creation failed. Any partial ZIP must not be installed.\n")
        raise
    print(output)
    print(f"Packaged {len(sources)} source and setup files. Export the encrypted account backup separately.")


if __name__ == "__main__":
    main()
