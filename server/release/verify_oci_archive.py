#!/usr/bin/env python3
"""Verify a BuildKit OCI archive without extracting archive entries to disk."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import struct
import sys
import tarfile
from pathlib import Path
from typing import Any


class VerificationError(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)


def digest_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def descriptor_digest(descriptor: dict[str, Any]) -> str:
    value = descriptor.get("digest")
    require(isinstance(value, str) and value.startswith("sha256:") and len(value) == 71, f"invalid OCI digest: {value!r}")
    require(all(char in "0123456789abcdef" for char in value[7:]), f"invalid OCI digest: {value!r}")
    return value


def descriptor_size(descriptor: dict[str, Any]) -> int:
    value = descriptor.get("size")
    require(isinstance(value, int) and value >= 0, f"invalid OCI descriptor size: {value!r}")
    return value


class ArchiveReader:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.archive = tarfile.open(path, mode="r:*")
        self.members: dict[str, tarfile.TarInfo] = {}
        for member in self.archive:
            require(member.name not in self.members, f"duplicate OCI archive entry: {member.name}")
            self.members[member.name] = member

    def close(self) -> None:
        self.archive.close()

    def read(self, name: str) -> bytes:
        member = self.members.get(name)
        require(member is not None, f"OCI archive entry is missing: {name}")
        require(member.isfile(), f"OCI archive entry is not a regular file: {name}")
        stream = self.archive.extractfile(member)
        require(stream is not None, f"OCI archive entry cannot be read: {name}")
        with stream:
            return stream.read()

    def read_blob(self, descriptor: dict[str, Any], description: str) -> bytes:
        digest = descriptor_digest(descriptor)
        expected_size = descriptor_size(descriptor)
        data = self.read(f"blobs/sha256/{digest[7:]}")
        require(len(data) == expected_size, f"{description} size mismatch: {digest}")
        require(digest_bytes(data) == digest[7:], f"{description} digest mismatch: {digest}")
        return data

    def read_json(self, descriptor: dict[str, Any], description: str) -> dict[str, Any]:
        try:
            value = json.loads(self.read_blob(descriptor, description))
        except json.JSONDecodeError as error:
            raise VerificationError(f"{description} is not valid JSON: {error}") from error
        require(isinstance(value, dict), f"{description} must be a JSON object")
        return value


def read_layer_file(layer: bytes, wanted: str) -> bytes | None:
    try:
        with tarfile.open(fileobj=io.BytesIO(layer), mode="r:*") as filesystem:
            for member in filesystem:
                name = member.name.removeprefix("./")
                if name != wanted:
                    continue
                require(member.isfile(), f"OCI layer entry is not a regular file: {wanted}")
                stream = filesystem.extractfile(member)
                require(stream is not None, f"OCI layer entry cannot be read: {wanted}")
                with stream:
                    return stream.read()
    except (tarfile.TarError, EOFError) as error:
        raise VerificationError(f"invalid OCI layer tar: {error}") from error
    return None


def effective_file(layers: list[dict[str, Any]], reader: ArchiveReader, wanted: str) -> bytes:
    for layer in reversed(layers):
        data = read_layer_file(reader.read_blob(layer, f"OCI layer containing {wanted}"), wanted)
        if data is not None:
            return data
    raise VerificationError(f"OCI image is missing required file: {wanted}")


def validate_image(
    descriptor: dict[str, Any],
    reader: ArchiveReader,
    version: str,
    build: int,
    revision: str,
) -> tuple[str, dict[str, Any], list[dict[str, Any]]]:
    platform = descriptor.get("platform")
    require(isinstance(platform, dict), "image descriptor is missing platform")
    operating_system = platform.get("os")
    architecture = platform.get("architecture")
    require(operating_system == "linux" and architecture in ("amd64", "arm64"), f"unsupported image platform: {platform}")
    manifest_digest = descriptor_digest(descriptor)
    manifest = reader.read_json(descriptor, f"linux/{architecture} image manifest")
    config_descriptor = manifest.get("config")
    require(isinstance(config_descriptor, dict), f"linux/{architecture} image manifest has no config")
    config = reader.read_json(config_descriptor, f"linux/{architecture} image config")
    require(config.get("os") == "linux" and config.get("architecture") == architecture, f"image config platform mismatch for linux/{architecture}")
    config_data = config.get("config")
    require(isinstance(config_data, dict), f"linux/{architecture} image config has no config object")
    require(config_data.get("User") == "65532:65532", f"OCI user mismatch for linux/{architecture}")
    require(config_data.get("Entrypoint") == ["/termlink-server"], f"OCI entrypoint mismatch for linux/{architecture}")
    require(config_data.get("Cmd") == ["serve"], f"OCI command mismatch for linux/{architecture}")
    labels = config_data.get("Labels") or {}
    require(labels.get("org.opencontainers.image.version") == f"{version}+{build}", f"OCI version label mismatch for linux/{architecture}")
    require(labels.get("org.opencontainers.image.revision") == revision, f"OCI revision label mismatch for linux/{architecture}")
    require(labels.get("org.opencontainers.image.licenses") == "GPL-3.0-only", f"OCI license label mismatch for linux/{architecture}")

    layers = manifest.get("layers")
    require(isinstance(layers, list) and all(isinstance(layer, dict) for layer in layers), f"invalid OCI layers for linux/{architecture}")
    binary = effective_file(layers, reader, "termlink-server")
    require(len(binary) >= 20 and binary[:4] == b"\x7fELF" and binary[4:6] == b"\x02\x01", f"server binary is not ELF64 for linux/{architecture}")
    expected_machine = {"amd64": 62, "arm64": 183}[architecture]
    machine = struct.unpack_from("<H", binary, 18)[0]
    require(machine == expected_machine, f"ELF architecture mismatch for linux/{architecture}: {machine}")
    for layer in layers:
        reader.read_blob(layer, f"linux/{architecture} layer")
    return manifest_digest, manifest, layers


def validate_attestation(
    descriptor: dict[str, Any],
    reader: ArchiveReader,
    image_digests: set[str],
) -> tuple[str, set[str]]:
    annotations = descriptor.get("annotations") or {}
    parent = annotations.get("vnd.docker.reference.digest")
    require(isinstance(parent, str) and parent in image_digests, f"attestation references unknown image: {parent!r}")
    manifest = reader.read_json(descriptor, "OCI attestation manifest")
    config = manifest.get("config")
    if isinstance(config, dict):
        reader.read_blob(config, "OCI attestation config")
    predicates: set[str] = set()
    for layer in manifest.get("layers") or []:
        statement = reader.read_json(layer, "OCI attestation statement")
        subjects = statement.get("subject") or []
        require(any(isinstance(subject, dict) and (subject.get("digest") or {}).get("sha256") == parent[7:] for subject in subjects), "attestation subject does not match its image")
        predicate = statement.get("predicateType")
        require(isinstance(predicate, str), "attestation predicateType is missing")
        predicates.add(predicate)
    return parent, predicates


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("metadata", type=Path)
    parser.add_argument("version", type=str)
    parser.add_argument("build", type=int)
    parser.add_argument("revision", type=str)
    args = parser.parse_args()
    require(args.archive.is_file(), f"archive not found: {args.archive}")
    require(args.metadata.is_file(), f"metadata not found: {args.metadata}")
    require(args.version.count(".") == 2 and all(part.isdigit() for part in args.version.split(".")), "invalid version")
    require(1 <= args.build <= 65535, "build must be in 1..65535")
    require(len(args.revision) == 40 and all(char in "0123456789abcdef" for char in args.revision), "invalid revision")

    try:
        metadata = json.loads(args.metadata.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        raise VerificationError(f"metadata is not valid JSON: {error}") from error
    metadata_digest = metadata.get("containerimage.digest")
    require(isinstance(metadata_digest, str) and metadata_digest.startswith("sha256:") and len(metadata_digest) == 71, "metadata is missing containerimage.digest")

    archive_sha256 = hashlib.sha256()
    with args.archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            archive_sha256.update(block)

    reader = ArchiveReader(args.archive)
    try:
        try:
            root_index = json.loads(reader.read("index.json"))
        except json.JSONDecodeError as error:
            raise VerificationError(f"index.json is not valid JSON: {error}") from error
        require(root_index.get("schemaVersion") == 2, "OCI index schemaVersion must be 2")
        root_descriptors = root_index.get("manifests") or []
        require(isinstance(root_descriptors, list) and root_descriptors, "OCI index has no manifests")
        selected_index_digest = metadata_digest
        selected_index: dict[str, Any] | None = None
        if selected_index_digest == "sha256:" + digest_bytes(reader.read("index.json")):
            selected_index = root_index
        else:
            require(len(root_descriptors) == 1, "BuildKit OCI root index must contain exactly one nested index")
            for descriptor in root_descriptors:
                if isinstance(descriptor, dict) and descriptor.get("digest") == selected_index_digest:
                    selected_index = reader.read_json(descriptor, "nested OCI index")
                    break
        require(selected_index is not None, "BuildKit metadata digest does not match root or nested OCI index")
        require(selected_index.get("schemaVersion") == 2, "selected OCI index schemaVersion must be 2")
        image_descriptors: list[dict[str, Any]] = []
        attestation_descriptors: list[dict[str, Any]] = []
        for descriptor in selected_index.get("manifests") or []:
            require(isinstance(descriptor, dict), "OCI index manifest descriptor is invalid")
            annotations = descriptor.get("annotations") or {}
            if annotations.get("vnd.docker.reference.type") == "attestation-manifest":
                attestation_descriptors.append(descriptor)
            elif isinstance(descriptor.get("platform"), dict):
                image_descriptors.append(descriptor)
            else:
                raise VerificationError("OCI index contains an unsupported manifest descriptor")
        require(sorted((d.get("platform", {}).get("os"), d.get("platform", {}).get("architecture")) for d in image_descriptors) == [("linux", "amd64"), ("linux", "arm64")], "OCI index must contain exactly linux/amd64 and linux/arm64")
        platforms: list[dict[str, Any]] = []
        image_digests: set[str] = set()
        for descriptor in sorted(image_descriptors, key=lambda item: item["platform"]["architecture"]):
            digest, _, _ = validate_image(descriptor, reader, args.version, args.build, args.revision)
            image_digests.add(digest)
            platforms.append({"platform": f"linux/{descriptor['platform']['architecture']}", "digest": digest, "elf_machine": {"amd64": 62, "arm64": 183}[descriptor["platform"]["architecture"]]})
        attestation_by_image: dict[str, set[str]] = {}
        for descriptor in attestation_descriptors:
            parent, predicates = validate_attestation(descriptor, reader, image_digests)
            attestation_by_image.setdefault(parent, set()).update(predicates)
        require(set(attestation_by_image) == image_digests, "each image must have linked attestations")
        attestations = []
        for image_digest in sorted(attestation_by_image):
            predicates = sorted(attestation_by_image[image_digest])
            require("https://slsa.dev/provenance/v1" in predicates or "https://slsa.dev/provenance/v0.2" in predicates, f"SLSA provenance is missing for {image_digest}")
            require("https://spdx.dev/Document" in predicates, f"SPDX SBOM is missing for {image_digest}")
            attestations.append({"image_digest": image_digest, "predicate_types": predicates})
        result = {"verified": True, "archive": args.archive.name, "archive_sha256": archive_sha256.hexdigest(), "index_digest": metadata_digest, "version": args.version, "build_number": args.build, "revision": args.revision, "platforms": platforms, "attestations": attestations}
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=False))
        return 0
    finally:
        reader.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as error:
        print(f"verify_oci_archive: {error}", file=sys.stderr)
        raise SystemExit(1)
