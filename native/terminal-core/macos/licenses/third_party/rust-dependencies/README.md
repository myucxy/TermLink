# Rust dependency provenance

State Delta uses RustCrypto `sha2 = "=0.10.9"` for the complete canonical
snapshot and delta digests. The official crates.io API reported archive
SHA-256 `a7507d819769d01a365ab707794a4084392c824f54a7a6a7862f8c3d0892b283`,
which matches Cargo's verified source archive metadata.

`Cargo.lock` fixes all transitive versions and archive checksums. `SOURCES.json`
records those packages and their original license files, copied without changes
from the verified Cargo sources. MIT is selected where a dual license is offered.
No parser, terminal renderer, or filesystem broker code is taken from these packages.

Before an offline five-library build on a new workstation, use the pinned Rust
toolchain and its configured Cargo home to run `cargo +1.97.1 fetch --locked`.
Then `scripts/Build-ToTermLink.ps1` continues to build with `--locked --offline`.
Do not regenerate the lock or silently update dependency versions during release.

The GPL Corresponding Source bundle must include the pinned dependency sources
and these notices, together with the application's and core's matching revisions.
Client binary license bundles must carry all the copied notices; adding these
development dependencies alone does not prove that the next release bundle does so.
