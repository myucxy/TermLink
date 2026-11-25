#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: sign-dev-app.sh <TermLink-DEV.app> <identity-sha1|->" >&2
  exit 2
fi
app=$1; identity=$2
[[ $(uname -s) == Darwin && $app == /* && -d $app && ! -L $app ]] || { echo "a local macOS DEV bundle is required" >&2; exit 2; }
[[ $identity == - || $identity =~ ^[A-Fa-f0-9]{40}$ ]] || { echo "pin the DEV signing identity by its SHA-1 fingerprint" >&2; exit 2; }
[[ ! -L $app/Contents && ! -L $app/Contents/Info.plist && ! -L $app/Contents/MacOS && ! -L $app/Contents/MacOS/agent && ! -L $app/Contents/MacOS/TermLink && ! -L $app/Contents/MacOS/agent/TermLink.Agent && -f $app/Contents/MacOS/TermLink && -f $app/Contents/MacOS/agent/TermLink.Agent ]] || { echo "DEV UI and Agent entry points are required" >&2; exit 2; }
identifier=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")
channel=$(/usr/libexec/PlistBuddy -c 'Print :TermLinkBuildChannel' "$app/Contents/Info.plist")
[[ $identifier == dev.termlink.desktop.dev && $channel == Development ]] || { echo "only the DEV bundle may use this signing helper" >&2; exit 2; }
if [[ $identity == - ]]; then
  /usr/bin/codesign --force --deep --sign - "$app"
else
  /usr/bin/security find-identity -v -p codesigning | /usr/bin/awk '{ print toupper($2) }' | /usr/bin/grep -Fx -- "$(printf '%s' "$identity" | /usr/bin/tr '[:lower:]' '[:upper:]')" >/dev/null || { echo "the pinned DEV signing identity is unavailable" >&2; exit 3; }
  # The published .NET payload in MacOS includes generic code as well as Mach-O.
  while IFS= read -r -d '' executable; do
    [[ $executable != "$app/Contents/MacOS/TermLink" ]] || continue
    if [[ $executable == "$app/Contents/MacOS/agent/TermLink.Agent" ]]; then
      /usr/bin/codesign --force --timestamp=none --identifier dev.termlink.agent.dev --sign "$identity" "$executable"
    else
      /usr/bin/codesign --force --timestamp=none --sign "$identity" "$executable"
    fi
  done < <(/usr/bin/find "$app/Contents/MacOS" -type f -print0)
  /usr/bin/codesign --force --timestamp=none --sign "$identity" "$app"
fi
/usr/bin/codesign --verify --deep --strict "$app"
