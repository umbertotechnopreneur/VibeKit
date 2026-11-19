#!/usr/bin/env python3
"""Select independent VibeKit pipelines from a complete Git diff."""

import argparse
import json
import os
import subprocess
import sys

PROJECTS = {
    "helpme": "Apps/HelpMe/",
    "markvibe": "Apps/MarkVibe/",
    "thermalright": "Apps/ThermalRight Panel/",
    "ulanzideck": "Apps/UlanziDeck/",
    "voiceducker": "Apps/VoiceDucker/",
    "worktrail": "Apps/WorkTrail/",
    "ghostty-shaders": "Apps/Ghostty Shaders/",
    "openai-usage": "Widgets/OpenAI/",
    "desktop-ripple": "Widgets/WaterRipple/",
    "retro-terminal": "Widgets/RetroTerminal/",
    "studio-controls": "Widgets/StudioControls/",
}


def select_paths(paths):
    """Include both sides of renames and every consumer of shared scripts."""
    selected = set()
    for path in paths:
        if path in {".github/workflows/monorepo.yml", "scripts/ci/select_projects.py"} or (
            path.startswith(("scripts/", ".github/actions/"))
        ):
            return list(PROJECTS)
        if path in {"Widgets/Shared/Build-WindowsMsi.ps1", "Widgets/Shared/create-widget-wix.py"}:
            selected.update({"desktop-ripple", "retro-terminal", "studio-controls"})
            continue
        if path == ".github/workflows/widget-releases.yml":
            selected.update({"openai-usage", "desktop-ripple", "retro-terminal"})
            continue
        if path.startswith("Widgets/Shared/"):
            selected.update({"openai-usage", "desktop-ripple"})
            continue
        for project, prefix in PROJECTS.items():
            if path.startswith(prefix) or path in {
                f".github/workflows/ci-{project}.yml",
                f".github/workflows/{project}-release.yml",
                f".github/workflows/{project}-package-windows.yml",
                f".github/workflows/{project}-store-msix.yml",
                f".github/workflows/{project}-debug-msix.yml",
                f".github/workflows/{project}-codeql.yml",
                f".github/workflows/{project}-dependency-review.yml",
                f".github/workflows/ci-{project}-scripts.yml",
            }:
                # Preserve the existing Markdown-only exclusion for this widget.
                if project != "desktop-ripple" or not path.lower().endswith(".md"):
                    selected.add(project)
    return [project for project in PROJECTS if project in selected]


def changed_projects(base, head, event):
    """Compare the full push or PR; fall back to all projects without a base."""
    if not base or set(base) == {"0"}:
        return list(PROJECTS)
    try:
        # PRs compare with their merge base; pushes include every pushed commit.
        comparison = f"{base}{'...' if event == 'pull_request' else '..'}{head}"
        result = subprocess.run(
            ["git", "diff", "--name-only", "--no-renames", "-z", comparison, "--"],
            check=True, capture_output=True,
        )
    except subprocess.CalledProcessError:
        print("Cannot resolve the change base; checking every project.", file=sys.stderr)
        return list(PROJECTS)
    return select_paths(os.fsdecode(result.stdout).rstrip("\0").split("\0"))


def main():
    """Write a JSON selection for GitHub Actions and local inspection."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", default=os.getenv("EVENT_NAME", "push"))
    parser.add_argument("--base", default=os.getenv("BASE_SHA", ""))
    parser.add_argument("--head", default=os.getenv("HEAD_SHA", "HEAD"))
    parser.add_argument("--project", default=os.getenv("SELECTED_PROJECT", "all"),
                        choices=["all", *PROJECTS])
    args = parser.parse_args()
    if args.event == "workflow_dispatch":
        projects = list(PROJECTS) if args.project == "all" else [args.project]
    else:
        projects = changed_projects(args.base, args.head, args.event)
    result = json.dumps(projects, separators=(",", ":"))
    if output := os.getenv("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"projects={result}\n")
    print(result)


if __name__ == "__main__":
    main()
