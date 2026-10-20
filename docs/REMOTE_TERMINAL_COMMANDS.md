# Remote terminal command contract

## Purpose

TermLink exposes only terminal commands that the selected Windows PC can launch. Android does not infer installed shells and cannot submit an executable path or an arbitrary command line.

## Local configuration

Windows keeps launch details in `%LOCALAPPDATA%\TermLink\Agent\terminal-commands.json`.

Each manual profile contains:

- `id`: immutable, locally generated identifier;
- `display_name`: label shown in Windows settings and Android Files;
- `executable_path`: resolved absolute executable or script path;
- `arguments`: optional argument combination passed to the command;
- `managed_shell`: always `custom` for manual profiles;
- `source`: always `manual` for manual profiles;
- `enabled`: controls whether the profile is advertised.

Detected Command Prompt, Windows PowerShell, PowerShell 7, and WSL profiles are generated at runtime and are read-only. Missing executables, disabled manual profiles, and invalid local configuration are not advertised.

`powershell-7-enhanced` is an additional detected command with `shell_type: powershell-7`.
It requires PowerShell 7, SessionHost and the native Desktop display in the same installation;
it starts as the current user and requests the graphics terminal profile. The four existing
command IDs and `supported_shells` retain their original meanings.

## Server capability

The Agent includes `terminal_commands` in the signed PC upsert. Only these non-secret fields leave Windows:

```json
{
  "id": "custom-0123456789abcdef",
  "display_name": "Project CLI",
  "shell_type": "custom",
  "source": "manual"
}
```

Executable paths and arguments remain local. The server validates identifier syntax, bounded display names, source/type relationships, uniqueness, and a maximum of 24 advertised commands.
Detected commands normally have identical IDs and shell types. The sole additional mapping is
`powershell-7-enhanced` to `powershell-7`; this does not permit arbitrary detected IDs or shell types.

## Android launch flow

1. Android renders the PC's ordered `terminal_commands` list on directory menus.
2. Android submits only `terminal_command_id` and the selected directory path.
3. The server creates the operation only when the ID is present in that PC's current capability list.
4. The Agent resolves the ID again from current local configuration and refuses stale, disabled, missing, or unknown entries.
5. SessionHost starts the resolved executable and arguments through the existing ConPTY session pipeline.
6. Completion binds `session_id`, `terminal_command_id`, `shell_type`, and `cwd` to the original operation.

This double validation means neither Android nor the server can replace a configured command with arbitrary launch text.

Windows Terminal launches explicitly select TermLink's always-present CMD profile
by GUID to inherit its `closeOnExit: always` policy. The actual command line still
starts the resolved SessionHost/Shell directly, and the tab title uses the selected
command's display name; no extra CMD is wrapped around PowerShell, WSL or a custom
CLI. Android's confirmed termination ends only that SessionHost process tree, so
Windows Terminal can remove the corresponding tab instead of leaving an exited
process screen. Other tabs and terminal windows are not terminated. This applies
to newly launched sessions after installing a Windows build containing this fix;
it does not rewrite profiles on already-open tabs or the user's default settings.

## Client capability negotiation

The `GET /v1/pcs` response keeps older Android clients compatible while allowing a
new client to receive the enhanced PowerShell command. A client that understands
that command sends this exact request header:

```text
X-TermLink-PC-Command-Capabilities: powershell-7-enhanced
```

With the exact token, the response includes the stored `powershell-7-enhanced`
detected command. If the header is missing, empty, or unknown, the server projects
that one detected mapping out of the response. Other detected commands and manual
custom commands remain unchanged. This is a response-only projection: the stored
`terminal_commands`, `supported_shells`, and `capabilities` values are not modified.

## Compatibility

`supported_shells` and `shell_type` remain available for older Android and Agent versions. New Android versions use this fallback only when a PC has not reported `terminal_commands`. Built-in commands retain their existing IDs, so mixed-version clients continue to launch the four fixed shells during rollout.

Migration 22 adds `pcs.terminal_commands` and broadens the successful `OPEN_TERMINAL` result constraint to accept both legacy and standardized result shapes. Its down migration fails closed while terminal-command data exists.

## Extension boundary

Future command providers should produce the same local profile and public capability types. Providers own detection and local launch resolution; the server and Android continue to operate only on opaque command IDs and safe display metadata.
