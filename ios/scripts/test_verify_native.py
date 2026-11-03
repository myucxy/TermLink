#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Exercise the native Swift verifier against small local tampering fixtures."""

import copy
import hashlib
import json
import os
import pathlib
import plistlib
import shutil
import subprocess
import tempfile
import unittest


FRAMEWORK = pathlib.Path("native/terminal-core/ios/TermLinkTerminalCore.xcframework")
NAME = "TermLinkTerminalCore"
LOCK = "terminal-core.ios.lock.json"


class VerifyNativeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.compiler_directory = tempfile.TemporaryDirectory(prefix="termlink-native-verifier-")
        cls.addClassCleanup(cls.compiler_directory.cleanup)
        local = pathlib.Path(cls.compiler_directory.name).resolve()
        source = local / "verify_native.swift"
        shutil.copyfile(str(pathlib.Path(__file__).with_name("verify_native.swift")), str(source))
        cls.executable = local / "verify-native"
        sdk = subprocess.check_output(["/usr/bin/xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
        result = subprocess.run(["/usr/bin/xcrun", "--sdk", "macosx", "swiftc", "-sdk", sdk,
                                 "-module-cache-path", str(local / "module-cache"), str(source),
                                 "-o", str(cls.executable)], cwd=str(local),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False)
        if result.returncode:
            raise RuntimeError("Cannot compile native verifier:\n" + result.stdout)

    def setUp(self):
        self.fixture_directory = tempfile.TemporaryDirectory(prefix="termlink-native-fixture-")
        self.addCleanup(self.fixture_directory.cleanup)
        self.root = pathlib.Path(self.fixture_directory.name).resolve()
        self.framework = self.root / FRAMEWORK
        self.framework.mkdir(parents=True)
        self.libraries = [
            {"LibraryIdentifier": "ios-arm64", "LibraryPath": NAME + ".framework",
             "SupportedArchitectures": ["arm64"], "SupportedPlatform": "ios"},
            {"LibraryIdentifier": "ios-arm64_x86_64-simulator", "LibraryPath": NAME + ".framework",
             "SupportedArchitectures": ["arm64", "x86_64"], "SupportedPlatform": "ios",
             "SupportedPlatformVariant": "simulator"},
        ]
        self.xcinfo = {"CFBundlePackageType": "XFWK", "XCFrameworkFormatVersion": "1.0",
                       "AvailableLibraries": self.libraries}
        self.write_plist(self.framework / "Info.plist", self.xcinfo)
        for library in self.libraries:
            simulator = library.get("SupportedPlatformVariant") == "simulator"
            wrapper = self.framework / library["LibraryIdentifier"] / library["LibraryPath"]
            (wrapper / "Headers").mkdir(parents=True)
            (wrapper / "Modules").mkdir()
            (wrapper / NAME).write_bytes(b"!<arch>\nfixture-static-library")
            (wrapper / "Headers/termlink_terminal.h").write_text("/* packaged header */\n", encoding="utf-8")
            (wrapper / "Modules/module.modulemap").write_text(
                'framework module TermLinkTerminalCore {\n umbrella header "termlink_terminal.h"\n export *\n module * { export * }\n}\n', encoding="utf-8")
            self.write_plist(wrapper / "Info.plist", {
                "CFBundleExecutable": NAME, "CFBundlePackageType": "FMWK", "MinimumOSVersion": "17.0",
                "CFBundleSupportedPlatforms": ["iPhoneSimulator" if simulator else "iPhoneOS"],
                "DTPlatformName": "iphonesimulator" if simulator else "iphoneos",
            })
        self.lock = {"schema_version": 2, "repository": "git@github.com:myucxy/terminal-core.git",
                     "revision": "a" * 40, "source_dirty": False, "toolchain": "1.97.1"}
        self.reseal()

    @staticmethod
    def write_plist(path, value):
        with path.open("wb") as handle:
            plistlib.dump(value, handle)

    def save_lock(self):
        (self.root / LOCK).write_text(json.dumps(self.lock), encoding="utf-8")

    def reseal(self):
        self.lock["artifacts"] = []
        for path in sorted(self.framework.rglob("*")):
            if path.is_file():
                data = path.read_bytes()
                self.lock["artifacts"].append({"platform": "ios", "path": path.relative_to(self.root).as_posix(),
                                                "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        self.save_lock()

    def run_verifier(self, expected_success=False, arguments=None):
        args = arguments if arguments is not None else ["--root", str(self.root)]
        result = subprocess.run([str(self.executable)] + args, cwd=str(self.root),
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=False)
        if expected_success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Verified iOS terminal-core revision " + self.lock["revision"], result.stdout)
        else:
            self.assertNotEqual(result.returncode, 0, "Verifier unexpectedly accepted fixture:\n" + result.stdout)
            self.assertIn("error: iOS native artifact verification failed:", result.stderr)
        return result

    def wrapper(self, simulator=False):
        library = self.libraries[1 if simulator else 0]
        return self.framework / library["LibraryIdentifier"] / library["LibraryPath"]

    def test_valid_independent_ios_lock_passes_without_git_checkout(self):
        self.run_verifier(expected_success=True)

    def test_old_root_lock_is_never_a_fallback(self):
        (self.root / LOCK).rename(self.root / "terminal-core.lock.json")
        self.run_verifier()

    def test_missing_lock_or_framework_is_rejected(self):
        lock = self.root / LOCK
        content = lock.read_bytes()
        lock.unlink()
        self.run_verifier()
        lock.write_bytes(content)
        shutil.rmtree(self.framework)
        self.run_verifier()

    def test_provenance_is_strict(self):
        original = copy.deepcopy(self.lock)
        for field, value in [("schema_version", 1), ("source_dirty", True), ("source_dirty", "false"),
                             ("repository", "git@github.com:other/terminal-core.git"), ("toolchain", "1.97.0"),
                             ("revision", "A" * 40), ("revision", "a" * 39), ("revision", "a" * 40 + "\n")]:
            with self.subTest(field=field, value=value):
                self.lock = copy.deepcopy(original)
                self.lock[field] = value
                self.save_lock()
                self.run_verifier()

    def test_non_ios_or_mixed_platform_records_are_rejected(self):
        self.lock["artifacts"][0]["platform"] = "windows"
        self.save_lock()
        self.run_verifier()

    def test_duplicate_and_case_alias_records_are_rejected(self):
        record = copy.deepcopy(self.lock["artifacts"][0])
        self.lock["artifacts"].append(record)
        self.save_lock()
        self.run_verifier()
        self.lock["artifacts"][-1]["path"] = record["path"].upper()
        self.save_lock()
        self.run_verifier()

    def test_absolute_traversal_and_noncanonical_names_are_rejected(self):
        original = self.lock["artifacts"][0]["path"]
        for path in ["/" + original, "../" + original, original.replace("/ios/", "/ios/../ios/"),
                     original.replace("/ios/", "/ios//"), original.replace("/ios/", "/ios/./"),
                     original.replace("/", "\\"), original + "\n", "native/terminal-core/windows-x64/core.dll"]:
            with self.subTest(path=path):
                self.lock["artifacts"][0]["path"] = path
                self.save_lock()
                self.run_verifier()

    def test_extra_unrecorded_and_missing_recorded_files_are_rejected(self):
        extra = self.framework / "extra.txt"
        extra.write_bytes(b"not recorded")
        self.run_verifier()
        extra.unlink()
        (self.root / self.lock["artifacts"][0]["path"]).unlink()
        self.run_verifier()

    def test_omitted_artifact_record_and_empty_inventory_are_rejected(self):
        self.lock["artifacts"].pop()
        self.save_lock()
        self.run_verifier()
        self.lock["artifacts"] = []
        self.save_lock()
        self.run_verifier()

    def test_hash_size_and_malformed_digest_are_rejected(self):
        original = copy.deepcopy(self.lock)
        for field, value in [("sha256", "b" * 64), ("sha256", "a" * 63), ("sha256", "G" * 64),
                             ("size", 0), ("size", -1), ("size", original["artifacts"][0]["size"] + 1)]:
            with self.subTest(field=field, value=value):
                self.lock = copy.deepcopy(original)
                self.lock["artifacts"][0][field] = value
                self.save_lock()
                self.run_verifier()

    def test_replaced_file_bytes_are_rejected(self):
        path = self.wrapper() / NAME
        data = path.read_bytes()
        path.write_bytes(b"?" + data[1:])
        self.run_verifier()

    def test_file_lock_framework_and_intermediate_directory_links_are_rejected(self):
        for kind in ["file", "lock", "framework", "native", "headers"]:
            with self.subTest(kind=kind):
                paths = {"file": self.wrapper() / NAME, "lock": self.root / LOCK,
                         "framework": self.framework, "native": self.root / "native",
                         "headers": self.wrapper() / "Headers"}
                path = paths[kind]
                moved = self.root / ("moved-" + kind)
                path.rename(moved)
                path.symlink_to(moved, target_is_directory=moved.is_dir())
                try:
                    self.run_verifier()
                finally:
                    path.unlink()
                    moved.rename(path)

    def test_xcframework_slice_platform_architecture_and_count_are_strict(self):
        original = copy.deepcopy(self.xcinfo)
        changes = [lambda info: info["AvailableLibraries"].pop(),
                   lambda info: info["AvailableLibraries"].append(copy.deepcopy(info["AvailableLibraries"][0])),
                   lambda info: info["AvailableLibraries"][0].update(SupportedPlatform="macos"),
                   lambda info: info["AvailableLibraries"][0].update(SupportedPlatformVariant="simulator"),
                   lambda info: info["AvailableLibraries"][0].update(SupportedPlatformVariant="device"),
                   lambda info: info["AvailableLibraries"][1].update(SupportedArchitectures=["arm64"]),
                   lambda info: info["AvailableLibraries"][1].update(SupportedArchitectures=["arm64", "arm64", "x86_64"]),
                   lambda info: info["AvailableLibraries"][1].update(LibraryIdentifier="../escape"),
                   lambda info: info["AvailableLibraries"][1].update(LibraryPath="Other.framework")]
        for change in changes:
            info = copy.deepcopy(original)
            change(info)
            self.write_plist(self.framework / "Info.plist", info)
            self.reseal()
            self.run_verifier()

    def test_required_binary_header_and_module_must_exist_even_if_lock_is_resealed(self):
        for relative in [NAME, "Headers/termlink_terminal.h", "Modules/module.modulemap"]:
            with self.subTest(relative=relative):
                path = self.wrapper() / relative
                data = path.read_bytes()
                path.unlink()
                self.reseal()
                self.run_verifier()
                path.write_bytes(data)

    def test_module_name_header_contract_and_framework_minimum_are_strict(self):
        module = self.wrapper() / "Modules/module.modulemap"
        original = module.read_bytes()
        for content in [b"framework module Other { umbrella header \"termlink_terminal.h\" export * }",
                        b"framework module TermLinkTerminalCore { umbrella header \"../../secret.h\" export * }"]:
            module.write_bytes(content)
            self.reseal()
            self.run_verifier()
        module.write_bytes(original)
        path = self.wrapper() / "Info.plist"
        with path.open("rb") as handle:
            info = plistlib.load(handle)
        info["MinimumOSVersion"] = "18.0"
        self.write_plist(path, info)
        self.reseal()
        self.run_verifier()

    def test_relative_root_and_unknown_arguments_are_rejected(self):
        for arguments in [["--root", "."], ["--root"], ["--unknown", str(self.root)]]:
            self.run_verifier(arguments=arguments)


if __name__ == "__main__":
    unittest.main()
