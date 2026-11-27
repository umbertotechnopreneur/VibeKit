"""Prepara una release VibeKit dai pacchetti verificati di un singolo sottoprogetto."""

import argparse
import base64
import fnmatch
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path, PurePosixPath
from urllib.parse import quote, urlencode

REPO = "umbertotechnopreneur/VibeKit"
VERS = re.compile(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2,3}")
CATALOG = Path(__file__).with_name("projects.json")


class ReleaseError(Exception):
    """Errore operativo con cui interrompere la pubblicazione."""


def gh(*args, output=None, data=None):
    result = subprocess.run(["gh", *map(str, args)], input=data,
                            stdout=subprocess.PIPE if output is None else output,
                            stderr=subprocess.PIPE)
    if result.returncode:
        raise ReleaseError(result.stderr.decode(errors="replace").strip() or
                           "GitHub CLI ha interrotto il comando.")
    return result.stdout.decode("utf-8").strip() if output is None else None


def api(path):
    return json.loads(gh("api", f"repos/{REPO}/{path}"))


def pages(path, field=None):
    separator = "&" if "?" in path else "?"
    for page in range(1, 1001):
        result = api(f"{path}{separator}per_page=100&page={page}")
        values = result[field] if field else result
        yield from values
        if len(values) < 100:
            return
    raise ReleaseError("Troppe pagine nella risposta di GitHub.")


def source_version(project, sha):
    path = project["version_file"]
    if not path:
        return None
    data = api(f"contents/{quote(path)}?ref={sha}")
    content = base64.b64decode(data["content"]).decode("utf-8-sig")
    matches = re.findall(project["version_pattern"], content, re.MULTILINE)
    if len(set(matches)) != 1 or not VERS.fullmatch(matches[0]):
        raise ReleaseError(f"Versione assente o ambigua in {path} al commit {sha}.")
    return matches[0]


def choose_version(slug, source, requested, channel):
    if requested and not VERS.fullmatch(requested):
        raise ReleaseError("Usa una versione numerica a tre o quattro componenti.")
    if slug == "markvibe":
        if not requested or len(requested.split(".")) != 4:
            raise ReleaseError("MarkVibe richiede --version con quattro componenti.")
        old, new = (tuple(map(int, value.split("."))) for value in (source, requested))
        if new <= old or max(new) > 65535:
            raise ReleaseError(f"La versione di MarkVibe deve superare {source} e rientrare nei limiti MSIX.")
    elif requested and source and requested != source:
        raise ReleaseError(f"La versione nel sorgente è {source}, hai richiesto {requested}.")
    version = requested or source
    if channel == "stable" and not version:
        raise ReleaseError("Il progetto non dichiara una versione: specifica --version.")
    if slug == "worktrail" and channel == "stable" and version.split(".")[0] == "0":
        raise ReleaseError("Il packaging stabile WorkTrail richiede una versione sorgente con major almeno 1.")
    return version


def trusted_run(run, workflow, branch, sha, allow_monorepo):
    paths = {f".github/workflows/{workflow}"}
    if allow_monorepo:
        paths.add(".github/workflows/monorepo.yml")
    return (run["head_repository"]["full_name"] == REPO and run["repository"]["full_name"] == REPO
            and run["event"] in {"push", "workflow_dispatch"} and run["head_branch"] == branch
            and run["head_sha"] == sha and run["path"] in paths and run["conclusion"] == "success")


def select_artifacts(candidates, specs, run_id, sha):
    available = [a for a in candidates if not a["expired"] and a["workflow_run"]["id"] == run_id
                 and a["workflow_run"]["head_sha"] == sha
                 and re.fullmatch(r"sha256:[0-9a-f]{64}", a.get("digest") or "")]
    selected = {}
    for spec in specs:
        hits = [a for a in available if fnmatch.fnmatchcase(a["name"], spec["artifact"])]
        if not hits and spec.get("required", True):
            return {}
        for artifact in hits:
            selected[artifact["id"]] = artifact
    return selected


def get_build(project, specs, branch, sha, inputs, run_id):
    workflow = project["workflow"]
    allow_monorepo = not inputs
    def artifacts(run):
        if not trusted_run(run, workflow, branch, sha, allow_monorepo):
            return {}
        items = pages(f"actions/runs/{run['id']}/artifacts", "artifacts")
        return select_artifacts(items, specs, run["id"], sha)

    if run_id:
        run = api(f"actions/runs/{run_id}")
        found = artifacts(run)
        if not found:
            raise ReleaseError("Il run indicato non soddisfa origine, branch, SHA, workflow, esito o artefatti richiesti.")
        return run_id, found
    query = urlencode({"head_sha":sha, "branch":branch, "status":"success"})
    for run in pages(f"actions/runs?{query}", "workflow_runs"):
        found = artifacts(run)
        if found:
            return run["id"], found
    # gh returns the specific run URL on supported GitHub versions. Never select the latest run by date.
    url = gh("workflow", "run", workflow, "-R", REPO, "--ref", branch, "--json",
             data=json.dumps(inputs).encode("utf-8"))
    match = re.search(rf"https://github\.com/{re.escape(REPO)}/actions/runs/(\d+)", url)
    if not match:
        raise ReleaseError("Build richiesta. GitHub non ha restituito l'ID: riprendi con --run-id dopo il completamento.")
    run_id = int(match[1])
    print(f"Build avviata: {match[0]}", flush=True)
    gh("run", "watch", run_id, "-R", REPO, "--exit-status", "--interval", 15)
    run = api(f"actions/runs/{run_id}")
    found = artifacts(run)
    if not found:
        raise ReleaseError("La build terminata non corrisponde al commit o ai pacchetti richiesti.")
    return run_id, found


