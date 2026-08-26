#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "usage: build-appimage.sh <version> <build> <rid> <dotnet> <appimagetool> <gpg-key-fingerprint> <output>" >&2
  exit 2
fi
version=$1
build=$2
rid=$3
dotnet_path=$4
appimage_tool=$5
signing_fingerprint=${6//[ :_-]/}
output=$7

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "invalid version" >&2; exit 2; }
[[ $build =~ ^[1-9][0-9]{0,4}$ ]] || { echo "invalid build" >&2; exit 2; }
[[ $rid == linux-x64 || $rid == linux-arm64 ]] || { echo "invalid Linux RID" >&2; exit 2; }
[[ $signing_fingerprint =~ ^[A-Fa-f0-9]{40}$|^[A-Fa-f0-9]{64}$ ]] || { echo "invalid signing fingerprint" >&2; exit 2; }
[[ -x $dotnet_path && -x $appimage_tool ]] || { echo "pinned dotnet/appimagetool is unavailable" >&2; exit 3; }
command -v cc >/dev/null || { echo "C compiler is unavailable" >&2; exit 3; }
command -v gpg >/dev/null || { echo "GnuPG is unavailable" >&2; exit 3; }

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../../.." && pwd)
case $rid in linux-x64) appimage_arch=x86_64 ;; linux-arm64) appimage_arch=aarch64 ;; esac
work_root=$(mktemp -d)
trap 'rm -rf -- "$work_root"' EXIT
app_dir="$work_root/TermLink.AppDir"
mkdir -p "$app_dir/usr/bin" "$app_dir/usr/lib/termlink"

"$dotnet_path" publish "$repo_root/pc-client/src/Desktop/TermLink.Desktop.csproj" \
  -c Release -f net8.0 -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:FileVersion="$version.$build" \
  -o "$app_dir/usr/lib/termlink"
"$dotnet_path" publish "$repo_root/pc-client/src/Agent.Host/TermLink.Agent.Host.csproj" \
  -c Release -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:FileVersion="$version.$build" \
  -o "$app_dir/usr/lib/termlink/agent"
"$repo_root/pc-client/native/posix/build-native.sh" "$app_dir/usr/lib/termlink"

mv -- "$app_dir/usr/lib/termlink/agent/TermLink.Agent" "$app_dir/usr/lib/termlink/TermLink.Agent"
ln -s ../lib/termlink/TermLink "$app_dir/usr/bin/termlink"
cat > "$app_dir/AppRun" <<'APP_RUN'
#!/bin/sh
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export LD_LIBRARY_PATH="$HERE/usr/lib/termlink${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$HERE/usr/lib/termlink/TermLink" "$@"
APP_RUN
chmod 0755 "$app_dir/AppRun" "$app_dir/usr/lib/termlink/TermLink" "$app_dir/usr/lib/termlink/TermLink.Agent"
cat > "$app_dir/termlink.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=TermLink
Exec=termlink
Icon=termlink
Categories=Network;Utility;
Terminal=false
DESKTOP
cp -- "$repo_root/assets/brand/windows/TermLink-preview-256.png" "$app_dir/termlink.png"

mkdir -p -- "$(dirname -- "$output")"
ARCH=$appimage_arch "$appimage_tool" "$app_dir" "$output"
chmod 0755 "$output"
actual_fingerprint=$(gpg --batch --with-colons --list-secret-keys "$signing_fingerprint" | awk -F: '$1=="fpr" { print toupper($10); exit }')
[[ $actual_fingerprint == "${signing_fingerprint^^}" ]] || { echo "signing key continuity check failed" >&2; exit 4; }
gpg --batch --yes --local-user "$signing_fingerprint" --detach-sign --output "$output.sig" "$output"
gpgv --keyring "$repo_root/pc-client/package/linux/termlink-update.gpg" "$output.sig" "$output"
sha256sum "$output" "$output.sig"
