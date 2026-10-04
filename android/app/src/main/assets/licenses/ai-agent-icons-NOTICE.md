# AI Agent product icons

These small product marks identify the connected third-party Agent in TermLink. They are not
TermLink branding and do not indicate endorsement. Product names and marks remain the property
of their respective owners. No Agent runtime, account data or application code is included.

## Codex and Claude Code

Source: https://github.com/lobehub/lobe-icons/tree/82e641b4fece9d1028a127149af9ded00df5ac0c

- `packages/static-png/light/codex.png` → `res/drawable-nodpi/ai_agent_codex.png`
- `packages/static-svg/icons/claudecode-color.svg` → `res/drawable/ai_agent_claude.xml`

Copyright (c) 2023 LobeHub. MIT license reproduced in `lobe-icons-LICENSE.txt`.
Codex's original PNG is copied byte-for-byte (SHA-256:
`87b2480a8fbb1ee1c3454e40ef2cd3c3a8d98e7203ecee25fdfb50b813f0aafa`)
and tinted to the app's foreground color at rendering time. This avoids Android VectorDrawable's
incompatibility with the SVG's compact arc flags. Claude Code's SVG path is converted to
Android VectorDrawable, preserving geometry, even-odd fill and product color.
No icon library dependency or network icon loading is used.

## ZCode

Source: https://github.com/zai-org/ZCode/blob/29628c9acdb81b703bbd4080c207a0e7ce5e276e/packages/desktop/build/icons/64x64.png

The original 64×64 product icon is copied byte-for-byte to `res/drawable-nodpi/ai_agent_zcode.png`.
SHA-256: `769f896dfa661599edafe46e41a560edde5d9c05f773bcf8709758a62bb35aab`.
The upstream root Apache-2.0 license is included as `zcode-icon-LICENSE.txt`; the original
upstream notice is retained as `zcode-upstream-NOTICE.md`. The pinned repository's third-party
inventory and notices were checked; this desktop product icon has no separate third-party entry.
Apache-2.0 does not grant trademark ownership. Usage here is limited to identifying ZCode.
The retained upstream functional notice describes ZCode, not TermLink functionality.
