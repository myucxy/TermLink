// Package migrations exposes the immutable SQL migration set embedded in the
// server binary. Applying migrations remains an explicit command.
package migrations

import "embed"

// Files contains all versioned up/down migration scripts.
//
//go:embed *.sql
var Files embed.FS
