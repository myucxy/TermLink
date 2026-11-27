#!/usr/bin/env bash
set -euo pipefail
parser=$(cd "$(dirname "$0")" && pwd)/tag-build.sh
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
cd "$tmp"
git init -q
git config user.name CI
git config user.email ci@example.invalid
git commit -q --allow-empty -m first
rev=$(git rev-parse HEAD)
check_rejected() {
  if bash "$parser" "$1" "$2" >/dev/null 2>&1; then echo "unexpectedly accepted $1" >&2; exit 1; fi
}
git tag -a server-v1.2.3 -m 'build=149'
grep -Fx 'build=149' <<< "$(bash "$parser" server-v1.2.3 "$rev")"
git tag server-v1.2.4
check_rejected server-v1.2.4 "$rev"
git tag -a server-v01.2.5 -m 'build=150'
check_rejected server-v01.2.5 "$rev"
git tag -a server-v1.2.6 -m 'build=65536'
check_rejected server-v1.2.6 "$rev"
git commit -q --allow-empty -m second
check_rejected server-v1.2.3 "$(git rev-parse HEAD)"
