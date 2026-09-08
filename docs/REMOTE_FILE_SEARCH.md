# Remote current-directory search

TermLink Android exposes a compact filename search between the parent-directory
and current-directory action buttons. Search is non-recursive, matches files
and directories with Windows ordinal case-insensitive semantics, and is applied
by the PC Agent before the existing stable sort and pagination.

The request contract keeps `LIST` and adds an optional `query`. Queries are
plain text, never shell expressions, regular expressions, paths, or file-content
searches. Android, Server, PostgreSQL, and Agent enforce 128 Unicode code points
and 512 UTF-8 bytes. Pagination identity is the tuple `(pc, path, query, offset)`.

The Agent advertises `file_operation_name_filter_v1`. A compatible Server maps
that private Agent capability to the Android-visible
`file_operations_search_v1`; an older Server therefore cannot accidentally
enable a UI that it cannot route. Search is explicitly submitted instead of
being sent for every keystroke because file operations are durable, bounded
jobs rather than an unbounded type-ahead channel.

## Termux reference boundary

The review used the repository-pinned `termux/termux-app` commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4c`, recorded in
`terminal-forks/termux-terminal/UPSTREAM.md`, plus the local TermLink patch queue
and `android/terminal-engine-termux/` boundary. Termux's
`TermuxDocumentsProvider` and shared file utilities cover local Android file
exposure, path handling, and errors; they do not implement a remote Windows
directory browser with Agent-side filtering and pagination. They were used as
behavior references only. No Termux code, local PTY, shell, bootstrap, package,
or signing architecture was copied.
