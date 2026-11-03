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
[[ $version =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$ && $build =~ ^[1-9][0-9]{0,4}$ && $build -le 65535 ]] || { echo "invalid version/build" >&2; exit 2; }
IFS=. read -r version_major version_minor version_patch <<< "$version"
[[ $version_major -le 65535 && $version_minor -le 65535 && $version_patch -le 65535 ]] || { echo "version exceeds assembly version limits" >&2; exit 2; }
[[ $team_id =~ ^[A-Z0-9]{10}$ ]] || { echo "invalid Apple team ID" >&2; exit 2; }
[[ $app_identity == "Developer ID Application: "*" ($team_id)" && $installer_identity == "Developer ID Installer: "*" ($team_id)" ]] || { echo "both Developer ID identities must belong to the pinned team" >&2; exit 2; }
[[ $output == /* && $output == *.pkg && ! -e $output && ! -L $output ]] || { echo "a new absolute PKG output path is required" >&2; exit 2; }
case $(uname -m):$rid in
  arm64:osx-arm64|x86_64:osx-x64) ;;
  *) echo "native PTY packaging requires a matching host architecture" >&2; exit 3 ;;
esac
for tool in codesign pkgbuild productbuild xcrun security cc; do command -v "$tool" >/dev/null || { echo "$tool is unavailable" >&2; exit 3; }; done
[[ -x $dotnet_path ]] || { echo "pinned dotnet is unavailable" >&2; exit 3; }
security find-identity -v -p codesigning | grep -F -- "$app_identity" >/dev/null || { echo "Developer ID Application identity unavailable" >&2; exit 4; }
security find-identity -v -p basic | grep -F -- "$installer_identity" >/dev/null || { echo "Developer ID Installer identity unavailable" >&2; exit 4; }

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../../.." && pwd)
protocol_args=()
if [[ -n ${TERMLINK_PROTOC:-} ]]; then
  [[ -x $TERMLINK_PROTOC ]] || { echo "TERMLINK_PROTOC is not executable" >&2; exit 3; }
  protocol_args+=("-p:Protobuf_ProtocFullPath=$TERMLINK_PROTOC")
fi
mkdir -p -- "$(dirname -- "$output")"
work_root=$(mktemp -d "$(dirname -- "$output")/.termlink-pkg.XXXXXX")
trap 'rm -rf -- "$work_root"' EXIT
app="$work_root/TermLink.app"
contents="$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources"
"$dotnet_path" publish "$repo_root/pc-client/src/Desktop/TermLink.Desktop.csproj" \
  -c Release -f net8.0 -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:EnableWindowsTargeting=true -p:Version="$version" -p:AssemblyVersion="$version.$build" -p:FileVersion="$version.$build" \
  -p:TermLinkDevelopmentBuild=false -p:TermLinkMacUpdatesEnabled=true -p:TermLinkMacSigningTeamId="$team_id" -o "$contents/MacOS" "${protocol_args[@]}"
"$dotnet_path" publish "$repo_root/pc-client/src/Agent.Host/TermLink.Agent.Host.csproj" \
  -c Release -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:AssemblyVersion="$version.$build" -p:FileVersion="$version.$build" \
  -p:TermLinkDevelopmentBuild=false -p:TermLinkMacUpdatesEnabled=true -p:TermLinkMacSigningTeamId="$team_id" -o "$contents/MacOS/agent" "${protocol_args[@]}"
bash "$repo_root/pc-client/native/posix/build-native.sh" "$contents/MacOS"
cp -- "$contents/MacOS/libtermlink-pty.dylib" "$contents/MacOS/agent/"
bash "$repo_root/assets/brand/Export-MacOSAssets.sh" "$contents/Resources/TermLink.icns"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$contents/Resources/"
cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.termlink.desktop</string>
<key>CFBundleName</key><string>TermLink</string>
<key>CFBundleExecutable</key><string>TermLink</string>
<key>CFBundleIconFile</key><string>TermLink.icns</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$build</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
find "$contents/MacOS" -type f -print0 | while IFS= read -r -d '' executable; do
  if file -b "$executable" | grep -q 'Mach-O'; then
    if [[ $executable == "$contents/MacOS/TermLink" || $executable == "$contents/MacOS/agent/TermLink.Agent" ]]; then
      codesign --force --timestamp --options runtime --entitlements "$script_dir/runtime-entitlements.plist" --sign "$app_identity" "$executable"
    else
      codesign --force --timestamp --options runtime --sign "$app_identity" "$executable"
    fi
  fi
done
codesign --force --timestamp --options runtime --entitlements "$script_dir/runtime-entitlements.plist" --sign "$app_identity" "$app"
codesign --verify --deep --strict --verbose=4 "$app"
# Notarize and staple the app itself before assessing it or enclosing it in a
# PKG. Stapling only the outer installer leaves offline app assessment missing.
ditto -c -k --keepParent "$app" "$work_root/TermLink-notary.zip"
xcrun notarytool submit "$work_root/TermLink-notary.zip" --keychain-profile "$notary_profile" --team-id "$team_id" --wait
xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=4 "$app"

component="$work_root/TermLink-component.pkg"
mkdir -p "$work_root/pkgroot/Applications"
cp -R -- "$app" "$work_root/pkgroot/Applications/TermLink.app"
cat > "$work_root/components.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><array><dict>
<key>RootRelativeBundlePath</key><string>Applications/TermLink.app</string>
<key>BundleIsRelocatable</key><false/>
<key>BundleHasStrictIdentifier</key><true/>
<key>BundleIsVersionChecked</key><true/>
<key>BundleOverwriteAction</key><string>upgrade</string>
</dict></array></plist>
PLIST
pkgbuild --root "$work_root/pkgroot" --component-plist "$work_root/components.plist" --identifier dev.termlink.desktop --version "$version.$build" \
  --install-location / --sign "$installer_identity" "$component"
candidate="$work_root/TermLink-notarized.pkg"
productbuild --package "$component" --sign "$installer_identity" "$candidate"
pkgutil --check-signature "$candidate"
xcrun notarytool submit "$candidate" --keychain-profile "$notary_profile" --team-id "$team_id" --wait
xcrun stapler staple "$candidate"
xcrun stapler validate "$candidate"
spctl --assess --type install --verbose=4 "$candidate"
ln -- "$candidate" "$output"
shasum -a 256 "$output"
