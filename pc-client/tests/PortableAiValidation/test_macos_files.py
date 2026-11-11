#!/usr/bin/env python3
"""Descriptor and ACL checks against the caller-selected locally built helper."""
import ctypes
import os
from pathlib import Path
import subprocess
import sys
import tempfile

library = ctypes.CDLL(sys.argv[1])
library.termlink_files_private_directory.argtypes = [ctypes.c_int, ctypes.c_char_p]
library.termlink_files_write_child.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
with tempfile.TemporaryDirectory(prefix="termlink-ai-files-acl-") as temporary:
    root = Path(temporary).resolve()
    with_root = os.open(root, os.O_RDONLY)
    try:
        private = library.termlink_files_private_directory(with_root, b"private")
        assert private >= 0, f"Private no-ACL directory rejected: {private}"
        try:
            file = library.termlink_files_write_child(private, b"upload", 1)
            assert file >= 0
            os.write(file, b"fixture")
            os.close(file)
            # A broad ACL with unchanged 0600 must be rejected on an existing file.
            subprocess.run(["/bin/chmod", "+a", "everyone allow read", str(root / "private/upload")], check=True)
            assert library.termlink_files_write_child(private, b"upload", 0) < 0
            subprocess.run(["/bin/chmod", "-N", str(root / "private/upload")], check=True)
        finally:
            os.close(private)
        # Inheritable grants are rejected before creating an attachment directory.
        subprocess.run(["/bin/chmod", "+a", "everyone allow read,readattr,readextattr,readsecurity,file_inherit,directory_inherit", str(root)], check=True)
        try:
            assert library.termlink_files_private_directory(with_root, b"shared") < 0
            assert not (root / "shared").exists()
        finally:
            subprocess.run(["/bin/chmod", "-N", str(root)], check=True)
        (root / "link").symlink_to(root / "private", target_is_directory=True)
        assert library.termlink_files_private_directory(with_root, b"link") < 0
        assert library.termlink_files_private_directory(with_root, b"../escape") < 0
    finally:
        os.close(with_root)
print("PASS macOS native ACL/no-follow: private directory/file, inherited ACL rejection, existing file ACL rejection, links/traversal")
