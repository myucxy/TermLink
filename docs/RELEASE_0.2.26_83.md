# TermLink 0.2.26 (83) release record

Date: 2026-09-03 (Asia/Shanghai)

## Source identity

- Release source commit: `52771e87c462fae054aadd8c16e8bf9d92124491`
- `origin/main` matched the release source commit before building.
- Annotated tag `v0.2.26` was created and pushed for that commit.
- Per the user's instruction, the GitHub/Docker Hub release workflow is not
  monitored or handled further. The Docker Hub `0.2.26` tag was not present at
  the final pre-stop registry check, so this record does not claim that the
  official multi-architecture image was published.

## Client artifacts and AppShip

Both artifacts came from the same promoted Production manifest at
`artifacts/release/0.2.26-83/artifact-manifest.json`.

### Android

- Artifact: `TermLink-Android-0.2.26-83.apk`
- Size: `36243724` bytes
- SHA-256: `5b777253fc497eacd8edcd46c5653f105990fc60a9d2e1a3ebacc80b2501420d`
- Application ID: `dev.termlink.harness`
- Version: `0.2.26 (83)`
- APK signature scheme: v2
- Signer SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`
- AppShip application ID: `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`
- AppShip release ID: `d6a496bd-c88c-4a96-b170-0d364d5242dc`
- AppShip artifact ID: `4c1345a4-2a0b-4cba-a9a6-3dba8051a35e`
- Final AppShip state: release `published`, artifact `ready`, download enabled
- Resumable state: `.appship/termlink-android-stable-0.2.26-build83-signature-approved.json`

### Windows x64

- Artifact: `TermLink-Setup-0.2.26.83-x64.exe`
- Size: `256728744` bytes
- SHA-256: `ab3c4a4caccde9b51c97e33825ebb64f43f53f9304a6e16ce7c96faa97371224`
- Product version: `0.2.26.83+52771e87c462fae054aadd8c16e8bf9d92124491`
- File version: `0.2.26.83`
- Authenticode status: valid
- Signer SHA-1: `91ae60bc06d866628b8eb2c62f32bde9dab56466`
- RFC 3161 timestamp: present and valid
- Signed payload binaries verified: 17
- AppShip application ID: `0ae760b3-294e-48e8-a793-95d162f685e2`
- AppShip release ID: `196b6173-44b1-4ac6-9764-bbac82158d1a`
- AppShip artifact ID: `3ff565bd-bdff-4dd1-bac3-738c34def336`
- Final AppShip state: release `published`, artifact `ready`, download enabled
- Resumable state: `.appship/termlink-win64-stable-0.2.26-build83-signature-approved.json`

For both applications, build 82 returned `optional` with build 83 as the
update, while build 83 returned `no_update`.

## Server deployment

- Local production image: `myucxy/termlink:0.2.26-local-production`
- Local image ID: `sha256:47a4118205f1c178be3f9a468db52cfa9c3ae00bfec2c6a8b49c45ee6206d1eb`
- Platform: `linux/amd64`
- OCI version: `0.2.26`
- OCI revision: `52771e87c462fae054aadd8c16e8bf9d92124491`
- Deployment entry point: `Start-ProductionAcceptance.ps1` with
  `-LocalServerImage` and `-EnableDirectTransportShadow`
- `-Reset` was not used.
- The production OIDC/TLS/API/Realtime acceptance probe passed.
- Eight resident services, including the Direct Valkey service, were healthy.
- Database migration version matched the repository maximum: `37/37`.
- Public `https://termlink.kget.cc:8910/readyz` returned HTTP 200 with
  `{"status":"ready"}` after deployment.

There were no TermLink production volumes before startup. The startup created
new empty volumes; it did not delete or overwrite an existing volume during
this release operation.

## Validation reuse

Functional and regression evidence is inherited from
`docs/VALIDATION_BASELINE_2026-09-03.md`. Already-passing scenarios were not
repeated. Only new artifact signature/manifest verification and post-deployment
health gates were run.
