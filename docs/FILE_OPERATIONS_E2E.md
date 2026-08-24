# File operations end-to-end validation

`scripts/Invoke-FileOperationsE2E.ps1` validates the authenticated production
protocol against a running TermLink server and Windows Agent. It is deliberately
separate from Android emulator UI validation: a successful run proves the REST,
object-transfer, and Agent execution path, but does not claim that Compose UI,
the Storage Access Framework picker, or the download-management screen was
automated.

## Prerequisites

- Windows Agent is linked to the same disposable acceptance account, online,
  remote routing is enabled, and its file-operation worker is running.
- The production acceptance HTTPS server, OIDC realm, database, Valkey, and
  object store are ready. File-transfer PUT/GET routes must be exposed by the
  selected edge configuration.
- The disposable acceptance password file and public acceptance CA are present.
  Their values are read in-process and are never written to evidence or output.
- The Go toolchain is available through
  `D:\Dev\Scripts\Ensure-DevEnv.ps1 -Tool go -Json` (or `go` on `PATH` when the
  canonical inspection script is unavailable).
- The account has exactly one online, remotely routable PC, or `-PcId` is
  supplied to remove selection ambiguity.

The managed stack normally provides:

```text
server\secrets\acceptance\acceptance-user-password
server\secrets\acceptance\acceptance-root-ca.crt
```

The default username is `acceptance-user`. Use `-Username acceptance-probe`
only when the running Agent is linked to that separate disposable account.

## Run

For a LAN acceptance stack:

```powershell
$origin = 'https://termlink.192.168.9.28.sslip.io:9443'
$history = 'https://history.192.168.9.28.sslip.io:9443'

.\scripts\Invoke-FileOperationsE2E.ps1 `
  -AcceptanceOrigin $origin `
  -HistoryOrigin $history `
  -PcId '00000000-0000-0000-0000-000000000000'
```

Replace the example PC UUID with the selected Agent's real PC ID. Omit `-PcId`
only when exactly one eligible PC is visible. `-OidcIssuer` defaults to
`<AcceptanceOrigin>/realms/termlink`. `-HistoryOrigin` may also come from the
`TERMLINK_ACCEPTANCE_HISTORY_ORIGIN` process environment variable.

Full mode opens one managed Command Prompt and one managed Windows PowerShell
session. The runner validates their returned session IDs and working directory,
then proves that direct deletion of each still-running catalog row is rejected
with HTTP 409 `SESSION_ACTIVE`. Online closure belongs to the Realtime Terminate
flow, which this protocol-only harness does not implement. The PowerShell wrapper
therefore stops only `TermLink.SessionHost.exe` processes whose command line
contains an exact session ID created by this run. A full pass requires exactly
two requested IDs, exactly two confirmed process exits, and no unmatched ID.
Use `-SkipTerminalOperations` only for a consciously partial protocol run; the
evidence marks the terminal section as skipped.

## What a full run proves

- Registers a fresh, temporary Android device identity through OIDC and the
  signed device-registration contract, then self-revokes it at the end.
- Creates a uniquely owned Windows temporary tree with an empty file, UTF-8
  Unicode filenames/content, a nested directory, and deterministic SHA-256
  expectations.
- Forces `LIST` pagination across exactly 500 and 5 entries, then checks that
  the combined names, absolute paths, kinds, and file sizes exactly match the
  controlled tree without duplicates.
- Downloads the empty and Unicode files, checking response metadata, byte count,
  SHA-256, and exact source bytes.
- Downloads the nested directory as ZIP, checking the downloaded archive hash,
  safe/exact entry names, and every file entry hash against the source tree.
- Opens Command Prompt and Windows PowerShell at the exact requested directory;
  checks `session_id`, `shell_type`, and `cwd`, and confirms each returned session
  appears under the selected PC. It also checks the active-session deletion fence
  returns HTTP 409 `SESSION_ACTIVE`, records `server_session_deleted=false`, and
  delegates the two locally owned hosts to exact-ID wrapper cleanup.
- Cancels a real queued or processing file operation and reads back the persisted
  `CANCELLED` state and `cancelled_at` value.

The protocol driver accepts presigned downloads only from the exact HTTPS
`HistoryOrigin`, caps test downloads at 32 MiB, disables redirects, and never
prints response bodies, access tokens, private keys, password values, presigned
URLs, or headers.

## Evidence and cleanup

Evidence is written to:

```text
artifacts\file-operations-e2e\<ArtifactRunId>\evidence.json
```

It contains public check summaries, operation/session IDs, hashes, cleanup
status, and a stable failure code/stage. It intentionally excludes credentials,
identity key material, authorization headers, HTTP bodies, and presigned URLs.
For each terminal it records `active_delete_fenced=true`,
`owned_cleanup_delegated=true`, and `realtime_terminate_executed=false`. Wrapper
cleanup records requested/stopped/unmatched counts and
`exact_owned_terminal_cleanup`; it does not claim Android Realtime termination or
server-side deletion was exercised.

The temporary tree name includes a random 128-bit value and contains an exact
ownership marker. Recursive cleanup refuses a path outside the selected
temporary parent, a leaf outside the runner's fixed name pattern, or a missing
or mismatched marker. It never cleans the temporary parent or sibling paths.

## Static and safety self-test

Run before attempting a live stack:

```powershell
.\scripts\Test-FileOperationsE2E.ps1
```

The self-test parses both scripts, compiles the embedded standard-library Go
driver using the canonical Go tool, exercises parameter rejection, validates
the 505-entry fixture, proves normal cleanup preserves a sibling sentinel, and
proves wrong-name and wrong-marker cleanup targets are rejected. It also builds
three disposable processes named `TermLink.SessionHost.exe`, confirms two exact
session IDs are stopped, and confirms the unlisted third process survives. It
performs no network access.

To exercise only fixture creation and normal cleanup manually:

```powershell
.\scripts\Invoke-FileOperationsE2E.ps1 -FixtureOnly
```

## Android UI validation boundary

This harness does not install an APK, start an emulator, drive Compose semantics,
interact with `ACTION_CREATE_DOCUMENT`, or assert download-list progress/cancel/
delete controls. Those checks require a separate Android instrumentation or
manual simulator run. Do not use `android_ui.executed: false` protocol evidence
as UI acceptance evidence.

The harness also does not send an Android Realtime Terminate message. Its
`realtime_terminate_executed=false` evidence is intentional: the test proves the
REST active-delete fence plus controlled local host cleanup, not the Android
online-close interaction.
