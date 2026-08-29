package dev.termlink.product

import dev.termlink.session.OrderedRemoteSessionNotification
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RendererBootstrapState
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.nativecore.NativeSourceEvent
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalCoreShadowObserverTest {
    @Test
    fun missingJniCannotBlockProductSessionConstruction() = runBlocking {
        val observer = createJniTerminalCoreShadowObserver(
            RendererBootstrapState.Empty(),
            TerminalSize(80, 24),
        )
        assertNotNull(observer)
        val failure = try {
            observer!!.observe(applied(1, RemoteSessionEvent.Output(1, byteArrayOf(1))))
            null
        } catch (error: Throwable) {
            error
        }
        assertNotNull(failure)
        observer!!.close()
    }

    @Test
    fun portableBootstrapRegistersDormantObserverWithoutLoadingJni() {
        assertNotNull(
            createJniTerminalCoreShadowObserver(
                RendererBootstrapState.FullPrefix(sequence = 20, rendererGeneration = 2),
                TerminalSize(80, 24),
            ),
        )
    }

    @Test
    fun emptyRendererStartsAtRemoteSequenceOneAndMapsEveryOrderedEvent() = runBlocking {
        val factory = RecordingFactory()
        val observer = TerminalCoreShadowObserver(
            RendererBootstrapState.Empty(rendererGeneration = 4),
            TerminalSize(120, 30),
            factory,
        )
        assertTrue(factory.terminals.isEmpty())

        observer.observe(applied(4, RemoteSessionEvent.Output(1, byteArrayOf(1, 2))))
        observer.observe(applied(4, RemoteSessionEvent.Title(2, "pwsh")))
        observer.observe(applied(4, RemoteSessionEvent.Resize(3, TerminalSize(140, 40))))
        observer.observe(applied(4, RemoteSessionEvent.Lifecycle(4, RemoteLifecycle.RUNNING)))
        observer.observe(applied(4, RemoteSessionEvent.Gap(5, 1, 4)))

        val terminal = factory.terminals.single()
        assertEquals(TerminalSize(120, 30), terminal.createdSize)
        assertEquals(1L, terminal.initialNextSourceSequence)
        val output = terminal.events[0] as NativeSourceEvent.Output
        assertArrayEquals(byteArrayOf(1, 2), output.copyBytes())
        assertTrue(terminal.events[1] is NativeSourceEvent.Advance)
        assertEquals(TerminalSize(140, 40), (terminal.events[2] as NativeSourceEvent.Resize).size)
        assertTrue(terminal.events[3] is NativeSourceEvent.Advance)
        assertTrue(terminal.events[4] is NativeSourceEvent.Advance)
        observer.close()
        assertTrue(terminal.closed)
    }

    @Test
    fun portableBootstrapStaysDormantUntilCompleteRendererReplay() = runBlocking {
        val factory = RecordingFactory()
        val observer = TerminalCoreShadowObserver(
            RendererBootstrapState.ExactCheckpoint(
                sequence = 50,
                rendererGeneration = 7,
                snapshotId = java.util.UUID.randomUUID(),
                ciphertextSha256 = ByteArray(32),
            ),
            TerminalSize(80, 24),
            factory,
        )

        observer.observe(applied(7, RemoteSessionEvent.Resize(51, TerminalSize(100, 35))))
        observer.observe(applied(7, RemoteSessionEvent.Output(52, byteArrayOf(9))))
        assertTrue(factory.terminals.isEmpty())

        observer.observe(OrderedRemoteSessionNotification.RendererReset(8))
        assertTrue(factory.terminals.isEmpty())
        observer.observe(applied(8, RemoteSessionEvent.Title(1, "fresh")))
        observer.observe(applied(8, RemoteSessionEvent.Output(2, byteArrayOf(2))))

        val terminal = factory.terminals.single()
        assertEquals(TerminalSize(100, 35), terminal.createdSize)
        assertEquals(listOf(1L, 2L), terminal.events.map(NativeSourceEvent::sourceSequence))
        observer.close()
    }

    @Test
    fun invalidationClosesCurrentCoreAndWaitsForTheNextReset() = runBlocking {
        val factory = RecordingFactory()
        val observer = TerminalCoreShadowObserver(
            RendererBootstrapState.Empty(rendererGeneration = 2),
            TerminalSize(80, 24),
            factory,
        )
        observer.observe(applied(2, RemoteSessionEvent.Output(1, byteArrayOf(9))))
        val first = factory.terminals.single()

        observer.observe(OrderedRemoteSessionNotification.RendererInvalidated(2))
        observer.observe(applied(2, RemoteSessionEvent.Output(2, byteArrayOf(1))))
        assertTrue(first.closed)
        assertEquals(1, first.events.size)

        observer.observe(OrderedRemoteSessionNotification.RendererReset(2))
        observer.observe(applied(2, RemoteSessionEvent.Output(1, byteArrayOf(2))))
        assertEquals(2, factory.terminals.size)
        assertEquals(1, factory.terminals.last().events.size)
        observer.close()
    }

    @Test
    fun generationMismatchFailsClosedWithoutTouchingProductionState() = runBlocking {
        val factory = RecordingFactory()
        val observer = TerminalCoreShadowObserver(
            RendererBootstrapState.Empty(rendererGeneration = 3),
            TerminalSize(80, 24),
            factory,
        )

        val failure = try {
            observer.observe(applied(4, RemoteSessionEvent.Output(1, byteArrayOf(1))))
            null
        } catch (error: Throwable) {
            error
        }
        assertTrue(failure is IllegalArgumentException)
        assertTrue(factory.terminals.isEmpty())
        observer.close()
        observer.close()
    }

    private fun applied(
        generation: Long,
        event: RemoteSessionEvent,
    ) = OrderedRemoteSessionNotification.Applied(generation, event)

    private class RecordingFactory : NativeShadowTerminalFactory {
        val terminals = mutableListOf<RecordingTerminal>()

        override fun create(size: TerminalSize, initialNextSourceSequence: Long): NativeShadowTerminal =
            RecordingTerminal(size, initialNextSourceSequence).also(terminals::add)
    }

    private class RecordingTerminal(
        val createdSize: TerminalSize,
        val initialNextSourceSequence: Long,
    ) : NativeShadowTerminal {
        val events = mutableListOf<NativeSourceEvent>()
        var closed = false

        override fun apply(event: NativeSourceEvent) {
            check(!closed)
            events += event
        }

        override fun close() {
            closed = true
        }
    }
}
