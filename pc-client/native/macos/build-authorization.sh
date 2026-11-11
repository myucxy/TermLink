#!/usr/bin/env bash
set -euo pipefail
[[ $# -eq 1 && $1 == /* && $(uname -s) == Darwin ]] || { echo "usage: build-authorization.sh <absolute-output-directory>" >&2; exit 2; }
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
mkdir -p "$1"
/usr/bin/xcrun clang -dynamiclib -fobjc-arc -fblocks -mmacosx-version-min=13.0 \
  -fvisibility=hidden -Werror -Wno-deprecated-declarations \
  -framework Foundation -framework Security -framework UserNotifications -framework ApplicationServices \
  "$script_dir/authorization.m" -o "$1/libtermlink-authorization.dylib"
