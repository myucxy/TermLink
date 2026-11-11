#!/usr/bin/env python3
"""Verify precompiled macOS terminal-core only. Never reads or builds the core repository."""
import argparse, hashlib, json, re, shutil, subprocess
from pathlib import Path, PurePosixPath

def verify(root, rid):
    root = root.resolve(strict=True)
    lock = json.loads((root / "terminal-core.macos.lock.json").read_text())
    if lock.get("schema") != 2 or lock.get("platform") != "macos" or lock.get("source_dirty") is not False or not re.fullmatch(r"[0-9a-f]{40}", lock.get("revision", "")):
        raise ValueError("invalid/dirty macOS core lock")
    if lock.get("rust_toolchain") != "1.97.1" or lock.get("abi_version") != 1 or lock.get("source_method") != "git_archive_HEAD" or lock.get("minimum_os") != "13.0":
        raise ValueError("unapproved native contract")
    libraries = [x for x in lock["libraries"] if x["rid"] == rid]
    if len(libraries) != 1 or libraries[0].get("smoke_executed") is not True:
        raise ValueError("native architecture unavailable or not smoke tested")
    library = libraries[0]
    architecture = {"osx-arm64": "arm64", "osx-x64": "x86_64"}[rid]
    expected = "native/terminal-core/macos/" + rid + "/libtermlink_terminal_ffi.dylib"
    if library["path"] != expected or library.get("platform") != "macos" or library.get("architecture") != {"osx-arm64":"arm64","osx-x64":"x64"}[rid] or library.get("abi_version") != 1 or library.get("minimum_os") != "13.0":
        raise ValueError("incorrect native platform metadata")
    for group in (lock["artifacts"], lock["libraries"]):
        paths = [x["path"] for x in group]
        if len(paths) != len(set(paths)): raise ValueError("duplicate artifact entry")
    seen = set()
    for entry in lock["artifacts"] + libraries:
        path = PurePosixPath(entry["path"])
        if entry["path"] != path.as_posix() or "\\" in entry["path"] or path.is_absolute() or ".." in path.parts or not str(path).startswith("native/terminal-core/macos/"):
            raise ValueError("invalid artifact path")
        target = root / path
        if any(x.is_symlink() for x in [target, *target.parents] if x != root.parent) or not target.resolve().is_relative_to(root.resolve()):
            raise ValueError("artifact symlink")
        data = target.read_bytes()
        if len(data) != entry["size"] or hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError("native artifact hash mismatch: " + str(path))
        seen.add(str(path))
    if libraries[0]["path"] not in seen: raise ValueError("missing native library")
    native = root / expected
    arches = subprocess.check_output(["/usr/bin/lipo", "-archs", str(native)], text=True).strip().split()
    if arches != [architecture]: raise ValueError("native Mach-O architecture mismatch")
    load = subprocess.check_output(["/usr/bin/otool", "-l", str(native)], text=True)
    if "platform 1" not in load or "minos 13.0" not in load: raise ValueError("native macOS deployment mismatch")
    return library

if __name__ == "__main__":
    parser=argparse.ArgumentParser();parser.add_argument("root",type=Path);parser.add_argument("rid",choices=["osx-arm64","osx-x64"]);parser.add_argument("--stage",type=Path)
    args=parser.parse_args(); library=verify(args.root,args.rid)
    if args.stage:
        target=args.stage;target.mkdir(parents=True,exist_ok=True)
        shutil.copy2(args.root/library["path"], target/"libtermlink_terminal_ffi.dylib")
    print("Verified macOS terminal-core: " + library["rid"])
