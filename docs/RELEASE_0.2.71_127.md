# TermLink 0.2.71 / 127 release evidence

Status: tests, joint emulator/Windows acceptance, Production build, independent verification, AppShip publication and local Production installation passed.

Production source revision: `a488a4747068e9ae7838ef97b35b7f355a024e97`; the worktree was clean and HEAD equaled origin/main before building both artifacts. This later documentation update does not change product code.

## Scope and behavior

- Android 0.2.71/127 and Windows 0.2.71.127; no Server/Docker change.
- Session labels share one projection of Windows presence, lifecycle, catalog availability and live transport state. An unconfirmed catalog cannot acquire a new control lease; an already authenticated live controller retains the existing lease/input path.
- Android remembers the catalog host instance and observation timestamp within the current account. Older observations and retired instances fail closed. Agent restart retires the old session clients/renderers before publishing the replacement directory. History bootstrap rejects a tail from a different host instance.
- Windows serializes catalog observations and keeps their timestamp increasing within a process, including clock rollback. Existing wire messages, E2EE, ACK, input deduplication and Direct routing are unchanged.
- Background catalog polling stops; foreground resume requests an immediate refresh. Foreground retains the existing two-second compatibility poll. Catalog push hints require separate Server capability negotiation and are deferred.
- HAPI was a behavioral reference for instance fencing and foreground reconciliation; no HAPI implementation or dependencies were copied.

## Verification before candidate build

- Android application unit tests: 327 passed; core-network tests: 336 passed; lint passed with existing warnings.
- Windows solution Release build: passed, zero warnings/errors.
- Windows session fault isolation (including clock rollback), subscriber recovery, Direct transport, Avalonia headless and Setup ownership/rollback validations: passed.
- Release build/signing/manifest/AppShip gate tests: passed.
- Signer continuity checked against the existing production identities: Android SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`; Windows SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`. Both certificates are currently valid. Windows uses the existing self-signed publisher, not public CA trust.
- Live AppShip preflight: both stable applications are 0.2.70/build 126, next build 127.

## Joint acceptance and publication gates

- Original `TermLinkE2E` emulator still fails Android startup with a corrupted permission `access.abx`; it was stopped without deleting/resetting its data.
- Existing `TermLinkReleaseUpgrade_API36` (`emulator-5556`) boots successfully. Signed Release acceptance APK upgraded 0.2.40/96 to 0.2.71/127 in place; appId 10216 and firstInstallTime 2026-08-29 18:14:23 are unchanged. The user restored its expired login to the same account as Windows.
- Windows acceptance Setup upgraded the existing `D:\Dev\Tool\TermLink` installation to 0.2.71.127; same signer, retained account, remote access enabled and Relay connected. This local acceptance installer is Development (no RFC 3161 timestamp) and must never be uploaded. The final Production installer must be rebuilt from the clean, pushed revision and independently timestamp/signature verified.
- Real installed Windows SessionHost/Agent test: new process was Connected, emitted a unique marker and ceased to be Connected after exit.
- Joint test on the public Server: Android displayed HOME-PC 0.2.71.127 and older HT-LPC-1326 0.2.66.122. A new HOME-PC fixture appeared automatically as running. Opening it rendered the 160-line history tail; a single-finger swipe displayed earlier lines 116-140. Android input `PHONE127` produced exactly one Windows `PHONE_ECHO:PHONE127`.
- Graceful Agent restart changed its process instance; Android removed the stale live page. The Windows terminal remained alive, a fresh catalog allowed reopening, foreground resume restored control, and Android `AFTER127` produced exactly one Windows echo. Sending `exit` changed the Android directory row to exited.
- Offline observation: with the desktop/Agent stopped, HOME-PC was hidden by the existing device filter and the mobile session page showed no sessions or control/history. Restart restored the directory. An initial automation assertion incorrectly required an explicit offline card; the observation predicate was corrected to allow the existing hidden-offline-device policy and the gate rerun.
- Existing Codex history (`分析历史会话 | TermLink`) restored its final screen. Inspection found that the read-only renderer's synthetic Exited state masked the authoritative Interrupted lifecycle; the display precedence and regression assertion were corrected. Full app tests/lint and signed APK were rerun; a second in-place install confirmed the final label is Interrupted (`catalog-final-interrupted.xml`).
- UI evidence: `artifacts/validation/catalog-joint-{created,open,restart,resumed,exited,host-offline,host-offline-session,online-restored}.xml`, `catalog-joint-open.png`, `catalog-joint-history.png`, and fixture output `catalog-joint-host.stdout.log`.
- No physical LAN/NAT matrix or live credential revocation was exercised; existing deterministic Direct/auth tests passed. The original emulator corruption is unrelated to the app and remains unresolved.
- Final Production APK/Setup were independently verified and installed over the acceptance builds before upload. Android retained appId, firstInstallTime and login; Windows retained the existing install root/account and connected Relay. The existing Codex history screen restored with the correct Interrupted label (`catalog-production-history.png`). No Git release tag or Docker deployment was created.

