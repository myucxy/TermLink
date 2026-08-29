package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertContentEquals

class TerminalOutputProfileTest {
    @Test
    fun outputProfilesKeepUtf8AsTheExplicitBaseline() {
        assertContentEquals(
            arrayOf(
                TerminalOutputProfile.UTF8,
                TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            ),
            TerminalOutputProfile.entries.toTypedArray(),
        )
    }
}
