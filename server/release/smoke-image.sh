#!/usr/bin/env bash
# Runs on a disposable GitHub-hosted runner with a local amd64 image.
set -euo pipefail
image=${1:?expected local image reference}

# Check the licenses in the very image that will be started below.
files=$(docker create "$image")
trap 'docker rm "$files" >/dev/null' EXIT
for name in LICENSE NOTICE licenses/THIRD_PARTY_NOTICES.txt; do
  expected=$name
  [[ $name != licenses/* ]] || expected=server/$name
  docker cp "$files:/$name" - | tar -xO | cmp -- "$expected" -
done

admin_password=$(openssl rand -hex 24)
signing_key=$(openssl rand -hex 32)
echo "::add-mask::$admin_password"
echo "::add-mask::$signing_key"
docker network create termlink-image-ci
# No database host port; trust is limited to this disposable CI network.
docker run -d --name termlink-ci-postgres --network termlink-image-ci --network-alias postgres \
  -e POSTGRES_DB=termlink -e POSTGRES_USER=termlink -e POSTGRES_HOST_AUTH_METHOD=trust \
  postgres:17.5-alpine3.22@sha256:6567bca8d7bc8c82c5922425a0baee57be8402df92bae5eacad5f01ae9544daa
for attempt in $(seq 1 60); do
  # TCP excludes the socket-only temporary server used during first-time initialization.
  if docker exec termlink-ci-postgres pg_isready -h 127.0.0.1 -U termlink -d termlink; then break; fi
  sleep 1
done
docker exec termlink-ci-postgres pg_isready -h 127.0.0.1 -U termlink -d termlink
docker run -d --name termlink-ci-server --network termlink-image-ci -p 127.0.0.1:18080:8080 \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m --cap-drop ALL --security-opt no-new-privileges:true \
  -e TERMLINK_RUNTIME_MODE=single-node -e TERMLINK_IDENTITY_PROVIDER=embedded \
  -e TERMLINK_PUBLIC_URL=https://termlink.example.test \
  -e 'TERMLINK_DATABASE_URL=postgres://termlink@postgres:5432/termlink?sslmode=disable' \
  -e "TERMLINK_INITIAL_ADMIN_PASSWORD=$admin_password" -e "TERMLINK_TOKEN_SIGNING_KEY=$signing_key" \
  -e TERMLINK_DIRECT_TRANSPORT_ENABLED=false -e TERMLINK_DIRECT_ROUTING_PERMITTED=false \
  -e TERMLINK_LOG_FAILURE_REASON=true "$image"
curl --fail --silent --show-error --retry 60 --retry-connrefused --retry-all-errors --retry-delay 1 --max-time 5 \
  http://127.0.0.1:18080/readyz > "$RUNNER_TEMP/readiness.json"
docker exec termlink-ci-server /termlink-server healthcheck
curl --fail --silent --show-error --max-time 10 \
  http://127.0.0.1:18080/realms/termlink/.well-known/openid-configuration > "$RUNNER_TEMP/oidc.json"
python3 - <<'PY'
import json, os
from pathlib import Path
root = Path(os.environ['RUNNER_TEMP'])
assert json.loads((root/'readiness.json').read_text())['status'] == 'ready'
assert json.loads((root/'oidc.json').read_text())['issuer'] == 'https://termlink.example.test/realms/termlink'
PY
docker restart termlink-ci-server
curl --fail --silent --show-error --retry 60 --retry-connrefused --retry-all-errors --retry-delay 1 --max-time 5 http://127.0.0.1:18080/readyz
docker exec termlink-ci-server /termlink-server healthcheck
