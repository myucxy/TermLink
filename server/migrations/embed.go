// Package migrations exposes the immutable SQL migration set embedded in the
// server binary. Applying migrations remains an explicit command.
package migrations

import "embed"

// LatestVersion is shared by migration loading and runtime schema validation.
// TestEmbeddedMigrationsAreOrderedAndStable requires the newest embedded file
// to match it, preventing a migration from making the deployed server reject
// the schema it just applied.
const LatestVersion int64 = 22

// Files contains all versioned up/down migration scripts.
//
//go:embed *.sql
var Files embed.FS
