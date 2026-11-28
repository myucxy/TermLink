#!/usr/bin/env bash
set -euo pipefail

work=$(mktemp -d "$RUNNER_TEMP/termlink-native-smoke.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
source="$RUNNER_TEMP/verified-core-source/core-source.zip"
native=$(python3 - "$source" "$work" "$CLIENT_RID" <<'PY'
import hashlib
import json
from pathlib import Path
import sys
import zipfile

source, work, rid = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
policy = json.loads(Path('release/github-client-core-source.json').read_text())
lock = json.loads(Path('terminal-core.macos.lock.json').read_text())
with source.open('rb') as stream:
    assert hashlib.file_digest(stream, 'sha256').hexdigest() == policy['archive_sha256']
assert lock['source_dirty'] is False and lock['revision'] == policy['core_revision']
(library,) = [row for row in lock['libraries'] if row['rid'] == rid]
native = Path('native/terminal-core/macos')/rid/'libtermlink_terminal_ffi.dylib'
assert library['path'] == native.as_posix() and native.stat().st_size == library['size']
assert hashlib.sha256(native.read_bytes()).hexdigest() == library['sha256']
with zipfile.ZipFile(source) as archive:
    manifest = json.loads(archive.read('SOURCE-MANIFEST.json'))
    assert manifest['core_revision'] == lock['revision']
    for name in ('include/termlink_terminal.h', 'scripts/cursor_blink_smoke.c', 'scripts/apple_core_smoke.c'):
        entry = 'terminal-core/' + name
        (record,) = [row for row in manifest['files'] if row['path'] == entry]
        data = archive.read(entry)
        assert len(data) == record['size'] and hashlib.sha256(data).hexdigest() == record['sha256']
        target = work/'TermLinkTerminalCore'/'termlink_terminal.h' if name.startswith('include/') else work/Path(name).name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
print(native.resolve())
PY
)
case "$CLIENT_RID:$(uname -m)" in osx-arm64:arm64|osx-x64:x86_64) ;; *) exit 1 ;; esac
[[ $(lipo -archs "$native") == "$(uname -m)" ]]
otool -l "$native" > "$work/load-commands"
grep -q 'minos 13.0' "$work/load-commands"
for probe in apple_core_smoke cursor_blink_smoke; do
  clang -UNDEBUG -I "$work" -I "$work/TermLinkTerminalCore" "$work/$probe.c" "$native" \
    -Wl,-rpath,"$(dirname "$native")" -o "$work/$probe"
  "$work/$probe"
done
digest=$(shasum -a 256 "$native" | cut -d ' ' -f 1)
printf 'TERMLINK_MACOS_NATIVE_SMOKE_SHA256=%s\n' "$digest" >> "$GITHUB_ENV"
