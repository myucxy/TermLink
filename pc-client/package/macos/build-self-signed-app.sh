#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "usage: build-self-signed-app.sh <version> <build> <rid> <dotnet> <certificate-sha1> <keychain|-> <output.zip>" >&2
  exit 2
fi

version=$1
build=$2
rid=$3
dotnet_path=$4
certificate_sha1=$5
keychain=$6
output=$7

[[ $(uname -s) == Darwin ]] || { echo "self-signed macOS packaging requires a macOS host" >&2; exit 3; }
[[ $rid == osx-x64 || $rid == osx-arm64 ]] || { echo "invalid macOS RID" >&2; exit 2; }
[[ $version =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$ && $build =~ ^[1-9][0-9]{0,4}$ && $build -le 65535 ]] || { echo "invalid version/build" >&2; exit 2; }
IFS=. read -r version_major version_minor version_patch <<< "$version"
[[ $version_major -le 65535 && $version_minor -le 65535 && $version_patch -le 65535 ]] || { echo "version exceeds assembly version limits" >&2; exit 2; }
[[ $certificate_sha1 =~ ^[A-Fa-f0-9]{40}$ ]] || { echo "certificate SHA-1 must be 40 hexadecimal characters" >&2; exit 2; }
[[ $output == /* && $output == *.zip && ! -e $output && ! -L $output ]] || { echo "a new absolute ZIP output path is required" >&2; exit 2; }
if [[ $keychain != - ]]; then
  [[ $keychain == /* && -f $keychain ]] || { echo "keychain must be an existing absolute path or -" >&2; exit 3; }
fi
case $(uname -m):$rid in
  arm64:osx-arm64|x86_64:osx-x64) ;;
  *) echo "native PTY packaging requires a matching host architecture" >&2; exit 3 ;;
esac
for tool in codesign security ditto shasum file lipo otool; do
  command -v "$tool" >/dev/null || { echo "$tool is unavailable" >&2; exit 3; }
done
[[ -x $dotnet_path ]] || { echo "pinned dotnet is unavailable" >&2; exit 3; }

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../../.." && pwd)
protocol_args=()
if [[ -n ${TERMLINK_PROTOC:-} ]]; then
  [[ -x $TERMLINK_PROTOC ]] || { echo "TERMLINK_PROTOC is not executable" >&2; exit 3; }
  protocol_args+=("-p:Protobuf_ProtocFullPath=$TERMLINK_PROTOC")
fi

keychain_args=()
identity_args=()
identity_query=()
if [[ $keychain != - ]]; then
  [[ -f $keychain ]] || { echo "keychain was not found" >&2; exit 3; }
  keychain_args+=(--keychain "$keychain")
  identity_query+=("$keychain")
fi
certificate_sha1=$(printf '%s' "$certificate_sha1" | tr '[:lower:]' '[:upper:]')
identity_args+=(--sign "$certificate_sha1")
bundle_requirement='identifier "dev.termlink.desktop" and certificate leaf = H"'"$certificate_sha1"'"'
identity_lines=$(security find-identity -v -p codesigning "${identity_query[@]}" 2>/dev/null || true)
printf '%s\n' "$identity_lines" | awk '{print toupper($2)}' | grep -Fx -- "$certificate_sha1" >/dev/null || {
  echo "the pinned self-signing identity is unavailable in the selected keychain" >&2
  exit 4
}

mkdir -p -- "$(dirname -- "$output")"
work_root=$(mktemp -d "$(dirname -- "$output")/.termlink-self-signed.XXXXXX")
cleanup() { rm -rf -- "$work_root"; }
trap cleanup EXIT
app="$work_root/TermLink.app"
contents="$app/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources"

"$dotnet_path" publish "$repo_root/pc-client/src/Desktop/TermLink.Desktop.csproj" \
  -c Release -f net8.0 -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:EnableWindowsTargeting=true -p:Version="$version" -p:AssemblyVersion="$version.$build" -p:FileVersion="$version.$build" \
  -p:TermLinkDevelopmentBuild=false -p:TermLinkMacUpdatesEnabled=false \
  -o "$contents/MacOS" "${protocol_args[@]}"
"$dotnet_path" publish "$repo_root/pc-client/src/Agent.Host/TermLink.Agent.Host.csproj" \
  -c Release -r "$rid" --self-contained true -p:RestoreLockedMode=true \
  -p:Version="$version" -p:AssemblyVersion="$version.$build" -p:FileVersion="$version.$build" \
  -p:TermLinkDevelopmentBuild=false -p:TermLinkMacUpdatesEnabled=false \
  -o "$contents/MacOS/agent" "${protocol_args[@]}"

smoke_args=()
if [[ -n ${TERMLINK_MACOS_NATIVE_SMOKE_SHA256:-} ]]; then
  smoke_args+=(--same-host-smoke-sha256 "$TERMLINK_MACOS_NATIVE_SMOKE_SHA256")
fi
python3 "$script_dir/verify-terminal-core.py" "$repo_root" "$rid" --stage "$contents/MacOS/agent" "${smoke_args[@]}"
cp "$repo_root/terminal-core.macos.lock.json" "$contents/Resources/"
cp -R "$repo_root/native/terminal-core/macos/licenses" "$contents/Resources/terminal-core-licenses"
bash "$repo_root/pc-client/native/posix/build-native.sh" "$contents/MacOS"
cp -- "$contents/MacOS/libtermlink-pty.dylib" "$contents/MacOS/agent/"
bash "$repo_root/pc-client/native/macos/build-authorization.sh" "$contents/MacOS"
bash "$repo_root/pc-client/native/macos/build-files.sh" "$contents/MacOS/agent"
bash "$repo_root/assets/brand/Export-MacOSAssets.sh" "$contents/Resources/TermLink.icns"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$contents/Resources/"

cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.termlink.desktop</string>
<key>CFBundleName</key><string>TermLink</string>
<key>CFBundleDisplayName</key><string>TermLink</string>
<key>CFBundleExecutable</key><string>TermLink</string>
<key>CFBundleIconFile</key><string>TermLink.icns</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$build</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>LSUIElement</key><true/>
<key>TermLinkSelfSigned</key><true/>
<key>TermLinkSigningCertificateSHA1</key><string>$certificate_sha1</string>
</dict></plist>
PLIST

sign_macho() {
  local item=$1
  if [[ $item == "$contents/MacOS/TermLink" || $item == "$contents/MacOS/agent/TermLink.Agent" ]]; then
    codesign --force --timestamp=none --options runtime --entitlements "$script_dir/runtime-entitlements.plist" "${keychain_args[@]}" "${identity_args[@]}" "$item"
  else
    codesign --force --timestamp=none "${keychain_args[@]}" "${identity_args[@]}" "$item"
  fi
}
while IFS= read -r -d '' executable; do
  if file -b "$executable" | grep -q 'Mach-O'; then
    sign_macho "$executable"
  else
    codesign --force --timestamp=none "${keychain_args[@]}" "${identity_args[@]}" "$executable"
  fi
done < <(find "$contents/MacOS" -type f -print0)
codesign --force --timestamp=none --options runtime --entitlements "$script_dir/runtime-entitlements.plist" "${keychain_args[@]}" "${identity_args[@]}" --requirements "designated => $bundle_requirement" "$app"
codesign --verify --deep --strict --verbose=4 -R "$bundle_requirement" "$app"

ditto -c -k --sequesterRsrc --noqtn --keepParent "$app" "$work_root/TermLink-$rid.zip"
ditto -x -k "$work_root/TermLink-$rid.zip" "$work_root/zip-validation"
roundtrip="$work_root/zip-validation/TermLink.app"
codesign --verify --deep --strict -R "$bundle_requirement" "$roundtrip"
[[ $(lipo -archs "$roundtrip/Contents/MacOS/TermLink") == "$(uname -m)" ]]
[[ $(lipo -archs "$roundtrip/Contents/MacOS/agent/TermLink.Agent") == "$(uname -m)" ]]
ln -- "$work_root/TermLink-$rid.zip" "$output"
shasum -a 256 "$output"
