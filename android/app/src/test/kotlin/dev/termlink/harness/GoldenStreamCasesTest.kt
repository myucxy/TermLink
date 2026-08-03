package dev.termlink.harness

import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Test

class GoldenStreamCasesTest {
    @Test
    fun `local case catalog has stable unique ids and asset paths`() {
        assertEquals(10, GoldenStreamCases.local.size)
        assertEquals(10, GoldenStreamCases.local.map { it.id }.toSet().size)
        GoldenStreamCases.local.forEach { goldenCase ->
            assertEquals(
                "terminal-golden-streams/${goldenCase.id}",
                goldenCase.assetPath,
            )
        }
    }

    @Test
    fun `default and unknown selections preserve the smoke powershell baseline`() {
        assertEquals("smoke-powershell-v1", GoldenStreamCases.default.id)
        assertSame(GoldenStreamCases.default, GoldenStreamCases.lanFixed)
        assertSame(GoldenStreamCases.default, GoldenStreamCases.find("missing-case"))
    }
}
