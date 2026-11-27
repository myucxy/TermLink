#!/usr/bin/env bash
# Resolve the version, build and source commit for a server release.
set -euo pipefail
export LC_ALL=C
ref=${1:?expected server tag}
expected=${2:?expected checked out commit}
part='(0|[1-9][0-9]*)'
[[ "$ref" =~ ^server-v${part}\.${part}\.${part}$ ]] || { echo 'Expected server-vX.Y.Z' >&2; exit 1; }
[[ $(git cat-file -t "refs/tags/$ref") == tag ]] || { echo 'An annotated server tag is required' >&2; exit 1; }
[[ $(git rev-parse "refs/tags/$ref^{commit}") == "$expected" && $(git rev-parse HEAD) == "$expected" ]] || { echo 'Tag and checkout revisions differ' >&2; exit 1; }
build=$(git for-each-ref --format='%(contents:subject)' "refs/tags/$ref")
[[ "$build" =~ ^build=([1-9][0-9]{0,4})$ ]] || { echo 'Set the tag annotation subject to build=N' >&2; exit 1; }
build=${BASH_REMATCH[1]}
((build <= 65535)) || { echo 'Build number exceeds 65535' >&2; exit 1; }
printf 'version=%s\nbuild=%s\nrevision=%s\n' "${ref#server-v}" "$build" "$expected"
