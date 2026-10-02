# TermLink 0.2.70 / 126 release evidence

This release contains the combined Windows and Android authentication recovery fix.

## Source and scope

- Android: 0.2.70 / build 126
- Windows: 0.2.70.126
- Server/Docker: not included
- Android source revision: `e2680facc666b650baa3f55c700f2b6b1f983f7d`
- Windows source revision: `c60a24b28539d38a87b9097ec44f288db9666921`; the only changes since the Android source revision are packaging retry logic, its tests, and this document. Product code is unchanged.
- Both builds started from clean, pushed revisions. No Git release tag was created.

## Changes

- Android retries one rejected authenticated request with a fresh token and keeps the original body and idempotency key.
- Android treats only explicit credential or interaction failures as sign-out conditions; transient HTTP, proxy, and service failures remain recoverable.
- Android session lease/control requests use the same authentication refresh and invalidation path as REST catalog requests.
- Windows authentication loss closes Relay, Direct signalling, and Direct data runtimes while retaining local terminal state.
- Windows packaging retries only explicit RFC 3161 service failures (at most four attempts); certificate errors, signature verification, and exhausted retries still stop the build.

## Validation

- Android app unit tests: 325 passed, 0 failed.
- Android core-network unit tests: 133 passed, 0 failed.
- Android lint: 0 errors; existing warnings/hints only.
- Windows production subscriber recovery validation: passed.
- Windows direct transport validation: passed.
- Windows Avalonia headless validation: passed.
- Windows setup prerequisite/ownership validation: passed, including upgrade consent, isolated rollback, process ownership, and cleanup boundaries.
- `pwsh -NoProfile -File release/Test-BuildRelease.ps1`: passed, including the new bounded timestamp retry cases and the existing signature, RFC 3161, manifest, and AppShip gates.
- Independent `Verify-LocalRelease.ps1`: passed for the combined Production manifest; Android has one approved v2 signer, Windows Setup has valid Authenticode/RFC 3161 evidence and 17 signed TermLink payload binaries.

## Artifacts and publication

Android was published first, followed by Windows. Both are AppShip stable releases with `published` release status and `ready` artifacts.

| Platform | File | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Android | `artifacts/release/0.2.70-126/TermLink-Android-0.2.70-126.apk` | 38,080,562 | `4e5dc71faaff262b2d3e040b257aed7a2c58870639aab9d46629497e191558f3` |
| Windows | `artifacts/release/0.2.70-126/TermLink-Setup-0.2.70.126-x64.exe` | 182,484,656 | `3a586dda45e181e4c7342f1dff3e909286848239c921bf60a7303cf5a39d7c4e` |

- Android application ID: `dev.termlink.harness`; versionName `0.2.70`, versionCode `126`.
- Android signer SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows FileVersion: `0.2.70.126`; ProductVersion: `0.2.70.126+c60a24b28539d38a87b9097ec44f288db9666921`.
- Windows signer SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, the existing self-signed development publisher. This verifies update continuity, not public CA publisher trust.
- Windows timestamp: `http://timestamp.sectigo.com`, verified RFC 3161 OID `1.3.6.1.4.1.311.3.3.1`, Sectigo Public Time Stamping Signer R37. HTTP was explicitly enabled; timestamp signatures and Windows trust validation remain mandatory.
- Microsoft timestamp requests and some Sectigo requests failed intermittently before the final build. Failed builds did not publish a Windows release. Final packaging used the bounded retry fix and an E-drive process-local temporary directory because C-drive space was low.

| Platform | Application ID | Release ID | Artifact ID | Published (UTC) |
| --- | --- | --- | --- | --- |
| Android | `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2` | `eb28063a-e381-452f-add1-db23a2b29fa5` | `ab510290-98c9-45fd-84a3-5f25f1281844` | 2026-10-02 04:50:20 |
| Windows | `0ae760b3-294e-48e8-a793-95d162f685e2` | `90c95325-9703-444e-9c8b-596ef14a652b` | `e451bc3c-080f-4b5e-8d19-69b14198b07e` | 2026-10-02 05:11:41 |

- Public Android update check: build 125 => `optional`, build 126 => `no_update`.
- Public Windows update check: build 122 => `optional`, build 126 => `no_update`.
- Both artifacts were downloaded from their public AppShip endpoints and matched the exact local size and SHA-256.
- Idempotency states remain in `.appship/termlink-android-stable-0.2.70-build126-signature-approved.json` and `.appship/termlink-win64-stable-0.2.70-build126-signature-approved.json`.
- The combined manifest is `artifacts/release/0.2.70-126/artifact-manifest.json`; original per-platform manifests are preserved under `artifacts/validation/`.

## Incomplete device acceptance

**Publication succeeded, but full device acceptance is incomplete. Android was published before the failed emulator gate had been resolved; that sequence did not satisfy the release runbook. Do not treat this record as a completed end-to-end acceptance.**

- The same-source Android Debug 0.2.70/126 build succeeded. It is only a simulator artifact; the published APK is Production.
- `adb install -r` failed with `Can't find service: package`. The emulator was already unable to complete Android startup; `system_server` repeatedly crashed while reading `/data/misc/apexdata/com.android.permission/access.abx` with `Unexpected magic be6513d6`.
- A data-preserving Android reboot did not resolve the system failure. A full emulator cold start was then refused by the host disk-space check. C: had about 1.49 GiB free after build cleanup (0.77 GiB during an earlier build).
- No app uninstall, data clear, permission-database reset, or AVD wipe was performed. The original AVD directory remains intact; the emulator remains stopped after the cold-start refusal. The installed app was not upgraded, and preserved login/session recovery could not be verified.
- Physical-phone confirmation of reopening an existing HOME-PC session was requested and is pending. Repairing the emulator and completing that smoke test remain follow-up work.
- No real credential revocation or physical LAN/NAT failure matrix was exercised in this release. Debug simulator behavior does not qualify as Production Direct transport acceptance.
- Local Windows installation and Server/Docker deployment were not performed in this publication task.

## Rollback baseline and retained evidence

- Previous live Android stable: 0.2.69/125, release `b9d5c8cc-304d-44c9-a107-b57159b68470`, artifact `acf17173-f56c-4190-9ca7-8f7949a40eff`.
- Previous live Windows stable: 0.2.66/122, release `a5c44928-1d59-41da-843d-fd1452743eea`, artifact `786ab91a-bcaf-4fc7-86a0-8d1bce2b2eb1`.
- Previous releases and artifacts were retained. No rollback was executed. If a runtime regression requires rollback, rebuild the previously accepted product source under a new higher version/build with the same signer; do not overwrite build 126 or uninstall Android to force a downgrade.
- Local logs: `artifacts/validation/auth-android-tests.log`, `auth-windows-recovery.log`, `auth-windows-direct.log`, `auth-windows-ui.log`, `release-126-build-gates.log`, `release-126-windows-build-final.log`, `release-126-independent-verification.json`, `release-126-windows-public-check.json`, and `release-126-emulator-coldboot.log`.
