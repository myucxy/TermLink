# Fixed legacy character mapping data

Copyright (c) Microsoft Corporation. MIT; see LICENSE.txt.

Mapping data extracted from Windows Terminal `charsets.hpp`, revision
`6676c6f938f7e98ad680f0cecedd3bafb6529734`, Git blob
`72b441facce15885f507794bdba6147e79fc8265`.
Source: https://github.com/microsoft/terminal/blob/6676c6f938f7e98ad680f0cecedd3bafb6529734/src/terminal/adapter/charsets.hpp

Only fixed byte-to-Unicode character mapping data is used, with the upstream
undefined-character marker U+2426. No Windows Terminal parser, interpreter,
CharSet implementation, renderer or dependency is copied or linked.
The TermLink designation, invocation, input and snapshot code is independent.

The sixteen added sets and their size/designator are frozen in tables.json.
Existing Western NRC mappings retain their original canonical IDs and behavior.
Regenerate the checked-in Rust data with scripts/Generate-LegacyCharsets.py.
