#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: Export-MacOSAssets.sh <output.icns>" >&2
  exit 2
fi
[[ $(uname -s) == Darwin ]] || { echo "macOS host required" >&2; exit 3; }
output=$1
[[ $output == *.icns ]] || { echo "an .icns output path is required" >&2; exit 2; }
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
work_root=$(mktemp -d "${TMPDIR:-/tmp}/termlink-icon.XXXXXX")
trap 'rm -rf -- "$work_root"' EXIT
iconset="$work_root/TermLink.iconset"
mkdir -p "$iconset" "$(dirname -- "$output")"

# Render the existing vector once at full resolution, preserving transparency.
# The same source defines the Windows/product icon; no separate Mac design.
sips -s format png "$source_dir/termlink-app-icon.svg" --out "$work_root/original.png" > /dev/null
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$work_root/original.png" --out "$iconset/icon_${size}x${size}.png" > /dev/null
  retina=$((size * 2))
  sips -z "$retina" "$retina" "$work_root/original.png" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$output"
echo "macOS icon: $output"
