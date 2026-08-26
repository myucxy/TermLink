#!/usr/bin/env sh
set -eu

source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir=${1:?output directory is required}
mkdir -p "$output_dir"

case "$(uname -s)" in
  Linux)
    cc -std=c11 -O2 -fPIC -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
      -Wall -Wextra -Werror -shared \
      "$source_dir/termlink_pty.c" -Wl,-z,relro,-z,now -lutil \
      -o "$output_dir/libtermlink-pty.so"
    ;;
  Darwin)
    cc -std=c11 -O2 -fPIC -fstack-protector-strong \
      -Wall -Wextra -Werror -dynamiclib \
      "$source_dir/termlink_pty.c" -o "$output_dir/libtermlink-pty.dylib"
    ;;
  *)
    echo "unsupported native build host: $(uname -s)" >&2
    exit 2
    ;;
esac
