package dev.termlink.product

import dev.termlink.terminal.api.TerminalViewportEngine
import java.lang.reflect.Proxy
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ShellIntegrationMenuTest {
    @Test
    fun preparesBeforeLocatingAndPreservesUnavailableResult() = runBlocking {
        val calls = CopyOnWriteArrayList<String>()
        var available = false
        val engine = Proxy.newProxyInstance(
            TerminalViewportEngine::class.java.classLoader,
            arrayOf(TerminalViewportEngine::class.java),
        ) { _, method, arguments ->
            require(method.name == "showShellCommand")
            assertEquals(42L, arguments?.single())
            calls += "show"
            available
        } as TerminalViewportEngine

        assertFalse(locateShellCommand(engine, 42L) { calls += "prepare" })
        assertEquals(listOf("prepare", "show"), calls)

        calls.clear()
        available = true
        assertTrue(locateShellCommand(engine, 42L) { calls += "prepare" })
        assertEquals(listOf("prepare", "show"), calls)

        calls.clear()
        assertFalse(locateShellCommand(null, 42L) { calls += "prepare" })
        assertTrue(calls.isEmpty())
    }
}
