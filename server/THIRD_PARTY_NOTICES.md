# TermLink Server third-party notices

The complete dependency versions are recorded in `go.mod` and `go.sum`.
The original notice texts for Go runtime and the modules linked into the Linux
amd64/arm64 server are in [licenses/THIRD_PARTY_NOTICES.txt](licenses/THIRD_PARTY_NOTICES.txt).
The Docker image copies that bundle to `/licenses/THIRD_PARTY_NOTICES.txt`.
This document highlights several components; it is not the complete dependency list.

Versions, notice hashes and source links are recorded in
[dependency-inventory.json](../docs/licenses/dependency-inventory.json). Regenerate with
`python scripts/update_dependency_inventory.py` from the application repository root,
using the pinned Go toolchain; CI verifies the generated content against locked inputs.

## ORY Fosite

TermLink's embedded OAuth 2.0 and OpenID Connect provider uses
`github.com/ory/fosite`, distributed under the Apache License 2.0.

Copyright 2016 ORY GmbH

Source: https://github.com/ory/fosite

## go-jose

TermLink's embedded OAuth 2.0 and OpenID Connect token implementation uses
`github.com/go-jose/go-jose/v3`, distributed under the Apache License 2.0.

Copyright 2018 The go-jose Authors

Source: https://github.com/go-jose/go-jose

## pquerna/otp

TermLink's RFC 6238 TOTP implementation uses `github.com/pquerna/otp`,
distributed under the Apache License 2.0.

otp
Copyright (c) 2014, Paul Querna

This product includes software developed by Paul Querna
(http://paul.querna.org/).

Source: https://github.com/pquerna/otp
