# TermLink 0.2.70 / 126 release evidence

This release contains the combined Windows and Android authentication recovery fix.

## Source and scope

- Android: 0.2.70 / build 126
- Windows: 0.2.70.126
- Server/Docker: not included
- Artifact source revision: to be filled after the source commit is created

## Changes

- Android retries one rejected authenticated request with a fresh token and keeps the original body and idempotency key.
- Android treats only explicit credential or interaction failures as sign-out conditions; transient HTTP, proxy, and service failures remain recoverable.
- Android session lease/control requests use the same authentication refresh and invalidation path as REST catalog requests.
- Windows authentication loss closes Relay, Direct signalling, and Direct data runtimes while retaining local terminal state.

## Validation

- Android app unit tests: 325 passed, 0 failed.
- Android core-network unit tests: 133 passed, 0 failed.
- Android lint: 0 errors; existing warnings/hints only.
- Windows production subscriber recovery validation: passed.
- Windows direct transport validation: passed.
- Windows Avalonia headless validation: passed.

## Artifacts and publication

Artifact hashes, signer verification, AppShip IDs, update decisions, and download checks are recorded after each platform is published.
