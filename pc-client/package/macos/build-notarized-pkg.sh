#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 9 ]]; then
  echo "usage: build-notarized-pkg.sh <version> <build> <rid> <dotnet> <app-identity> <installer-identity> <team-id> <notary-profile> <output.pkg>" >&2
  exit 2
fi
version=$1; build=$2; rid=$3; dotnet_path=$4; app_identity=$5
installer_identity=$6; team_id=$7; notary_profile=$8; output=$9
[[ $(uname -s) == Darwin ]] || { echo "signed/notarized macOS packages require a macOS host" >&2; exit 3; }
[[ $rid == osx-x64 || $rid == osx-arm64 ]] || { echo "invalid macOS RID" >&2; exit 2; }
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && $build =~ ^[1-9][0-9]{0,4}$ ]] || { echo "invalid version/build" >&2; exit 2; }
[[ $team_id =~ ^[A-Z0-9]{10}$ ]] || { echo "invalid Apple team ID" >&2; exit 2; }
for tool in codesign pkgbuild productbuild xcrun security cc; do command -v "$tool" >/dev/null || { echo "$tool is unavailable" >&2; exit 3; }; done
[[ -x $dotnet_path ]] || { echo "pinned dotnet is unavailable" >&2; exit 3; }
security find-identity -v -p codesigning | grep -F -- "$app_identity" >/dev/null || { echo "Developer ID Application identity unavailable" >&2; exit 4; }
security find-identity -v -p basic | grep -F -- "$installer_identity" >/dev/null || { echo "Developer ID Installer identity unavailable" >&2; exit 4; }

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../../.." && pwd)
work_root=$(mktemp -d)
trap 'rm -rf -- "$work_root"' EXIT
app="$work_root/TermLink.app"
contents="$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources"
"$dotnet_path" publish "$repo_root/pc-client/src/Desktop/TermLink.Desktop.csproj" \
  -c Release -f net8.0 -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:FileVersion="$version.$build" -o "$contents/MacOS"
"$dotnet_path" publish "$repo_root/pc-client/src/Agent.Host/TermLink.Agent.Host.csproj" \
  -c Release -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:FileVersion="$version.$build" -o "$contents/MacOS/agent"
"$repo_root/pc-client/native/posix/build-native.sh" "$contents/MacOS"
mv -- "$contents/MacOS/agent/TermLink.Agent" "$contents/MacOS/TermLink.Agent"
cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.termlink.desktop</string>
<key>CFBundleName</key><string>TermLink</string>
<key>CFBundleExecutable</key><string>TermLink</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$build</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
find "$contents/MacOS" -type f -perm -111 -print0 | while IFS= read -r -d '' executable; do
  codesign --force --timestamp --options runtime --sign "$app_identity" "$executable"
done
codesign --force --timestamp --options runtime --sign "$app_identity" "$app"
codesign --verify --deep --strict --verbose=4 "$app"
spctl --assess --type execute --verbose=4 "$app"

component="$work_root/TermLink-component.pkg"
mkdir -p "$work_root/pkgroot/Applications"
cp -R -- "$app" "$work_root/pkgroot/Applications/TermLink.app"
pkgbuild --root "$work_root/pkgroot" --identifier dev.termlink.desktop --version "$version.$build" \
  --install-location / --sign "$installer_identity" "$component"
mkdir -p -- "$(dirname -- "$output")"
productbuild --package "$component" --sign "$installer_identity" "$output"
pkgutil --check-signature "$output"
xcrun notarytool submit "$output" --keychain-profile "$notary_profile" --team-id "$team_id" --wait
xcrun stapler staple "$output"
xcrun stapler validate "$output"
spctl --assess --type install --verbose=4 "$output"
shasum -a 256 "$output"
