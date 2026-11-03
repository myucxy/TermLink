#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "usage: build-dev-app.sh <version> <build> <dotnet> <protoc> <output-directory>" >&2
  exit 2
fi
version=$1; build=$2; dotnet_path=$3; protoc_path=$4; output=$5
source_revision=${TERMLINK_SOURCE_REVISION:-unknown}
[[ $source_revision == unknown || $source_revision =~ ^[a-f0-9]{7,40}$ ]] || { echo "invalid source revision" >&2; exit 2; }
[[ $(uname -s) == Darwin ]] || { echo "macOS host required" >&2; exit 3; }
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && $build =~ ^[1-9][0-9]{0,4}$ && $build -le 65535 ]] || { echo "invalid version/build" >&2; exit 2; }
[[ -x $dotnet_path && -x $protoc_path && $output == /* && ! -e $output ]] || { echo "explicit tools and a new absolute output directory are required" >&2; exit 2; }
case $(uname -m) in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "unsupported macOS architecture" >&2; exit 3 ;;
esac
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/../../.." && pwd)
work_root=$(mktemp -d "${TMPDIR:-/tmp}/termlink-mac-dev.XXXXXX")
trap 'rm -rf -- "$work_root"' EXIT
contents="$work_root/TermLink DEV.app/Contents"
mkdir -p "$contents/MacOS/agent" "$contents/Resources"
args=(-c Debug -r "$rid" --self-contained true -p:RestoreLockedMode=true -p:TermLinkDevelopmentBuild=true
  -p:EnableWindowsTargeting=true "-p:Version=$version" "-p:AssemblyVersion=$version.$build" "-p:FileVersion=$version.$build"
  "-p:InformationalVersion=$version-dev+$source_revision"
  "-p:Protobuf_ProtocFullPath=$protoc_path")
"$dotnet_path" publish "$repo_root/pc-client/src/Desktop/TermLink.Desktop.csproj" \
  -f net8.0 "${args[@]}" -o "$contents/MacOS"
"$dotnet_path" publish "$repo_root/pc-client/src/Agent.Host/TermLink.Agent.Host.csproj" \
  "${args[@]}" -o "$contents/MacOS/agent"
bash "$repo_root/pc-client/native/posix/build-native.sh" "$contents/MacOS"
cp "$contents/MacOS/libtermlink-pty.dylib" "$contents/MacOS/agent/"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$contents/Resources/"
bash "$repo_root/assets/brand/Export-MacOSAssets.sh" "$contents/Resources/TermLink.icns"
cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.termlink.desktop.dev</string>
<key>CFBundleName</key><string>TermLink DEV</string>
<key>CFBundleDisplayName</key><string>TermLink DEV</string>
<key>CFBundleExecutable</key><string>TermLink</string>
<key>CFBundleIconFile</key><string>TermLink.icns</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$build</string>
<key>TermLinkBuildChannel</key><string>Development</string>
<key>TermLinkSourceRevision</key><string>$source_revision</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
plutil -lint "$contents/Info.plist"
# Local development signature only; this is not a notarized release package.
codesign --force --deep --sign - "$work_root/TermLink DEV.app"
codesign --verify --deep --strict "$work_root/TermLink DEV.app"
mkdir -p "$output"
ditto --norsrc --noextattr --noqtn "$work_root/TermLink DEV.app" "$output/TermLink DEV.app"
ditto -c -k --norsrc --noextattr --noqtn --keepParent "$output/TermLink DEV.app" "$output/TermLink-DEV-$rid.zip"
(cd -- "$output" && shasum -a 256 "TermLink-DEV-$rid.zip" > SHA256SUMS)
echo "DEV application: $output/TermLink DEV.app"
