# Docker Hub latest synchronization — 2026-09-24

The user requested automatic `latest` publication and immediate synchronization
to the newest existing Server image. This is an alias update, not a new build or
deployment. The existing Docker Hub `v0.2.61` image was copied by immutable digest
using `docker buildx imagetools create --prefer-index=false`; the version tag was
not changed.

- Registry: `docker.io/myucxy/termlink`.
- Tags after synchronization: `v0.2.61` and `latest`.
- Both digests: `sha256:68611748731e2039ce6f1411fd123983deed2577b07e28b657ed9abcd3e90be2`.
- Previous `latest`: `sha256:83d4e28e59e2546076b62aed8b0b28065c6e4014de3d0141c8250a0859f5120b`.
- Source version label: `0.2.61+117`.
- Source revision: `9be56278d83c5ab1b2bd26e6fa34417f4db9b579`.
- Source platform: **linux/amd64 only**.
- Docker Hub update time: 2026-09-24 14:30:10 UTC (22:30:10 CST).

Source manifest/config and OCI labels were inspected before publication; the
config digest matches the previously tested local Server image
`sha256:6f16ac1c9bfa5d6f6822aa7902dafeb0c057b50768b2816ec0e1cd505fc46127`.
Registry and public Docker Hub responses both confirm the new alias digest;
the version tag still has its original digest.

This retains the existing registry artifact's limitations: it does not publish
the separate local multiarch OCI archive, add arm64, create new SBOM/provenance,
or establish Cosign verification. It is not a new qualified multiarch release.
No GitHub image/Release was published. The local Server remains healthy without
a restart or configuration change.

Workflow change `9738830` serializes Server releases and promotes the exact
build digest to `latest` only after existing signature/attestation checks pass,
then verifies equality. Metadata generation intentionally excludes `latest`
at the initial build step. Shell syntax and checks for exact-digest promotion
and rejection of a mismatching result passed. The full future GitHub build/sign
workflow was not triggered for this alias synchronization.

Evidence: `artifacts/validation/server-latest-*`, including source preflight,
remote config, dry run, shell checks, push log, promotion result and public Hub
response. Direct registry access hit the known DNS issue; the existing proxy
was used only in the command process. No system DNS/proxy setting changed.

For rollback, deploy the desired historical version by its immutable digest.
If explicitly asked to restore the old alias, copy the previous `latest` digest
above using the same command and verify it; do not rewrite a version tag.
