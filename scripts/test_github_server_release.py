"""Offline trust-boundary tests for github_server_release.py."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import github_server_release as release


REVISION = "a" * 40
TAG = "server-v0.3.19"


def make_bundle(directory):
    version = "0.3.19"
    build = 149
    names = release.expected_names(version, build)
    contents = {
        f"TermLink-Server-{version}-{build}.oci.tar": b"oci archive",
        f"TermLink-Server-{version}-{build}-source.tar.gz": b"source archive",
    }
    for name, data in contents.items():
        (directory / name).write_bytes(data)
    checks = {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()}
    metadata = {
        "schema_version": 1,
        "kind": "termlink-server-release",
        "tag": TAG,
        "version": version,
        "build_number": build,
        "source_revision": REVISION,
        "oci_archive": f"TermLink-Server-{version}-{build}.oci.tar",
        "source_archive": f"TermLink-Server-{version}-{build}-source.tar.gz",
        "oci_archive_sha256": checks[f"TermLink-Server-{version}-{build}.oci.tar"],
        "source_archive_sha256": checks[f"TermLink-Server-{version}-{build}-source.tar.gz"],
        "unsigned": True,
        "local_signing_required": True,
    }
    (directory / "server-release.json").write_text(json.dumps(metadata), encoding="utf-8")
    checks["server-release.json"] = release.sha256(directory / "server-release.json")
    (directory / "SHA256SUMS").write_text(
        "".join(f"{checks[name]}  {name}\n" for name in sorted(checks)), encoding="utf-8"
    )
    return metadata, names


class FakeApi:
    def __init__(self, release_record=None):
        self.release_record = release_record
        self.calls = []

    def tag_revision(self, tag):
        self.calls.append(("tag", tag))
        return REVISION

    def require_tag(self, tag, revision):
        self.calls.append(("require_tag", tag, revision))
        if self.tag_revision(tag) != revision:
            raise ValueError("tag mismatch")

    def request(self, path, method="GET", value=None, data=None, content_type="application/json"):
        self.calls.append((method, path, value))
        if path == "/releases?per_page=100":
            return [self.release_record] if self.release_record else []
        if path.startswith("/releases/") and path.endswith("/assets?per_page=100"):
            return []
        if method == "POST" and path == "/releases":
            self.release_record = dict(id=17, tag_name=value["tag_name"],
                                       target_commitish=value["target_commitish"],
                                       body=value["body"], draft=True, prerelease=False,
                                       upload_url="https://uploads.github.com/repos/example/release/assets{?name}")
            return self.release_record
        if method == "GET" and path == "/releases/17":
            return self.release_record
        if method == "PATCH":
            self.release_record.update(value)
            return self.release_record
        raise AssertionError(f"unexpected API call: {method} {path}")


class ServerReleaseChecks(unittest.TestCase):
    def test_validate_bundle_requires_unsigned_local_signing_marker(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            metadata, _ = make_bundle(directory)
            self.assertEqual(release.validate_bundle(directory, TAG, REVISION)[0], metadata)
            metadata["unsigned"] = False
            (directory / "server-release.json").write_text(json.dumps(metadata), encoding="utf-8")
            with self.assertRaises(ValueError):
                release.validate_bundle(directory, TAG, REVISION)

    def test_stage_creates_unsigned_draft_without_network_upload_helper(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            make_bundle(directory)
            notes = directory / "notes.md"
            notes.write_text("Server release notes", encoding="utf-8")
            api = FakeApi()
            args = argparse.Namespace(directory=directory, tag=TAG, revision=REVISION, notes=notes)
            with patch.object(release, "upload_assets", return_value={name: {} for name in release.expected_names("0.3.19", 149)}), redirect_stdout(io.StringIO()) as output:
                release.stage(args, api)
            result = json.loads(output.getvalue())
            self.assertEqual(result["status"], "draft")
            create = next(call for call in api.calls if call[0] == "POST")
            self.assertEqual(create[2]["make_latest"], "false")
            self.assertIn("unsigned Server archive", create[2]["body"])

    def test_published_release_is_validated_and_not_rewritten(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            make_bundle(directory)
            notes = directory / "notes.md"
            notes.write_text("Server release notes", encoding="utf-8")
            body = notes.read_text() + "\n\n> This GitHub Release is an unsigned Server archive. Sign the OCI digest locally before Docker Hub promotion."
            record = dict(id=17, tag_name=TAG, target_commitish=REVISION, body=body,
                          draft=False, prerelease=False)
            api = FakeApi(record)
            args = argparse.Namespace(directory=directory, tag=TAG, revision=REVISION, notes=notes)
            names = release.expected_names("0.3.19", 149)
            with patch.object(release, "verify_existing_assets", return_value={name: {} for name in names}), redirect_stdout(io.StringIO()) as output:
                release.stage(args, api)
            self.assertEqual(json.loads(output.getvalue())["status"], "already_published")
            self.assertFalse(any(call[0] == "PATCH" for call in api.calls))

    def test_tag_mismatch_stops_before_release_lookup(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            make_bundle(directory)
            api = FakeApi()
            api.tag_revision = lambda tag: "b" * 40
            args = argparse.Namespace(directory=directory, tag=TAG, revision=REVISION, notes=directory / "notes.md")
            args.notes.write_text("notes", encoding="utf-8")
            with self.assertRaises(ValueError):
                release.stage(args, api)
            self.assertFalse(any(call[0] == "/releases?per_page=100" for call in api.calls))

    def test_publish_keeps_latest_false(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            make_bundle(directory)
            notes = directory / "notes.md"
            notes.write_text("Server release notes", encoding="utf-8")
            body = notes.read_text() + "\n\n> This GitHub Release is an unsigned Server archive. Sign the OCI digest locally before Docker Hub promotion."
            record = dict(id=17, tag_name=TAG, target_commitish=REVISION, body=body,
                          draft=True, prerelease=False)
            api = FakeApi(record)
            args = argparse.Namespace(directory=directory, tag=TAG, revision=REVISION, notes=notes, release_id=17)
            names = release.expected_names("0.3.19", 149)
            with patch.object(release, "verify_existing_assets", return_value={name: {} for name in names}), redirect_stdout(io.StringIO()) as output:
                release.publish(args, api)
            self.assertEqual(json.loads(output.getvalue())["status"], "published")
            patch_call = next(call for call in api.calls if call[0] == "PATCH")
            self.assertEqual(patch_call[2]["make_latest"], "false")

    def test_publish_without_release_id_is_single_command(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            make_bundle(directory)
            notes = directory / "notes.md"
            notes.write_text("Server release notes", encoding="utf-8")
            record = dict(id=17, tag_name=TAG, target_commitish=REVISION, draft=True, prerelease=False,
                          body=notes.read_text() + "\n\n> This GitHub Release is an unsigned Server archive. Sign the OCI digest locally before Docker Hub promotion.")
            api = FakeApi(record)
            args = argparse.Namespace(directory=directory, tag=TAG, revision=REVISION, notes=notes, release_id=None)
            names = release.expected_names("0.3.19", 149)
            with patch.object(release, "upload_assets", return_value={name: {} for name in names}), \
                    patch.object(release, "verify_existing_assets", return_value={name: {} for name in names}), \
                    redirect_stdout(io.StringIO()) as output:
                release.publish(args, api)
            result = json.loads(output.getvalue())
            self.assertEqual(result["status"], "published")
            patch_call = next(call for call in api.calls if call[0] == "PATCH")
            self.assertEqual(patch_call[1], "/releases/17")


if __name__ == "__main__":
    unittest.main()