def digest(path):
    with path.open("rb") as stream:
        return "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()


def extract_packages(archive, artifact_name, specs, dest, names):
    # Copy only explicitly selected regular files to controlled leaf names. Never unpack an artifact tree.
    hits = set()
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            if item.is_dir():
                continue
            name = PurePosixPath(item.filename).name
            matched = {i for i, spec in enumerate(specs)
                       if fnmatch.fnmatchcase(artifact_name, spec["artifact"])
                       and fnmatch.fnmatchcase(name, spec["file"])}
            if not matched:
                continue
            member = PurePosixPath(item.filename)
            if (member.is_absolute() or ".." in member.parts or "\\" in item.filename
                    or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]+", name) or name.casefold() in names
                    or not name.lower().endswith((".msi", ".zip", ".deb", ".tar.gz", ".msix", ".msixbundle"))
                    or (item.external_attr >> 16) & 0o170000 == 0o120000):
                raise ReleaseError(f"Nome di pacchetto duplicato o non valido: {name}")
            path = dest/name
            with bundle.open(item) as src, path.open("xb") as out:
                shutil.copyfileobj(src, out)
            if not path.stat().st_size:
                raise ReleaseError(f"Pacchetto vuoto: {name}")
            headers = {".msi": bytes.fromhex("d0cf11e0a1b11ae1"), ".deb": b"!<arch>\n", ".gz": b"\x1f\x8b"}
            if signature := headers.get(path.suffix.lower()):
                with path.open("rb") as payload:
                    if payload.read(len(signature)) != signature:
                        raise ReleaseError(f"Formato del pacchetto non valido: {name}")
            if name.lower().endswith(".zip"):
                with zipfile.ZipFile(path) as inner:
                    if not inner.namelist() or inner.testzip():
                        raise ReleaseError(f"Archivio non valido: {name}")
            names.add(name.casefold())
            hits.update(matched)
    return hits


def stage(artifacts, specs, directory):
    dest = directory/"pacchetti"
    dest.mkdir()
    names, matched = set(), set()
    for a in artifacts.values():
        archive = directory/f"{a['id']}.zip"
        with archive.open("wb") as stream:
            gh("api", f"repos/{REPO}/actions/artifacts/{a['id']}/zip", output=stream)
        if digest(archive) != a["digest"]:
            raise ReleaseError(f"SHA-256 dell'artefatto non valido: {a['name']}")
        matched.update(extract_packages(archive, a["name"], specs, dest, names))
        archive.unlink()
    if any(i not in matched and s.get("required",True) for i,s in enumerate(specs)) or not names:
        raise ReleaseError("Mancano pacchetti richiesti nel contenuto degli artefatti.")
    checksums = {p.name:digest(p) for p in sorted(dest.iterdir())}
    sums = dest/"SHA256SUMS.txt"
    sums.write_text("".join(f"{value[7:]}  {name}\n" for name,value in checksums.items()), encoding="ascii")
    checksums[sums.name] = digest(sums)
    return dest, checksums