## Production artifacts and publication

| Platform | File | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Android | `artifacts/release/0.2.71-127/TermLink-Android-0.2.71-127.apk` | 38,080,562 | `6bc7a8c3fdbebaee34ab6727d1886d6057d461681d2b412973b4431b192ce728` |
| Windows | `artifacts/release/0.2.71-127/TermLink-Setup-0.2.71.127-x64.exe` | 182,484,664 | `38f5f82ae0f2eac8b06a249220f4ab611aac3f8acf1ce118aff54c91f0b25184` |

- Independent `Verify-LocalRelease.ps1`: passed for the combined Production manifest. Android has one approved v2 signer; Windows has Valid Authenticode, the approved signer, verified RFC 3161 timestamp OID `1.3.6.1.4.1.311.3.3.1`, and 17 verified payload binaries.
- Windows ProductVersion: `0.2.71.127+a488a4747068e9ae7838ef97b35b7f355a024e97`; FileVersion: `0.2.71.127`. Timestamp service: `http://timestamp.sectigo.com` with explicit HTTP opt-in and mandatory cryptographic verification.
- Android was published first, then Windows, using `scripts/Publish-AppShipProduction.ps1` and the reviewed `appship-publish` skill.

| Platform | Application ID | Release ID | Artifact ID | Published UTC |
| --- | --- | --- | --- | --- |
| Android | `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2` | `1c65fab1-1ead-4f3b-adf1-43967547f088` | `a4a2b660-e38d-4b44-a47d-bb11c7fcda4f` | 2026-10-02 14:10:33 |
| Windows | `0ae760b3-294e-48e8-a793-95d162f685e2` | `9230d8f1-ca6a-4b0d-9d11-3f926b0ee3df` | `9f7e711a-c95b-41c5-a1a6-9dbc71950a19` | 2026-10-02 14:12:00 |

- Authenticated online verification confirms both stable releases are `published`, both artifacts `ready`.
- Both public download endpoints returned the exact local file size/SHA-256. Both platforms return `optional` for build 126 and `no_update` for build 127.
- Retained idempotency states: `.appship/termlink-android-stable-0.2.71-build127-signature-approved.json` and `.appship/termlink-win64-stable-0.2.71-build127-signature-approved.json`.
- Verification/publication logs: `artifacts/validation/catalog-production-build.log`, `catalog-production-independent-verification.json`, `catalog-appship-published.json`, `catalog-public-update-checks.json`, and `catalog-joint-acceptance.json`.

## Rollback

Retain published 0.2.70/126 artifacts and AppShip history. For a regression, rebuild the accepted source under a higher version/build with the same signer; do not overwrite a released version, force an Android downgrade or uninstall the app. Preserve existing Windows installation root, account credentials and local terminal history.

Local test logs: `artifacts/validation/catalog-android-tests.log`, `catalog-windows-build.log`, `catalog-windows-tests.log`, `catalog-windows-ui.log`, `catalog-windows-installer.log`, `catalog-release-gates.log`.
