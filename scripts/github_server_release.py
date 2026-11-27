"""Publish a verified Server OCI archive as an unsigned GitHub Release.

Docker Hub signing and promotion stay outside GitHub.  This helper only owns
the GitHub draft/release and its immutable assets; a later local step can
download the OCI archive, sign the Docker digest, and push it to Docker Hub.
"""

import argparse
import json
import os
from pathlib import Path
import re
import tempfile
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, build_opener

from github_release import GitHub, NoRedirect, read_json, require, sha256


TAG_RE = re.compile(r"^server-v(?P<version>(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2})$")
REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
SCHEMA_VERSION = 1


def parse_tag(tag):
    match = TAG_RE.fullmatch(tag)
    require(match is not None, "Server tag must match server-vX.Y.Z")
    return match.group("version")


def expected_names(version, build):
    require(re.fullmatch(r"\d+\.\d+\.\d+", version), "Invalid Server version")
    require(type(build) is int and 1 <= build <= 65535, "Invalid Server build")
    return {
        f"TermLink-Server-{version}-{build}.oci.tar",
        f"TermLink-Server-{version}-{build}-source.tar.gz",
        "server-release.json",
        "SHA256SUMS",
    }


def sha256sums(path):
    result = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line:
            continue
        parts = line.split(None, 1)
        require(len(parts) == 2 and re.fullmatch(r"[0-9a-fA-F]{64}", parts[0]),
                "SHA256SUMS contains an invalid line")
        name = parts[1].lstrip("*")
        require("/" not in name and "\\" not in name and name not in result,
                "SHA256SUMS contains an unsafe or duplicate filename")
        result[name] = parts[0].lower()
    return result


def validate_bundle(directory, tag, revision):
    directory = Path(directory).resolve()
    version = parse_tag(tag)
    require(REVISION_RE.fullmatch(revision) is not None, "Expected a full source commit")
    metadata_path = directory / "server-release.json"
    metadata = read_json(metadata_path)
    require(metadata.get("schema_version") == SCHEMA_VERSION, "Unsupported Server release metadata schema")
    require(metadata.get("kind") == "termlink-server-release", "Invalid Server release metadata kind")
    require(metadata.get("tag") == tag and metadata.get("version") == version and
            type(metadata.get("build_number")) is int and 1 <= metadata["build_number"] <= 65535,
            "Server release metadata version/tag/build mismatch")
    require(metadata.get("source_revision") == revision, "Server source revision mismatch")
    require(metadata.get("unsigned") is True and metadata.get("local_signing_required") is True,
            "Server Release must be explicitly marked for local signing")
    names = expected_names(version, metadata["build_number"])
    prefix = f"TermLink-Server-{version}-{metadata['build_number']}"
    require(metadata.get("oci_archive") == prefix + ".oci.tar" and
            metadata.get("source_archive") == prefix + "-source.tar.gz", "Unexpected archive names")
    for name in names:
        path = directory / name
        require(path.is_file() and not path.is_symlink(), f"Missing regular Server release asset: {name}")
    sums = sha256sums(directory / "SHA256SUMS")
    require(set(sums) == names - {"SHA256SUMS"}, "SHA256SUMS inventory does not match Server assets")
    for name, digest in sums.items():
        actual = sha256(directory / name)
        require(actual == digest, f"SHA256SUMS digest mismatch: {name}")
    require(metadata.get("oci_archive_sha256") == sums[metadata["oci_archive"]],
            "OCI archive digest is not bound in server-release.json")
    require(metadata.get("source_archive_sha256") == sums[metadata["source_archive"]],
            "Source archive digest is not bound in server-release.json")
    return metadata, names


def verify_tag(api, tag, revision):
    api.require_tag(tag, revision)


def release_assets(api, release_id):
    assets = api.request(f"/releases/{release_id}/assets?per_page=100")
    names = [asset.get("name") for asset in assets]
    require(len(names) == len(set(names)), "Release contains duplicate asset names")
    return {asset["name"]: asset for asset in assets}


def is_signature_bundle(name):
    return bool(re.fullmatch(r".+\.(?:sig|bundle|sigstore\.json)", name))


def validate_server_asset(asset, path, name):
    require(asset.get("state") == "uploaded",
            f"GitHub asset is not complete: {name}; preserve the draft and retry later")
    require(asset.get("size") == path.stat().st_size,
            f"GitHub asset size differs; refusing replacement: {name}")


def verify_existing_assets(api, release, directory, names):
    assets = release_assets(api, release["id"])
    require(names <= set(assets), "Existing Server Release is missing an attachment")
    extras = set(assets) - names
    require(all(is_signature_bundle(name) for name in extras),
            "Existing Server Release contains an unexpected non-signature asset")
    with tempfile.TemporaryDirectory(prefix="termlink-server-release-") as temporary:
        for name in names:
            asset = assets[name]
            validate_server_asset(asset, Path(directory) / name, name)
            expected = sha256(Path(directory) / name)
            digest = asset.get("digest")
            if digest == "sha256:" + expected:
                continue
            downloaded = Path(temporary) / name
            api.download(asset["id"], downloaded)
            require(sha256(downloaded) == expected,
                    f"Existing GitHub asset differs from the local immutable file: {name}")
    return assets


