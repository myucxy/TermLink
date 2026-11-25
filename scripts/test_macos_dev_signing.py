"""DEV identity and real Keychain continuity; uses only disposable local bundles/items."""
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid

SIGNER = Path(__file__).resolve().parents[1] / "pc-client/package/macos/sign-dev-app.sh"
PROBE = r'''#include <Security/Security.h>
#include <stdio.h>
#include <string.h>
#ifndef BUILD_MARKER
#define BUILD_MARKER "unset"
#endif
int main(int argc, char **argv) {
    if (argc != 3) return 2;
    const char *service = argv[1], *account = "ephemeral-validation";
    const char sentinel[] = "TermLink disposable signing continuity sentinel";
    SecKeychainSetUserInteractionAllowed(false);
    OSStatus status;
    if (!strcmp(argv[2], "write")) {
        status = SecKeychainAddGenericPassword(NULL, (UInt32)strlen(service), service,
            (UInt32)strlen(account), account, sizeof(sentinel), sentinel, NULL);
    } else {
        UInt32 length = 0;
        void *bytes = NULL;
        SecKeychainItemRef item = NULL;
        status = SecKeychainFindGenericPassword(NULL, (UInt32)strlen(service), service,
            (UInt32)strlen(account), account, &length, &bytes, &item);
        if (!status) {
            if (!strcmp(argv[2], "read")) {
                if (length != sizeof(sentinel) || memcmp(bytes, sentinel, length)) status = -1;
            } else if (!strcmp(argv[2], "delete")) {
                status = SecKeychainItemDelete(item);
            } else { status = -2; }
        }
        if (bytes) SecKeychainItemFreeContent(NULL, bytes);
        if (item) CFRelease(item);
    }
    if (status) { fprintf(stderr, "Keychain OSStatus: %d\n", (int)status); return 1; }
    puts(BUILD_MARKER);
    return 0;
}
'''


@unittest.skipUnless(sys.platform == "darwin", "requires macOS code signing and Keychain")
class DevSigningTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="termlink-dev-signing-")
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def bundle(self, name="fixture", marker="first", identifier="dev.termlink.desktop.dev"):
        app = self.root / name / "TermLink DEV.app"
        macos = app / "Contents/MacOS"
        (macos / "agent").mkdir(parents=True)
        (app / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": identifier,
            "CFBundleExecutable": "TermLink",
            "CFBundlePackageType": "APPL",
            "TermLinkBuildChannel": "Development",
        }))
        source = self.root / (name + ".c")
        source.write_text(PROBE)
        executable = macos / "TermLink"
        subprocess.run(["/usr/bin/cc", "-Wno-deprecated-declarations", '-DBUILD_MARKER="' + marker + '"',
                        str(source), "-framework", "Security", "-framework", "CoreFoundation",
                        "-o", str(executable)], check=True, capture_output=True)
        shutil.copy2(executable, macos / "agent/TermLink.Agent")
        (macos / "Managed.dll").write_bytes(b"disposable generic-code fixture")
        (macos / "LICENSE.txt").write_text("Disposable packaging fixture, no third-party content.")
        (macos / "fixture.runtimeconfig.json").write_text('{"fixture": true}')
        return app

    def sign(self, app, identity):
        return subprocess.run(["/bin/bash", str(SIGNER), str(app), identity],
                              capture_output=True, text=True, timeout=60)

    def requirement(self, code):
        result = subprocess.run(["/usr/bin/codesign", "-d", "-r-", str(code)],
                                check=True, capture_output=True, text=True)
        return next(line for line in (result.stdout + result.stderr).splitlines()
                    if line.startswith("designated =>"))

    def test_ad_hoc_remains_available(self):
        app = self.bundle()
        result = self.sign(app, "-")
        self.assertEqual(0, result.returncode, result.stderr)
        details = subprocess.run(["/usr/bin/codesign", "-dv", str(app)], capture_output=True, text=True, check=True)
        self.assertIn("Signature=adhoc", details.stderr)

    def test_production_bundle_rejected(self):
        result = self.sign(self.bundle(identifier="dev.termlink.desktop"), "-")
        self.assertEqual(2, result.returncode)
        self.assertIn("only the DEV bundle", result.stderr)

    def test_ambiguous_or_unavailable_identity_rejected(self):
        app = self.bundle()
        self.assertEqual(2, self.sign(app, "Apple Development").returncode)
        result = self.sign(app, "0" * 40)
        self.assertEqual(3, result.returncode)
        self.assertIn("unavailable", result.stderr)

    def test_external_agent_symlink_rejected(self):
        app = self.bundle()
        agent = app / "Contents/MacOS/agent/TermLink.Agent"
        external = self.root / "outside"
        agent.rename(external)
        agent.symlink_to(external)
        before = external.read_bytes()
        self.assertEqual(2, self.sign(app, "-").returncode)
        self.assertEqual(before, external.read_bytes())

    @unittest.skipUnless(os.environ.get("TERMLINK_TEST_DEV_SIGNING_IDENTITY"), "set an existing test identity fingerprint")
    def test_two_builds_preserve_identity_and_keychain(self):
        identity = os.environ["TERMLINK_TEST_DEV_SIGNING_IDENTITY"]
        first, second = self.bundle("first", "first"), self.bundle("second", "second")
        for app in (first, second):
            result = self.sign(app, identity)
            self.assertEqual(0, result.returncode, result.stderr)
        a, b = (app / "Contents/MacOS/agent/TermLink.Agent" for app in (first, second))
        self.assertNotEqual(a.read_bytes(), b.read_bytes())
        self.assertEqual(self.requirement(first), self.requirement(second))
        self.assertEqual(self.requirement(a), self.requirement(b))
        stripped_archive = self.root / "stripped.zip"
        stripped = self.root / "stripped"
        subprocess.run(["/usr/bin/ditto", "-c", "-k", "--norsrc", "--noextattr", "--keepParent",
                        str(second), str(stripped_archive)], check=True, capture_output=True)
        subprocess.run(["/usr/bin/ditto", "-x", "-k", str(stripped_archive), str(stripped)],
                       check=True, capture_output=True)
        invalid = subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(stripped / second.name)],
                                 capture_output=True)
        self.assertNotEqual(0, invalid.returncode, "stripping generic signatures must be detected")
        archive = self.root / "signed.zip"
        restored = self.root / "restored"
        subprocess.run(["/usr/bin/ditto", "-c", "-k", "--sequesterRsrc", "--noqtn", "--keepParent",
                        str(second), str(archive)], check=True, capture_output=True)
        subprocess.run(["/usr/bin/ditto", "-x", "-k", str(archive), str(restored)], check=True, capture_output=True)
        restored_app = restored / second.name
        subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(restored_app)],
                       check=True, capture_output=True)
        b = restored_app / "Contents/MacOS/agent/TermLink.Agent"
        service = "dev.termlink.signing-validation." + uuid.uuid4().hex
        def action(code, operation):
            return subprocess.run([str(code), service, operation], capture_output=True, text=True, timeout=15)
        write = action(a, "write")
        self.assertEqual(0, write.returncode, write.stderr)
        try:
            read = action(b, "read")
            self.assertEqual(0, read.returncode, read.stderr)
            self.assertEqual("second", read.stdout.strip())
        finally:
            removed = action(a, "delete")
            self.assertEqual(0, removed.returncode, removed.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
