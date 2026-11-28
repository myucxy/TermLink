#!/usr/bin/env bash
set -euo pipefail
umask 077
: "${RUNNER_TEMP:?}" "${DOTNET_ROOT:?}" "${CLIENT_VERSION:?}" "${CLIENT_BUILD:?}" "${CLIENT_REVISION:?}" "${CLIENT_RID:?}"
: "${GH_MACOS_SIGNING_P12_B64:?}" "${GH_MACOS_SIGNING_P12_PASSWORD:?}"
[[ ${RUNNER_OS:-} == macOS && ${GITHUB_ACTIONS:-} == true ]]
work=$(mktemp -d "$RUNNER_TEMP/termlink-signing.XXXXXX")
keychain="$work/signing.keychain-db"
certificate="$work/signer.cer"
previous_keychains=()
while IFS= read -r line; do
  line=${line#*\"}
  line=${line%\"*}
  if [[ -n $line ]]; then previous_keychains+=("$line"); fi
done < <(/usr/bin/security list-keychains -d user)
cleanup() {
  /usr/bin/security list-keychains -d user -s "${previous_keychains[@]}" >/dev/null 2>&1 || true
  if [[ -f $certificate ]]; then
    sudo /usr/bin/security remove-trusted-cert -d "$certificate" >/dev/null 2>&1 || true
  fi
  /usr/bin/security delete-keychain "$keychain" >/dev/null 2>&1 || true
  rm -f -- "$work/signer.p12" "$certificate" "$work/signing-probe"
  rmdir -- "$work" 2>/dev/null || true
}
trap cleanup EXIT

policy=release/github-macos-signing-policy.json
pin=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["signer_sha1"])' "$policy")
sha256=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["signer_sha256"])' "$policy")
[[ $pin =~ ^[0-9a-f]{40}$ && $sha256 =~ ^[0-9a-f]{64}$ ]]
keypass=$(openssl rand -hex 32)
printf '::add-mask::%s\n' "$keypass"
printf '%s' "$GH_MACOS_SIGNING_P12_B64" | base64 -D > "$work/signer.p12"
unset GH_MACOS_SIGNING_P12_B64
/usr/bin/security create-keychain -p "$keypass" "$keychain"
/usr/bin/security set-keychain-settings -lut 2700 "$keychain"
/usr/bin/security unlock-keychain -p "$keypass" "$keychain"
/usr/bin/security list-keychains -d user -s "$keychain" "${previous_keychains[@]}"
/usr/bin/security import "$work/signer.p12" -k "$keychain" -f pkcs12 -P "$GH_MACOS_SIGNING_P12_PASSWORD" -T /usr/bin/codesign
unset GH_MACOS_SIGNING_P12_PASSWORD
rm -f -- "$work/signer.p12"
/usr/bin/security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keypass" "$keychain" >/dev/null
/usr/bin/security find-certificate -c 'TermLink macOS Release' -p "$keychain" | openssl x509 -outform der > "$certificate"
printf '%s  %s\n' "$sha256" "$certificate" | shasum -a 256 --check
# The dedicated ephemeral runner trusts only the pinned certificate for signing.
sudo /usr/bin/security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$certificate"
# Check the same host session can actually sign before investing in a full build.
printf '%s\n' 'int main(void) { return 0; }' | clang -x c - -o "$work/signing-probe"
/usr/bin/codesign --force --timestamp=none --keychain "$keychain" --sign "$pin" "$work/signing-probe"
/usr/bin/codesign --verify --strict -R "certificate leaf = H\"$pin\"" "$work/signing-probe"
rm -f -- "$work/signing-probe"

out="$RUNNER_TEMP/macos-release"
mkdir -p "$out"
zip="$out/TermLink-macOS-$CLIENT_VERSION-$CLIENT_BUILD-$CLIENT_RID.zip"
bash pc-client/package/macos/build-self-signed-app.sh "$CLIENT_VERSION" "$CLIENT_BUILD" "$CLIENT_RID" "$DOTNET_ROOT/dotnet" "$pin" "$keychain" "$zip"

python3 - "$zip" "$policy" "$out/macos-$CLIENT_RID-verification.json" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

archive, policy_path, evidence_path = map(Path, sys.argv[1:])
policy = json.loads(policy_path.read_text())
lock = json.loads(Path('terminal-core.macos.lock.json').read_text())
revision = os.environ['CLIENT_REVISION']
version = os.environ['CLIENT_VERSION']
build = int(os.environ['CLIENT_BUILD'])
rid = os.environ['CLIENT_RID']
assert revision == subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
assert lock['source_dirty'] is False
(library,) = [x for x in lock['libraries'] if x['rid'] == rid]
assert os.environ.get('TERMLINK_MACOS_NATIVE_SMOKE_SHA256') == library['sha256']
with tempfile.TemporaryDirectory(dir=os.environ['RUNNER_TEMP']) as temp:
    subprocess.run(['/usr/bin/ditto', '-x', '-k', str(archive), temp], check=True)
    app = Path(temp)/'TermLink.app'
    import plistlib
    with (app/'Contents/Info.plist').open('rb') as stream:
        info = plistlib.load(stream)
    assert info['CFBundleIdentifier'] == policy['bundle_id']
    assert info['CFBundleShortVersionString'] == version and info['CFBundleVersion'] == str(build)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)
    subprocess.run(['/usr/bin/codesign', '--display', '--extract-certificates', str(app)], cwd=temp, check=True)
    leaf = (Path(temp)/'codesign0').read_bytes()
    assert hashlib.sha1(leaf).hexdigest() == policy['signer_sha1']
    assert hashlib.sha256(leaf).hexdigest() == policy['signer_sha256']
with archive.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
evidence = dict(schema_version=1, verified=True, notarized=False, rid=rid, bundle_id=policy['bundle_id'],
                source_revision=revision, core_revision=lock['revision'], version=version, build_number=build,
                filename=archive.name, size=archive.stat().st_size, sha256=digest,
                signer_sha1=policy['signer_sha1'], signer_sha256=policy['signer_sha256'])
evidence_path.write_text(json.dumps(evidence, indent=2) + '\n')
print(json.dumps(evidence))
PY
