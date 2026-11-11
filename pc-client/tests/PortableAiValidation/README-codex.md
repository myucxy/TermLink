# Portable Codex validation

The macOS portable owner now returns the existing `codex` and `zcode` native
brands. Codex is available only when its public CLI version and `app-server`
help probe succeed. The fixed executable/runtime descriptor is reused for later
stdio launch; it never opens a shell command string or desktop private engine.
Node/bun shebangs keep their exact script path and narrowly supported harmless
runtime options. Shell wrappers and eval/preload shims cannot become native AI
launchers. Windows' existing launcher code is excluded from the portable change
by its compile symbol and is checked for unchanged runtime/cmd argument behavior.

Codex installation/protocol availability is separate from authentication.
TermLink creates `<accountDirectory>/codex-runtime` and `sqlite/` with mode 0700,
sets `CODEX_HOME`/`CODEX_SQLITE_HOME` and the explicit `sqlite_home` override,
selects file credential storage, and removes inherited provider-token variables.
It does not copy an existing `auth.json`/`config.toml`, use Keychain fallback,
borrow a user's existing login or open an existing Codex session database. This
runtime therefore needs its own deliberate configuration/authorization before
real model use. Catalog success alone never means model login has been verified.
The current change does not implement a new sign-in flow.

Default validation runs only protocol fixtures: missing and incompatible CLI,
native binary and Node shim paths, isolated configuration/environment, native-only
catalog with ZCode preserved, unauthorized create rejection, stable operation
receipts, initialize/initialized ordering, default sandbox/approval policy,
pending approval ownership/deny, interruption, end and process cleanup. The
fixture never executes its synthetic tools or sends any network/model request.
The existing portable ZCode fixture suite is run as a regression.

Optional installed Codex validation (macOS only):

```sh
dotnet run --project pc-client/tests/PortableAiValidation/TermLink.PortableAI.Validation.csproj \
  -- --real-codex /absolute/new/evidence-directory
```

This uses `sandbox-exec` to deny existing `~/.codex`, Keychain files and IP
networking. It verifies an authored nonsecret deny canary before starting the
installed public CLI, runs only version/help plus stdio `initialize` followed by
`initialized`, closes stdin and reaps the process. It sends no thread, turn,
model/list, login or tool request. Evidence is a sanitized report; raw app-server
responses/errors and the initialize user-agent are not printed. The installed
2026-11-04 CLI was `codex-cli 0.164.0`; protocol initialization passed. Actual
model authorization, inference and user-session migration remain unverified.

Protocol source checked via OpenAI Docs: official Codex App Server documentation
at `https://learn.chatgpt.com/docs/app-server`, and public environment-variable
reference at `https://learn.chatgpt.com/docs/config-file/environment-variables`.
The transport is newline-delimited JSON with an initialize request, then an
initialized notification. Only controlled fixtures create threads or turns.
