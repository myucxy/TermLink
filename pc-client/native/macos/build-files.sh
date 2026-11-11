#!/usr/bin/env bash
set -euo pipefail
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
output=${1:?output directory required}
[[ $(uname -s) == Darwin ]] || exit 3
mkdir -p "$output"
cc -std=c11 -O2 -fstack-protector-strong -Wall -Wextra -Werror -dynamiclib -mmacosx-version-min=13.0 \
  -Wl,-install_name,@rpath/libtermlink-files.dylib "$source_dir/files.c" -o "$output/libtermlink-files.dylib"
