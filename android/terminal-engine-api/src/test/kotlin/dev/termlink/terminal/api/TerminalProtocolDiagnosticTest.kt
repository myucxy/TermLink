package dev.termlink.terminal.api

import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

class TerminalProtocolDiagnosticTest {
    private val valid = "TL-DIAG/1\ncategory=unsupported\nreason=private mode\nfamily=CSI\n" +
        "sequence=<ESC>[?9999h\nhex=1B 5B 3F 39 39 39 39 68\nstart=1:2\nend=2:5\n" +
        "observed_bytes=8\nhidden_bytes=0\ntruncated_bytes=0\n"

    @Test fun parsesEscapedSequenceWithoutExecutingOrNormalizingBytes() {
        val value = assertNotNull(TerminalProtocolDiagnostic.parse(valid))
        assertEquals("<ESC>[?9999h", value.sequence)
        assertEquals(valid, value.text)
        assertEquals("private mode", value.reason)
    }

    @Test fun hostExitDiagnosticRequiresAuthorityFailureAndEscapedValidatedEvidence() {
        assertEquals(valid, assertNotNull(TerminalProtocolDiagnostic.forHostExit(125, valid)).text)
        for (code in listOf(null, 0, 17)) assertNull(TerminalProtocolDiagnostic.forHostExit(code, valid))
        assertNull(TerminalProtocolDiagnostic.forHostExit(125, null))
        assertNull(TerminalProtocolDiagnostic.forHostExit(125, valid.replace("<ESC>", "\u001b")))
        assertNull(TerminalProtocolDiagnostic.forHostExit(125, "x".repeat(10241)))
    }

    @Test fun validatesV2DetectionMetadataAgainstTheExactBytes() {
        val extended = valid.replace("TL-DIAG/1", "TL-DIAG/2") +
            "detected_at=2:4\nlast_byte=68\nlast_codepoint=U+0068\ncore_revision=" + "a".repeat(40) +
            "\nsource_dirty=true\noutput_profile=0\nwidth_profile=1\ndirection=output\ncore_impact=failed_closed\n" +
            "rejected_parameter_index=0\nrejected_parameter=9999\n"
        assertEquals(extended, assertNotNull(TerminalProtocolDiagnostic.parse(extended)).text)
        for (invalid in listOf(
            extended.replace("detected_at=2:4", "detected_at=2:3"),
            extended.replace("last_byte=68", "last_byte=67"),
            extended.replace("U+0068", "U+9999"), extended.replace("width_profile=1", "width_profile=999"),
            extended.replace("core_impact=failed_closed", "core_impact=network_disconnected"),
            extended.replace("rejected_parameter=9999", "rejected_parameter=999"),
            extended.replace("rejected_parameter_index=0", "rejected_parameter_index=1"),
        )) assertNull(TerminalProtocolDiagnostic.parse(invalid))
    }

    @Test fun rejectsRawControlsUnknownVersionsAndAmbiguousFields() {
        listOf(valid.replace("<ESC>", "\u001b"), valid.replace("TL-DIAG/1", "TL-DIAG/2"),
            valid + "reason=other\n", valid.replace("start=1:2", "start=-1:2"),
            valid.replace("hex=1B", "hex=GG"), valid + "x".repeat(4096),
            valid.replace("observed_bytes=8", "observed_bytes=7"),
            valid.replace("hidden_bytes=0", "hidden_bytes=18446744073709551615"),
            valid.replace("end=2:5", "end=0:5"),
            valid.replace("start=1:2", "start=1:4294967296"),
            valid.replace("sequence=<ESC>[?9999h", "sequence=something else"),
            valid.replace("private mode", "private\u202emode"), valid.replace("category=unsupported", "category=unknown")
        ).forEach { assertNull(TerminalProtocolDiagnostic.parse(it)) }
    }

    @Test fun validatesMacroCoordinatesSeparatelyFromSourceEventCoordinates() {
        val common = "detected_at=2:4\nlast_byte=68\nlast_codepoint=U+0068\ncore_revision=" + "a".repeat(40) +
            "\nsource_dirty=true\noutput_profile=0\nwidth_profile=1\ndirection=output\ncore_impact=failed_closed\n" +
            "rejected_parameter_index=0\nrejected_parameter=9999\n"
        val outer = valid.replace("TL-DIAG/1", "TL-DIAG/3") + common
        val inner = (valid.substringAfter('\n') + common).replace("start=1:2", "start=5:2")
            .replace("end=2:5", "end=5:10").replace("detected_at=2:4", "detected_at=5:9")
        val expanded = outer + "expanded_source=macro\n" + inner.lineSequence().filter { it.isNotEmpty() }
            .joinToString("\n", postfix = "\n") { "expanded_$it" }
        assertEquals(expanded, assertNotNull(TerminalProtocolDiagnostic.parse(expanded)).text)
        val mixed = expanded.replace("expanded_source=macro\n", "expanded_source=mixed\n" +
            "expanded_origins=macro,5,2,3;output,12,0,5\nexpanded_origin_omitted_bytes=0\n")
            .replace("expanded_start=5:2", "expanded_start=0:0").replace("expanded_end=5:10", "expanded_end=0:8")
            .replace("expanded_detected_at=5:9", "expanded_detected_at=0:7")
        assertEquals(mixed, assertNotNull(TerminalProtocolDiagnostic.parse(mixed)).text)
        for (invalid in listOf(mixed.replace("output,12,0,5", "output,12,0,4"),
            mixed.replace("macro,5,2,3", "macro,64,2,3"), mixed.replace("output,12,0,5", "output,12,4294967295,5"),
            mixed.replace("omitted_bytes=0", "omitted_bytes=1"), mixed + "expanded_origins=macro,1,0,8\n",
            mixed.replace("expanded_source=mixed", "expanded_source=macro"),
            expanded.replace("expanded_source_dirty=true", "expanded_source_dirty=false")))
            assertNull(TerminalProtocolDiagnostic.parse(invalid))
        for (invalid in listOf(expanded + "expanded_source=macro\n", expanded.replace("expanded_source=macro", "expanded_source=output"),
            expanded.replace("expanded_start=5:2", "expanded_start=6:2"), expanded.replace("expanded_end=5:10", "expanded_end=5:262145"),
            expanded.replace("expanded_reason=private mode", "expanded_reason=wrong error"))) assertNull(TerminalProtocolDiagnostic.parse(invalid))
    }
}
