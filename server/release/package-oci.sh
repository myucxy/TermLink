#!/usr/bin/env bash
# Called from the root of a clean checkout on the Linux release runner.
set -euo pipefail
: "${VERSION:?}" "${BUILD:?}" "${REVISION:?}" "${RUNNER_TEMP:?}"
[[ $(git rev-parse HEAD) == "$REVISION" && -z $(git status --porcelain) ]]
out="$RUNNER_TEMP/server-release"
mkdir "$out"
prefix="TermLink-Server-$VERSION-$BUILD"
archive="$prefix.oci.tar"
source_archive="$prefix-source.tar.gz"
epoch=$(git show -s --format=%ct "$REVISION")
created=$(date -u -d "@$epoch" +'%Y-%m-%dT%H:%M:%SZ')
metadata="$RUNNER_TEMP/server-buildkit.json"
docker buildx build --platform linux/amd64,linux/arm64 --file server/Dockerfile \
  --provenance=mode=max --sbom=true --metadata-file "$metadata" \
  --build-arg "VERSION=$VERSION+$BUILD" --build-arg "REVISION=$REVISION" \
  --build-arg "CREATED=$created" --build-arg "SOURCE_DATE_EPOCH=$epoch" \
  --tag "termlink-server:$VERSION-build.$BUILD" \
  --output "type=oci,dest=$out/$archive,oci-mediatypes=true,rewrite-timestamp=true" .
python3 server/release/verify_oci_archive.py "$out/$archive" "$metadata" "$VERSION" "$BUILD" "$REVISION" > "$RUNNER_TEMP/server-oci.json"

# Include the editable frontend behind the embedded web assets, build tools and locked Go sources.
source_root=$(mktemp -d "$RUNNER_TEMP/termlink-source.XXXXXXXX")
git archive "$REVISION" -- LICENSE NOTICE server ai-web protocol testdata docs/licenses scripts .github/workflows | tar -x -C "$source_root"
(cd "$source_root/server" && go mod vendor)
tar --sort=name --mtime="@$epoch" --owner=0 --group=0 --numeric-owner -C "$source_root" -cf - . | gzip -n > "$out/$source_archive"
cd "$out"
jq --arg tag "server-v$VERSION" --arg source "$source_archive" \
  --arg sha "$(sha256sum "$source_archive" | cut -d' ' -f1)" \
  '. + {schema_version: 1, kind: "termlink-server-release", tag: $tag, source_revision: .revision,
        unsigned: true, local_signing_required: true, oci_archive: .archive, oci_archive_sha256: .archive_sha256,
        source_archive: $source, source_archive_sha256: $sha} | del(.revision,.archive,.archive_sha256)' \
  "$RUNNER_TEMP/server-oci.json" > server-release.json
sha256sum "$archive" "$source_archive" server-release.json > SHA256SUMS
