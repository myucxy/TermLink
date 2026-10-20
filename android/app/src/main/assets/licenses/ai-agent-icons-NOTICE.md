# AI Agent product icons

These small product marks identify the connected third-party Agent in TermLink. They are not
TermLink branding and do not indicate endorsement. Product names and marks remain the property
of their respective owners. No Agent runtime, account data or application code is included.

## Codex

The Codex integration uses OpenAI's black/white Blossom mark, as requested by the user.
Official brand page: https://openai.com/brand/ (checked 2026-10-09).
Source artwork:
https://images.ctfassets.net/kftzwdyauwt9/3hUGLn3ypllZ0oa01qOYVq/28e8188e6f11b84c3e876569d492734f/Blossom_Light.svg

Source SHA-256: `01485e70cea6df8422f5abc643fbbd3c153442cc41da0e7d8e7451801ebf26e2`.

Destination: `res/drawable/ai_agent_openai.xml` (Android VectorDrawable).
The first solid black Blossom path is preserved verbatim from the source artwork;
the surrounding construction guides and duplicate example are not part of the mark.
The viewport centers the unmodified geometry with clear space. Rendered black on light
surfaces and white on dark surfaces, without stretching or decorative effects.

Copyright and trademarks belong to OpenAI. This asset is used only to identify the Codex
integration, not as TermLink branding or an endorsement. It is not covered by TermLink's GPL
or the LobeHub MIT license. Usage is subject to OpenAI's brand guidelines and Marks usage terms:
https://openai.com/brand/ (checked 2026-10-09; usage terms retained in `openai-marks-usage.txt`).

## Claude Code

Source: https://github.com/lobehub/lobe-icons/tree/82e641b4fece9d1028a127149af9ded00df5ac0c

- `packages/static-svg/icons/claudecode-color.svg` → `res/drawable/ai_agent_claude.xml`

Copyright (c) 2023 LobeHub. MIT license reproduced in `lobe-icons-LICENSE.txt`.
Claude Code's SVG path is converted to
Android VectorDrawable, preserving geometry, even-odd fill and product color.
No icon library dependency or network icon loading is used.

## OpenCode and Grok Build

Source revision: `8a1b2821bb768c484fe8da20c5e1838b6bf551a9` (checked 2026-10-20).

- OpenCode: https://github.com/lobehub/lobe-icons/blob/8a1b2821bb768c484fe8da20c5e1838b6bf551a9/packages/static-svg/icons/opencode.svg
  → `res/drawable/ai_agent_opencode.xml`.
  Source SHA-256: `7cfa6e9d6726f7c9fa26c7d9aef0dfec52d20a137380454340f30f12ccbfd302`.
- Grok: https://github.com/lobehub/lobe-icons/blob/8a1b2821bb768c484fe8da20c5e1838b6bf551a9/packages/static-svg/icons/grok.svg
  → `res/drawable/ai_agent_grok.xml`.
  Source SHA-256: `9175fc90c22655160231976c849f25a03b888d7cc0e04c5f1b987b659bb07c95`.

Copyright (c) 2023 LobeHub. The pinned repository's MIT license matches the existing
`lobe-icons-LICENSE.txt` shipped in this directory. Conversion preserves the original
24×24 geometry and even-odd fill; monochrome marks render black on light surfaces and
white on dark surfaces. Product names and trademarks remain with their respective owners.
The assets identify integrations only. No runtime icon download or new dependency is used.

## ZCode

Source: https://github.com/zai-org/ZCode/blob/29628c9acdb81b703bbd4080c207a0e7ce5e276e/packages/desktop/build/icons/64x64.png

The original 64×64 product icon is copied byte-for-byte to `res/drawable-nodpi/ai_agent_zcode.png`.
SHA-256: `769f896dfa661599edafe46e41a560edde5d9c05f773bcf8709758a62bb35aab`.
The upstream root Apache-2.0 license is included as `zcode-icon-LICENSE.txt`; the original
upstream notice is retained as `zcode-upstream-NOTICE.md`. The pinned repository's third-party
inventory and notices were checked; this desktop product icon has no separate third-party entry.
Apache-2.0 does not grant trademark ownership. Usage here is limited to identifying ZCode.
The retained upstream functional notice describes ZCode, not TermLink functionality.
