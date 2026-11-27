"""Recognize a successful CI run for exactly the tree being promoted from dev."""
import json
import os
import subprocess


def github(path):
    result = subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def successful_run(repo, head, base, tree, api=github):
    endpoint = f"repos/{repo}"
    prs = api(f"{endpoint}/commits/{head}/pulls?per_page=100")
    for pr in prs:
        source = pr["head"]
        if not (pr["merged_at"] and pr["merge_commit_sha"] == head
                and pr["base"]["ref"] == "main" and source["ref"] == "dev"
                and source["repo"] and source["repo"]["full_name"] == repo):
            continue
        runs = api(f"{endpoint}/actions/workflows/monorepo.yml/runs?event=pull_request"
                   f"&head_sha={source['sha']}&status=success&per_page=100")["workflow_runs"]
        for run in runs:
            if run["head_branch"] != "dev" or run["head_repository"]["full_name"] != repo:
                continue
            jobs = api(f"{endpoint}/actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100")["jobs"]
            for job in jobs:
                if job["name"] == "VibeKit CI required" and job["conclusion"] == "success":
                    if any(step["name"] == f"Verificato PR {pr['number']} {base} {tree}" and step["conclusion"] == "success"
                           for step in job["steps"]):
                        return run["id"]
    return None


if __name__ == "__main__":
    try:
        run_id = successful_run(os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"],
                                os.environ["BASE_SHA"], os.environ["TREE"])
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as exc:
        print(f"Verifica precedente non disponibile. Avvio i controlli. {type(exc).__name__}")
    else:
        if run_id:
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
                print(f"run={run_id}", file=out)