def publish(tag, sha, run_id, title, channel, dest, checksums, links=()):
    notes = f"Pacchetti dal commit `{sha}`. [Build verificata](https://github.com/{REPO}/actions/runs/{run_id}).\n"
    for label, artifact_id in links:
        notes += f"\n[{label}](https://github.com/{REPO}/actions/runs/{run_id}/artifacts/{artifact_id})\n"
    release = next((r for r in pages("releases") if r["tag_name"] == tag), None)
    # A release draft may reserve a name before the corresponding ref exists.
    refs = api("git/matching-refs/tags/"+quote(tag))
    ref = next((ref for ref in refs if ref["ref"] == f"refs/tags/{tag}"), None)
    if ref and (ref["object"]["type"] != "commit" or ref["object"]["sha"] != sha):
        raise ReleaseError(f"Il tag {tag} esiste già su un'altra sorgente.")
    if ref and not release:
        raise ReleaseError("Tag preesistente senza release associata. Verifica la sua origine.")
    if not release:
        result = gh("api","--method","POST",f"repos/{REPO}/releases","--input","-",
                    data=json.dumps({"tag_name":tag,"target_commitish":sha,"name":title,
                                     "body":notes,"draft":True,"prerelease":channel=="test",
                                     "make_latest":"false"}).encode())
        release = json.loads(result)
    if (release["target_commitish"] != sha or release.get("prerelease") != (channel=="test")
            or notes.strip() != release.get("body","").strip()):
        raise ReleaseError("La release esistente proviene da un altro commit, canale o run.")
    existing = {a["name"]:a["digest"] for a in release["assets"]}
    if any(checksums.get(name) != value for name,value in existing.items()):
        raise ReleaseError("Gli asset caricati non corrispondono. Nessun file esistente è stato sostituito.")
    if not release["draft"]:
        if existing != checksums or not ref:
            raise ReleaseError("La release pubblicata ha un insieme di asset o un tag diverso dal previsto.")
        return release["html_url"]
    missing = sorted(checksums.keys() - existing.keys())
    if missing:
        gh("release","upload",tag,*[dest/name for name in missing],"-R",REPO)
    verified = api(f"releases/{release['id']}")
    if {a["name"]:a["digest"] for a in verified["assets"] if a["state"]=="uploaded"} != checksums:
        raise ReleaseError("Verifica degli asset remoti fallita. La release è ancora in bozza.")
    if not ref:
        gh("api","--method","POST",f"repos/{REPO}/git/refs","-f",f"ref=refs/tags/{tag}","-f",f"sha={sha}")
    if api("git/ref/tags/"+quote(tag))["object"]["sha"] != sha:
        raise ReleaseError("Il tag non punta al commit verificato.")
    final = json.loads(gh("api","--method","PATCH",f"repos/{REPO}/releases/{release['id']}",
                          "--input","-",data=b'{"draft":false,"make_latest":"false"}'))
    if final["draft"]:
        raise ReleaseError("La release risulta ancora in bozza.")
    return final["html_url"]


def main():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8-sig"))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", choices=sorted(catalog), help="sottoprogetto")
    parser.add_argument("--channel", required=True, choices=["dev", "stable", "test"],
                        help="dev (alias test) per le prerelease, stable per le stabili")
    parser.add_argument("--version", help="versione del pacchetto")
    parser.add_argument("--run-id", type=int, help="ID di una build già riuscita")
    parser.add_argument("--dry-run", action="store_true", help="mostra il piano senza avviare build o pubblicare")
    args = parser.parse_args()
    if args.channel == "dev":
        args.channel = "test"
    project = catalog[args.project]
    if args.channel not in project.get("channels", ["test", "stable"]):
        raise ReleaseError(f"Il packaging di {args.project} non supporta il canale {args.channel}.")
    branch = {"test":"dev", "stable":"main"}[args.channel]
    sha = api(f"branches/{branch}")["commit"]["sha"]
    source = source_version(project, sha)
    version = choose_version(args.project, source, args.version, args.channel)
    version_tag = f"v{version}" if version else ""
    if args.channel == "test":
        version_tag = f"{version_tag + '-' if version_tag else ''}test.{sha[:12]}"
    tag = f"release/{args.project}/{version_tag}"
    params = {"version":version or "", "channel":args.channel, "project":args.project,
              "configuration":"Release" if args.channel=="stable" else "Debug"}
    inputs = {k:v.format(**params) for k,v in project["inputs"].items()}
    specs = [{**s, "artifact":s["artifact"].format(**params), "file":s["file"].format(**params)}
             for s in project["assets"]]
    linked = project.get("linked_artifacts", [])
    print(f"{args.project} • {branch} • {sha}\nTag: {tag}\nWorkflow: {project['workflow']}", flush=True)
    if args.dry_run:
        for spec in specs:
            print(f"  {spec['artifact']} / {spec['file']}")
        for link in linked:
            print(f"  Collegamento al download autenticato: {link['artifact']}")
        return
    run_id, artifacts = get_build(project, specs + linked, branch, sha, inputs, args.run_id)
    package_artifacts = {i:a for i,a in artifacts.items() if any(
        fnmatch.fnmatchcase(a["name"], s["artifact"]) for s in specs)}
    links = [(link["title"], i) for link in linked for i,a in artifacts.items() if a["name"] == link["artifact"]]
    print(f"Uso la build https://github.com/{REPO}/actions/runs/{run_id}", flush=True)
    with tempfile.TemporaryDirectory(prefix="vibekit-release-") as temp:
        dest, sums = stage(package_artifacts, specs, Path(temp))
        print(publish(tag, sha, run_id, f"{args.project} {version or sha[:12]} ({args.channel})",
                      args.channel, dest, sums, links))


if __name__ == "__main__":
    try:
        main()
    except (ReleaseError, OSError, ValueError, zipfile.BadZipFile) as exc:
        sys.exit(f"Release interrotta: {exc}")
