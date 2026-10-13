package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import dev.termlink.terminal.api.TerminalHostEffectKindV2
import dev.termlink.terminal.api.TerminalHostEffectV2
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TerminalActionReceiptsTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private fun effect(seq: Long, offset: Int = 1, ordinal: Long = 0) = TerminalHostEffectV2(
        TerminalHostEffectKindV2.PLATFORM_ACTION, seq, offset, ordinal, byteArrayOf(1, 0, 0, 0))

    @Test fun watermarkSurvivesConsumerReplacementAndSeparatesAccountsAndSessions() {
        val id = UUID.randomUUID()
        val first = TerminalActionReceipts(context, "a", id)
        assertEquals(3, first.claim(listOf(effect(5), effect(5, 2), effect(5, 2, 1))).size)
        val replacement = TerminalActionReceipts(context, "a", id)
        assertTrue(replacement.claim(listOf(effect(4), effect(5, 2, 1))).isEmpty())
        assertEquals(1, replacement.claim(listOf(effect(5, 2, 1), effect(6))).size)
        assertEquals(1, TerminalActionReceipts(context, "b", id).claim(listOf(effect(1))).size)
        assertEquals(1, TerminalActionReceipts(context, "a", UUID.randomUUID()).claim(listOf(effect(1))).size)
    }

    @Test fun reorderedEffectsAreRejectedAndCannotExecuteLater() {
        val store = TerminalActionReceipts(context, "order", UUID.randomUUID())
        assertTrue(store.claim(listOf(effect(2), effect(1))).isEmpty())
        assertTrue(store.claim(listOf(effect(3))).isEmpty())
    }

    @Test fun twoConsumersCannotClaimTheSameAction() {
        val id = UUID.randomUUID()
        val a = TerminalActionReceipts(context, "race", id)
        val b = TerminalActionReceipts(context, "race", id)
        val results = java.util.concurrent.CopyOnWriteArrayList<Int>()
        val threads = listOf(a, b).map { store -> Thread { results.add(store.claim(listOf(effect(7))).size) } }
        threads.forEach(Thread::start); threads.forEach(Thread::join)
        assertEquals(1, results.sum())
    }
}
