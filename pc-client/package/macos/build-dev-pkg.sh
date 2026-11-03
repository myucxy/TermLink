#!/bin/bash
set -euo pipefail

# Local, unsigned DEV installer only. This does not build, sign, notarize,
# install, launch, or publish an application, and is not an automatic update.
if [[ $# -ne 2 ]]; then
  echo "usage: build-dev-pkg.sh <absolute/TermLink DEV.app> <new-absolute-output.pkg>" >&2
  exit 2
fi
app=$1
output=$2
fail() { echo "$*" >&2; exit 2; }
[[ $(/usr/bin/uname -s) == Darwin ]] || fail "macOS host required"
[[ $app == /* && -d $app && ! -L $app && ${app##*/} == 'TermLink DEV.app' ]] ||
  fail "an existing absolute TermLink DEV.app bundle is required"
[[ $output == /* && $output == *.pkg && ! -e $output && ! -L $output ]] ||
  fail "a new absolute .pkg output path is required"
app=$(cd -- "$app" && pwd -P)
output_parent=${output%/*}
[[ -d $output_parent ]] || fail "the output parent directory must already exist"
output_parent=$(cd -- "$output_parent" && pwd -P)
output="$output_parent/${output##*/}"
[[ $output != "$app/"* ]] || fail "output must be outside the source application"
[[ ! -e $output && ! -L $output ]] || fail "output already exists"

script_dir=$(cd -- "$(/usr/bin/dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repo_root=$(cd -- "$script_dir/../../.." && pwd -P)
plist="$app/Contents/Info.plist"
/usr/bin/plutil -lint "$plist"
read_plist() { /usr/libexec/PlistBuddy -c "Print :$1" "$plist"; }
[[ $(read_plist CFBundleIdentifier) == dev.termlink.desktop.dev ]] || fail "only the independent DEV bundle may be packaged"
[[ $(read_plist CFBundleName) == 'TermLink DEV' && $(read_plist CFBundleDisplayName) == 'TermLink DEV' ]] || fail "invalid DEV bundle name"
[[ $(read_plist TermLinkBuildChannel) == Development ]] || fail "development build channel required"
[[ $(read_plist CFBundleExecutable) == TermLink && $(read_plist CFBundleIconFile) == TermLink.icns ]] || fail "unexpected executable or icon"
version=$(read_plist CFBundleShortVersionString)
build=$(read_plist CFBundleVersion)
[[ $version =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$ && $build =~ ^[1-9][0-9]{0,4}$ ]] || fail "invalid version/build"
IFS=. read -r version_major version_minor version_patch <<< "$version"
[[ $version_major -le 65535 && $version_minor -le 65535 && $version_patch -le 65535 && $build -le 65535 ]] || fail "version/build exceeds its supported range"

[[ -x "$app/Contents/MacOS/TermLink" && -x "$app/Contents/MacOS/agent/TermLink.Agent" ]] || fail "desktop and Agent executables are required"
for dependency in TermLink.Agent.dll TermLink.Agent.deps.json TermLink.Agent.runtimeconfig.json \
  libhostfxr.dylib libhostpolicy.dylib libcoreclr.dylib libtermlink-pty.dylib; do
  [[ -s "$app/Contents/MacOS/agent/$dependency" ]] || fail "self-contained Agent dependency missing: $dependency"
done
/usr/bin/codesign --verify --deep --strict "$app"

# Keep staging on the output filesystem. The final hard link atomically refuses
# an existing output, including one created by another invocation while building.
work_root=$(/usr/bin/mktemp -d "$output_parent/.termlink-dev-pkg.XXXXXX")
trap '/bin/rm -rf -- "$work_root"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
/bin/bash "$repo_root/assets/brand/Export-MacOSAssets.sh" "$work_root/TermLink.icns"
/usr/bin/cmp -s "$work_root/TermLink.icns" "$app/Contents/Resources/TermLink.icns" || fail "bundle icon does not match the original TermLink artwork"

payload_root="$work_root/payload"
staged_app="$payload_root/Applications/TermLink DEV.app"
/bin/mkdir -p "$payload_root/Applications"
# Copy the complete signed bundle, including the private self-contained Agent
# directory. No dependencies are flattened, pruned, or taken from an installed app.
/usr/bin/ditto --norsrc --noextattr --noqtn "$app" "$staged_app"
/usr/bin/codesign --verify --deep --strict "$staged_app"

cat > "$work_root/components.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><array><dict>
<key>RootRelativeBundlePath</key><string>Applications/TermLink DEV.app</string>
<key>BundleIsRelocatable</key><false/>
<key>BundleHasStrictIdentifier</key><true/>
<key>BundleIsVersionChecked</key><false/>
<key>BundleOverwriteAction</key><string>upgrade</string>
</dict></array></plist>
PLIST
candidate="$work_root/TermLink-DEV-unsigned.pkg"
/usr/bin/pkgbuild --root "$payload_root" --component-plist "$work_root/components.plist" \
  --identifier dev.termlink.desktop.dev --version "$version.$build" \
  --install-location / --ownership recommended "$candidate"
if signature_report=$(LC_ALL=C /usr/sbin/pkgutil --check-signature "$candidate" 2>&1); then
  fail "local DEV installer unexpectedly has a package signature"
fi
[[ $signature_report == *'Status: no signature'* ]] || fail "could not confirm the local DEV installer is unsigned"
/bin/ln -- "$candidate" "$output"
echo "Unsigned local DEV installer: $output"
echo "Bundle: dev.termlink.desktop.dev; version/build preserved: $version / $build"
echo "Manual local installation only; not notarized and not eligible for trusted automatic updates."
/usr/bin/shasum -a 256 "$output"
