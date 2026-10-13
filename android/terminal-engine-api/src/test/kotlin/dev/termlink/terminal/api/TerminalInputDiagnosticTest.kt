package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

class TerminalInputDiagnosticTest {
    private val valid = "TL-INPUT-DIAG/1\ndirection=input_encoding\nstatus=unsupported\n" +
        "kind=key\nreason=unsupported_control_character\nkey=Character\n" +
        "codepoint=U+00E9\nmodifiers=0x04\n"

    @Test fun parsesOnlyBoundedInputEncodingFields() {
        val diagnostic = assertNotNull(TerminalInputDiagnostic.parse(valid))
        assertEquals(TerminalInputDiagnosticOrigin.NATIVE_ENCODING, diagnostic.origin)
        assertEquals("unsupported", diagnostic.status)
        assertEquals("key", diagnostic.kind)
        assertEquals("unsupported_control_character", diagnostic.reason)
        assertEquals("Character", diagnostic.key)
        assertEquals("U+00E9", diagnostic.codepoint)
        assertEquals("é", diagnostic.characterPreview)
        assertEquals("0x04", diagnostic.modifiers)
    }

    @Test fun rejectsOutputDiagnosticsExtraFieldsAndUntrustedText() {
        listOf(
            valid.replace("TL-INPUT-DIAG/1", "TL-DIAG/2"),
            valid.replace("direction=input_encoding", "direction=output"),
            valid.replace("key=Character", "key=Character\ntext=secret"),
            valid.replace("codepoint=U+00E9", "codepoint=U+00E9 pasted"),
            valid.replace("reason=unsupported_control_character", "reason=bad\u001b[31m"),
            valid.replace("U+00E9", "U+110000"),
            valid.replace("U+00E9", "U+D800"),
            valid.replace("U+00E9", "U+00e9"),
            valid.dropLast(1),
            valid.repeat(30),
        ).forEach { assertNull(TerminalInputDiagnostic.parse(it)) }
        assertNull(TerminalProtocolDiagnostic.parse(valid))
    }

    @Test fun acceptsSafeNoneFieldsAndWin32KeyName() {
        val win32 = valid.replace("kind=key", "kind=win32_key")
            .replace("key=Character", "key=VK_0041")
            .replace("codepoint=U+00E9", "codepoint=none")
            .replace("modifiers=0x04", "modifiers=none")
        assertEquals("VK_0041", assertNotNull(TerminalInputDiagnostic.parse(win32)).key)
        val control = valid.replace("U+00E9", "U+000A")
        assertNull(assertNotNull(TerminalInputDiagnostic.parse(control)).characterPreview)
    }

    @Test fun androidC1ValidationReportsTheRejectedScalarWithoutNativeProtocol() {
        val diagnostic = TerminalInputDiagnostic.androidC1Blocked(0x80)
        assertEquals(TerminalInputDiagnosticOrigin.ANDROID_VALIDATION, diagnostic.origin)
        assertEquals("U+0080", diagnostic.codepoint)
        assertEquals("c1_text_character_blocked", diagnostic.reason)
        assertNull(diagnostic.characterPreview)
        assertNull(TerminalInputDiagnostic.parse(diagnostic.text))
        assertNull(TerminalProtocolDiagnostic.parse(diagnostic.text))
    }
}
