# SPDX-License-Identifier: GPL-3.0-only
"""Execute the production tool-selection block with CMake, without building JNI."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

CMAKE = os.environ.get("TERMLINK_CMAKE") or shutil.which("cmake")
SOURCE = Path(__file__).resolve().parents[1] / "android/core-network/src/main/cpp/CMakeLists.txt"
ABIS = {"arm64-v8a": "aarch64-linux-android26", "armeabi-v7a": "armv7a-linux-androideabi26",
        "x86": "i686-linux-android26", "x86_64": "x86_64-linux-android26"}


class DirectCmakeSelectionTest(unittest.TestCase):
    def configure(self, host, abi, missing=None):
        # Exercise the production block itself, not a second copy of its selection logic.
        production = SOURCE.read_text()
        block = production[production.index("if(NOT ANDROID)"):production.index("file(GLOB_RECURSE RUST_SOURCES")]
        with tempfile.TemporaryDirectory(prefix="termlink-cmake-selection-") as temporary:
            root = Path(temporary)
            bin_dir = root / "NDK tools"
            cargo_dir = root / "Rust tools"
            bin_dir.mkdir(); cargo_dir.mkdir()
            windows = host == "Windows"
            suffix = ".exe" if windows else ""
            linker = ABIS.get(abi, "unsupported") + "-clang" + (".cmd" if windows else "")
            tools = {"TERMLINK_CARGO": cargo_dir / ("cargo" + suffix), "RUSTC": cargo_dir / ("rustc" + suffix),
                     "RUST_LINKER": bin_dir / linker, "RUST_ARCHIVER": bin_dir / ("llvm-ar" + suffix)}
            for tool, path in tools.items():
                if tool != missing:
                    path.write_text("fixture only")
            script = root / "select.cmake"
            prelude = f'''set(ANDROID TRUE)
set(ANDROID_ABI "{abi}")
set(CMAKE_HOST_WIN32 {"TRUE" if windows else "FALSE"})
set(CMAKE_HOST_UNIX {"FALSE" if windows else "TRUE"})
set(CMAKE_HOST_SYSTEM_NAME "{host}")
set(TERMLINK_DIRECT_CORE_ROOT "{root.as_posix()}")
set(TERMLINK_CARGO "{tools['TERMLINK_CARGO'].as_posix()}")
set(CMAKE_C_COMPILER "{(bin_dir / ('clang' + suffix)).as_posix()}")
'''
            script.write_text(prelude + block + '\nmessage(STATUS "SELECTED=${RUST_LINKER}|${RUST_ARCHIVER}|${RUSTC}")\n')
            result = subprocess.run([CMAKE, "-P", str(script)], capture_output=True, text=True, timeout=10)
            return result, tools

    def test_windows_macos_and_linux_select_exact_tools_for_all_abis(self):
        for host in ("Windows", "Darwin", "Linux"):
            for abi in ABIS:
                with self.subTest(host=host, abi=abi):
                    result, tools = self.configure(host, abi)
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("SELECTED=" + "|".join(tools[k].as_posix() for k in ("RUST_LINKER", "RUST_ARCHIVER", "RUSTC")), result.stdout)

    def test_missing_tools_fail_during_selection(self):
        for host in ("Windows", "Darwin"):
            for tool in ("TERMLINK_CARGO", "RUSTC", "RUST_LINKER", "RUST_ARCHIVER"):
                with self.subTest(host=host, tool=tool):
                    result, _ = self.configure(host, "arm64-v8a", missing=tool)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(tool + " is missing or is not a file:", result.stderr)

    def test_unknown_abi_is_rejected_before_building(self):
        result, _ = self.configure("Darwin", "unknown")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Unsupported Android ABI: unknown", result.stderr)


if __name__ == "__main__":
    if not CMAKE:
        raise SystemExit("CMake is required; set TERMLINK_CMAKE or provide cmake on PATH")
    unittest.main()
