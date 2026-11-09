"""Validate a tagged fork preview before granting it publication artifacts."""

import json
import os
import pathlib
import re
import subprocess


def github(path):
    return json.loads(subprocess.check_output(["gh", "api", path], text=True))


def main():
    config = json.loads(pathlib.Path(".github/fork-release.json").read_text())
    tag = os.environ["GITHUB_REF_NAME"]
    expected = r"v" + re.escape(config["server_version"]) + r"-multidb\.preview\.[1-9][0-9]*"
    if os.environ["GITHUB_REF_TYPE"] != "tag" or not re.fullmatch(expected, tag):
        raise SystemExit("Publish only a versioned multidb preview tag matching the stable server version")

    repository = os.environ["GITHUB_REPOSITORY"]
    if repository != "shea786/jellyfin":
        raise SystemExit("This publishing configuration belongs to shea786/jellyfin")

    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    subprocess.run(["git", "merge-base", "--is-ancestor", config["upstream_commit"], sha], check=True)
    subprocess.run(["git", "merge-base", "--is-ancestor", sha, "origin/multidb/main"], check=True)
    latest = github("repos/jellyfin/jellyfin/releases/latest")
    if latest["tag_name"] != config["upstream_tag"]:
        raise SystemExit("A newer stable Jellyfin release exists; update and qualify the stable baseline first")
    if github("repos/jellyfin/jellyfin/commits/" + config["upstream_tag"])["sha"] != config["upstream_commit"]:
        raise SystemExit("The recorded upstream stable tag and commit do not match")

    versions = re.findall(r'Assembly(?:File)?Version\("([^\"]+)"\)', pathlib.Path("SharedVersion.cs").read_text())
    if versions != [config["server_version"], config["server_version"]]:
        raise SystemExit("The stable assembly versions do not match the release configuration")
    dockerfile = pathlib.Path("docs/database/compose/Dockerfile").read_text()
    if "ARG JELLYFIN_BASE=jellyfin/jellyfin:" + config["upstream_tag"][1:] + "@sha256:" not in dockerfile:
        raise SystemExit("The container base must be the pinned matching stable release")

    checks = github(f"repos/{repository}/commits/{sha}/check-runs?per_page=100")["check_runs"]
    for name in config["required_checks"]:
        matches = [c for c in checks if c["name"] == name and c["app"]["id"] == 15368]
        newest = max(matches, key=lambda c: c["id"], default=None)
        if newest is None or newest["status"] != "completed" or newest["conclusion"] != "success":
            raise SystemExit(f"Required CI check has not passed on the tagged commit: {name}")

    notes = pathlib.Path("docs/releases") / (tag + ".md")
    if not notes.is_file():
        raise SystemExit("The tagged preview needs reviewed release notes in docs/releases")
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
        output.write(f"PREVIEW_TAG={tag}\nIMAGE={config['image']}:{tag[1:]}\nPLATFORM={config['platform']}\n")
    print(f"Validated {tag}, stable baseline {config['upstream_tag']}, source {sha}, and all required CI checks")


if __name__ == "__main__":
    main()