def upload_assets(api, release, directory, names):
    assets = release_assets(api, release["id"])
    extras = set(assets) - names
    require(all(is_signature_bundle(name) for name in extras),
            "Existing draft contains an unexpected non-signature asset; preserve it and inspect manually")
    for name in sorted(names):
        path = Path(directory) / name
        if name in assets:
            validate_server_asset(assets[name], path, name)
            expected = sha256(path)
            digest = assets[name].get("digest")
            if digest == "sha256:" + expected:
                continue
            with tempfile.TemporaryDirectory(prefix="termlink-server-existing-") as temporary:
                downloaded = Path(temporary) / name
                api.download(assets[name]["id"], downloaded)
                require(sha256(downloaded) == expected,
                        f"Existing draft asset differs; refusing replacement: {name}")
            continue
        upload_url = release["upload_url"].split("{", 1)[0] + "?" + urlencode({"name": name})
        require(upload_url.startswith("https://uploads.github.com/repos/"), "Unexpected GitHub upload host")
        with path.open("rb") as stream:
            request = Request(upload_url, data=stream, method="POST", headers={
                "Authorization": "Bearer " + api.token,
                "Content-Type": "application/octet-stream",
                "Content-Length": str(path.stat().st_size),
                "User-Agent": "TermLink-Server-Release",
            })
            try:
                with build_opener(NoRedirect).open(request, timeout=1800) as response:
                    uploaded = json.loads(response.read())
            except HTTPError as error:
                raise RuntimeError(f"GitHub asset upload failed for {name}: HTTP {error.code}") from None
        require(uploaded.get("digest") == "sha256:" + sha256(path),
                f"Uploaded GitHub asset digest mismatch: {name}")
    return release_assets(api, release["id"])


def release_body(notes_path, metadata):
    notes = Path(notes_path).read_text(encoding="utf-8") if notes_path else (
        f"Server {metadata['version']} / {metadata['build_number']} · Linux amd64 / arm64\n\n"
        f"Source commit `{metadata['source_revision']}` · OCI index `{metadata['index_digest']}`\n\n"
        "Download the OCI archive and corresponding source below. Verify both using `SHA256SUMS`."
    )
    marker = "\n\n> This GitHub Release is an unsigned Server archive. Sign the OCI digest locally before Docker Hub promotion."
    return notes.rstrip() + marker


def ensure_draft(args, api):
    version = parse_tag(args.tag)
    metadata, names = validate_bundle(args.directory, args.tag, args.revision)
    require(metadata["version"] == version, "Server tag and release metadata version differ")
    verify_tag(api, args.tag, args.revision)
    body = release_body(args.notes, metadata)
    releases = api.request("/releases?per_page=100")
    existing = [item for item in releases if item.get("tag_name") == args.tag]
    require(len(existing) <= 1, "Ambiguous Server Release identity")
    if existing:
        release = existing[0]
        require(release.get("target_commitish") == args.revision,
                "Existing Server Release points at a different commit")
        require(release.get("body") == body, "Existing Server Release notes differ")
        if not release.get("draft"):
            verify_existing_assets(api, release, args.directory, names)
            return release, names, "already_published"
    else:
        release = api.request("/releases", "POST", {
            "tag_name": args.tag,
            "target_commitish": args.revision,
            "name": f"TermLink Server {version} (build {metadata['build_number']})",
            "body": body,
            "draft": True,
            "prerelease": False,
            "make_latest": "false",
        })
    upload_assets(api, release, args.directory, names)
    return release, names, "draft"


def stage(args, api):
    release, names, status = ensure_draft(args, api)
    print(json.dumps({"status": status, "release_id": release["id"], "tag": args.tag,
                      "revision": args.revision, "asset_names": sorted(names),
                      "unsigned": True, "local_signing_required": True}))


def publish(args, api):
    metadata, names = validate_bundle(args.directory, args.tag, args.revision)
    if args.release_id:
        verify_tag(api, args.tag, args.revision)
        release = api.request(f"/releases/{args.release_id}")
    else:
        release, names, status = ensure_draft(args, api)
        if status == "already_published":
            print(json.dumps({"status": status, "release_id": release["id"], "tag": args.tag,
                              "revision": args.revision, "unsigned": True}))
            return
    require(release.get("tag_name") == args.tag and release.get("target_commitish") == args.revision,
            "Server Release identity changed")
    require(release.get("body") == release_body(args.notes, metadata), "Server Release notes changed")
    verify_existing_assets(api, release, args.directory, names)
    if not release.get("draft"):
        print(json.dumps({"status": "already_published", "release_id": release["id"],
                          "tag": args.tag, "revision": args.revision, "unsigned": True}))
        return
    verify_tag(api, args.tag, args.revision)
    updated = api.request(f"/releases/{release['id']}", "PATCH", {
        "draft": False,
        "prerelease": False,
        "make_latest": "false",
        "tag_name": args.tag,
        "target_commitish": args.revision,
    })
    require(not updated.get("draft") and updated.get("tag_name") == args.tag and
            updated.get("target_commitish") == args.revision, "Server Release did not publish cleanly")
    print(json.dumps({"status": "published", "release_id": updated["id"], "url": updated.get("html_url"),
                      "tag": args.tag, "revision": args.revision, "unsigned": True,
                      "local_signing_required": True}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("stage", "publish"))
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", "myucxy/TermLink"))
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--notes", type=Path)
    parser.add_argument("--release-id", type=int,
                        help="Existing draft ID; omit to stage and publish in one idempotent run")
    args = parser.parse_args()
    require(args.notes is None or args.notes.is_file(), "Release notes file is missing")
    require(args.directory.is_dir(), "Release asset directory is required")
    if args.release_id:
        require(args.release_id > 0, "Release ID must be positive")
    if args.phase == "stage":
        stage(args, GitHub(args.repository))
    else:
        publish(args, GitHub(args.repository))


if __name__ == "__main__":
    main()
