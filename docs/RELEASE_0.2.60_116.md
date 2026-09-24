# TermLink 0.2.60 (116)

Date: 2026-09-24. Scope: Windows x64 Setup and Android APK, AppShip stable.
Includes the multi-interface, subnet, STUN isolation, promoted-path recovery and
Android check lifecycle fixes described in [115](RELEASE_0.2.59_115.md).
No Server/protocol/schema changes; no Docker image is required.

## Additional real-product finding

The unpublished 115 packages exposed a diagnostic lifecycle defect during actual
emulator operations. LAN returned no matching path (emulator 10.0.2.x versus PC
192.168.9.x), but Android did not tell Windows that discovery had ended. Windows
kept waiting, consumed subsequent offers in the old attempt and entered rate/
failure cooldown. This was not evidence that the physical network rejects NAT.

- Android now sends authenticated existing-protocol PATH_CLOSE before releasing
  discovery secrets on completion/cancellation/failure. Signal sequence emission
  is serialized with retirement; promoted data-plane ownership remains separate.
- Windows validates/decrypts/canonicalizes close signals, retires their exact
  attempt, and does not count explicit cancellation as a failed network path.
  The next fresh ticket can start a check immediately; epoch replay checks and
  Server ticket/signal rate limits remain. Normal failed attempts retain the
  30-second minimum interval; repeated-failure cooldown is one minute instead
  of five, and successful negotiation resets accumulated failures.
- Admission rejection sends authenticated PATH_CLOSE instead of silently dropping
  the offer. Android reports cooldown separately from probe timeout.
- STUN resolution retains one address per family per configured server, keeping
  both server choices rather than using two DNS answers from the first. Interface
  fallback matches the peer's advertised address family.

## Validation

- Final Android Debug/Release core-network 329/329, app 318/313 passed, zero
  failures/errors/skips. Added encrypted close-on-completion regression.
- Windows Direct runtime suite includes close retirement, no failure penalty,
  immediate next attempt and stale epoch rejection, plus the 115 route/callback
  tests. Native ABI, Rust socket, desktop/setup/architecture and release gates
  are recorded in 115; native code is unchanged from that tested revision.
- Real HOME-PC route selection resolves to 192.168.9.28. Final 116 was installed
  in place at `D:\Dev\Tool\TermLink`; Setup exited 0 and Agent ProductVersion is
  `0.2.60.116+dc13c84e07c604cbc34756471ee3db06ebd4fdd2`.
- The emulator retained its account and firstInstallTime
  `2026-09-19 10:16:29`, and now runs 0.2.60/build 116. Its existing Debug signer
  required a same-source Debug APK; shadow discovery was enabled, data-plane
  promotion disabled. This is not installation/acceptance of the production APK.
- Actual emulator operations opened a dedicated new HOME-PC command session as
  Controlling over Relay. LAN changed from checking to `No matching path`;
  NAT ended with `Check timed out`; another request during cooldown returned
  `Please retry shortly`. The Windows log shows a fresh epoch 122 accepted after
  LAN epoch 121 retired, rather than leaving the previous discovery active.
  Later attempts were admitted after the bounded retry interval.
- After detection, Android sent `echo TERMLINK_116_RELAY_OK` and displayed the
  command result. Device refresh stayed available. Android's encrypted single
  session termination removed this dedicated test session and its local host
  process. No user account/app data was cleared.
- Local evidence: `artifacts/validation/direct-116-{android,windows}.log`,
  `direct-116-production-build.log`, `direct-116-windows-live.jsonl`,
  `direct-116-lan-start.xml`, `direct-116-lan-result.xml`,
  `direct-116-nat-result.xml`, `direct-116-nat-repeat.xml`,
  `direct-116-relay.png`, and `direct-116-cleanup.xml`.
- Termux reference/architecture and licensing boundary remain as recorded in 115.

## Publication

Production artifacts were built from clean, pushed source commit
`dc13c84e07c604cbc34756471ee3db06ebd4fdd2`. This evidence-only documentation
update follows that source commit; it does not change the binaries.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `TermLink-Android-0.2.60-116.apk` | 38147722 | `e8a29ab4f3a0580cf2fbb1848cbf37ac7703905e900ad2ae5c701807a52260a9` |
| `TermLink-Setup-0.2.60.116-x64.exe` | 181313240 | `27a456723ee08bd7e16e3533b9a7aafea0a1b45d3e66e8a68d6cb84bb978b80d` |

Files and the immutable manifest are in `artifacts/release/0.2.60-116/`.
Independent `Verify-LocalRelease.ps1` passed Production mode, native signatures,
versions, hashes and Windows RFC 3161 timestamp validation. Signer continuity:

- Android SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, valid through
  2036-08-04; the existing locally trusted development signer, not public CA trust.

Both releases were published sequentially to AppShip stable on 2026-09-24:

| Application | Release ID | Artifact ID | Published UTC |
| --- | --- | --- | --- |
| `termlink-win64` | `5f03fae0-b6ae-4da9-ab2b-886caa662be6` | `6dd1e27b-626c-4d78-812b-4fb96042dfd4` | 03:21:54 |
| `termlink-android` | `041dc5ba-65f2-41cb-9524-d847e46fa1de` | `bea111c7-2aa8-4524-a262-67525ebf6386` | 03:22:49 |

The public update APIs returned `optional` for Windows build 113 and Android
build 114, and `no_update` for build 116 on both platforms. Published artifacts
are `ready`, `manual_approved`, download-enabled, and match local size, hash and
signer. Sanitized evidence is in `direct-116-public-updates.json`; publication
logs are `direct-116-appship-{windows,android}.log`. Resume state remains under
`.appship/termlink-{win64,android}-stable-0.2.60-build116-signature-approved.json`
and is excluded from Git.

115 is preserved locally and was not published to AppShip. Both clients should
upgrade together; the remote HT-LPC-1326 was still build 113 during this smoke.
No Server image was built, uploaded or deployed, and no Git release tag was
pushed for this client-only release.

Physical same-Wi-Fi and cross-NAT end-to-end success are not established by the
emulator or unit tests. The release must not claim those acceptance scenarios.
Rollback uses Relay-only policy and a strictly newer same-signer client build;
do not clear app data, replace keys or mutate published artifact/version history.
